param(
    [string]$LyraSource = 'D:/Projects/posita/UE/R_A_D/LyraStarterGame/Source',
    [string]$BlockBreakerSource = 'D:/Projects/posita/git/Game/BlockBreaker/Source'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/combined-experiments/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
function Manifest([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName; Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
foreach ($target in @(@{Name='lyra';Root=$LyraSource}, @{Name='blockbreaker';Root=$BlockBreakerSource})) {
    $before = Manifest $target.Root
    $output = Join-Path $runRoot $target.Name
    dotnet $cli combined $target.Root --report $output | Set-Content (Join-Path $runRoot ($target.Name + '-analysis.log')) -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Combined analysis failed.' }
    git -C $target.Root apply --check (Join-Path $output 'changes.patch')
    if ($LASTEXITCODE -ne 0) { throw 'Combined patch check failed.' }
    $second = Join-Path $runRoot ($target.Name + '-second')
    dotnet $cli combined (Join-Path $output 'modified') --report $second | Set-Content (Join-Path $runRoot ($target.Name + '-second.log')) -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Combined reanalysis failed.' }
    $plan = Get-Content (Join-Path $output 'report.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $reanalysis = Get-Content (Join-Path $second 'report.json') -Raw -Encoding utf8 | ConvertFrom-Json
    if ($reanalysis.ChangedFiles -ne 0) { throw 'Combined reanalysis produced duplicate additions.' }
    $after = Manifest $target.Root
    if (@(Compare-Object $before $after -Property Path,Hash).Count -ne 0) { throw 'Original sources changed.' }
    $summary = [pscustomobject]@{
        Target=$target.Name; Files=$plan.Files.Count; ChangedFiles=$plan.ChangedFiles
        Cpu=$plan.AddedCpuScopes; Events=$plan.AddedEventCounters; Decrements=$plan.AddedDecrements; Add=$plan.AddedQuantityCounters
        Diagnostics=$plan.DiagnosticCount; SecondPassChanges=$reanalysis.ChangedFiles; OriginalFiles=$before.Count; OriginalChanges=0
    }
    $summary | ConvertTo-Json | Set-Content (Join-Path $runRoot ($target.Name + '-summary.json')) -Encoding utf8
    $summary | ConvertTo-Json
}
Write-Output "Artifacts: $runRoot"
