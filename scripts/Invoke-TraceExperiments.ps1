param(
    [string]$LyraSource = 'D:/Projects/posita/UE/R_A_D/LyraStarterGame/Source',
    [string]$BlockBreakerSource = 'D:/Projects/posita/git/Game/BlockBreaker/Source',
    [ValidateSet('cpu', 'counter', 'both')][string]$Mode = 'cpu',
    [switch]$Focused,
    [ValidateSet('auto','scope','str')][string]$CpuMacro = 'auto'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/trace-experiments/run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
[string[]]$modeFlags = @(switch ($Mode) { 'counter' { '--counter-only' } 'both' { '--both' } })
$modeFlags += @('--cpu-macro', $CpuMacro)
if ($Focused -and $Mode -ne 'cpu') { throw 'Focused is supported only with cpu mode.' }
[string[]]$commandPrefix = @(); if ($Focused) { $commandPrefix = @('cpu') }

function Get-SourceManifest([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName; Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}

dotnet build (Join-Path $repoRoot 'UEEtc.sln') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
foreach ($target in @(@{ Name = 'lyra'; Root = $LyraSource }, @{ Name = 'blockbreaker'; Root = $BlockBreakerSource })) {
    $before = Get-SourceManifest $target.Root
    $before | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $runRoot ($target.Name + '-before.json')) -Encoding utf8
    $output = Join-Path $runRoot $target.Name
    dotnet run --project (Join-Path $repoRoot 'src/UEEtc.Console') --configuration Release --no-build -- @commandPrefix $target.Root @modeFlags --report $output |
        Set-Content (Join-Path $runRoot ($target.Name + '.log')) -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Analysis failed: $($target.Name)" }
    git -C $target.Root apply --check (Join-Path $output 'changes.patch')
    if ($LASTEXITCODE -ne 0) { throw "Review patch check failed: $($target.Name)" }
    $secondOutput = Join-Path $runRoot ($target.Name + '-second-pass')
    dotnet run --project (Join-Path $repoRoot 'src/UEEtc.Console') --configuration Release --no-build -- @commandPrefix (Join-Path $output 'modified') @modeFlags --report $secondOutput |
        Set-Content (Join-Path $runRoot ($target.Name + '-second-pass.log')) -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Second analysis failed: $($target.Name)" }
    $secondPlan = Get-Content (Join-Path $secondOutput 'report.json') -Raw -Encoding utf8 | ConvertFrom-Json
    if ($secondPlan.ChangedFiles -ne 0) { throw "Idempotence check failed: $($target.Name)" }
    $after = Get-SourceManifest $target.Root
    $difference = @(Compare-Object $before $after -Property Path,Hash)
    if ($difference.Count -ne 0) { throw "Source integrity check failed: $($target.Name)" }
    $report = Get-Content (Join-Path $output 'report.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $counters = @($report.Files.Functions | Where-Object { $_.Status -eq 'Add' -and $_.Counter } | Select-Object -ExpandProperty Counter -Unique)
    $hashedCounters = @($counters | Where-Object { $_ -match '_CallCount_[0-9A-F]{16}(?:_\d+)?$' })
    [pscustomobject]@{
        Name = $target.Name; Mode = $Mode; Focused = [bool]$Focused; CpuMacro = $CpuMacro; SourceFiles = $before.Count; CppFiles = $report.Files.Count
        ChangedFiles = $report.ChangedFiles; AddedFunctions = $report.AddedFunctions
        SkippedFunctions = $report.SkippedFunctions; Diagnostics = $report.DiagnosticCount
        SecondPassChanges = $secondPlan.ChangedFiles; OriginalFileChanges = $difference.Count
        UniqueGeneratedCounters = $counters.Count; HashedCounters = $hashedCounters.Count
        AddedCpuScopes = $report.AddedCpuScopes; AddedCallCounters = $report.AddedCallCounters
        AddedPlainCpuScopes = $report.AddedPlainCpuScopes; AddedStringCpuScopes = $report.AddedStringCpuScopes
    } | ConvertTo-Json | Set-Content (Join-Path $runRoot ($target.Name + '-summary.json')) -Encoding utf8
    Get-Content (Join-Path $runRoot ($target.Name + '-summary.json')) -Encoding utf8
}
Write-Output "Artifacts: $runRoot"
