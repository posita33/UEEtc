param(
    [string]$LyraSource = 'D:/Projects/posita/UE/R_A_D/LyraStarterGame/Source',
    [string]$BlockBreakerSource = 'D:/Projects/posita/git/Game/BlockBreaker/Source',
    [switch]$RecommendedAdd
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/event-experiments/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
[string[]]$quantityFlags = @(); if ($RecommendedAdd) { $quantityFlags = @('--recommended-add') }
function Manifest([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName; Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
foreach ($target in @(@{Name='lyra';Root=$LyraSource}, @{Name='blockbreaker';Root=$BlockBreakerSource})) {
    $before = Manifest $target.Root
    $discovery = Join-Path $runRoot ($target.Name + '-discovery')
    dotnet $cli counters $target.Root --report $discovery
    if ($LASTEXITCODE -ne 0) { throw 'Discovery failed.' }
    $candidates = @(Get-Content (Join-Path $discovery 'candidates.json') -Raw -Encoding utf8 | ConvertFrom-Json)
    $selection = Get-Content (Join-Path $discovery 'selection.json') -Raw -Encoding utf8 | ConvertFrom-Json
    # Stress-test all recognized sites on generated copies only. This is not a recommended gameplay selection.
    $selection.Selections = @($candidates | ForEach-Object { [pscustomobject]@{ Id=$_.Id; CounterName=$_.SuggestedName; DisplayName=$_.DisplayName } })
    $selectionPath = Join-Path $runRoot ($target.Name + '-test-selection.json')
    $selection | ConvertTo-Json -Depth 6 | Set-Content $selectionPath -Encoding utf8
    $output = Join-Path $runRoot ($target.Name + '-selected')
    dotnet $cli counters $target.Root --selection $selectionPath @quantityFlags --report $output
    if ($LASTEXITCODE -ne 0) { throw 'Selected preview failed.' }
    git -C $target.Root apply --check (Join-Path $output 'changes.patch')
    if ($LASTEXITCODE -ne 0) { throw 'Patch check failed.' }
    $second = Join-Path $runRoot ($target.Name + '-second')
    dotnet $cli counters (Join-Path $output 'modified') --report $second
    if ($LASTEXITCODE -ne 0) { throw 'Reanalysis failed.' }
    $secondCandidates = @(Get-Content (Join-Path $second 'candidates.json') -Raw -Encoding utf8 | ConvertFrom-Json)
    if ($secondCandidates.Count -ne 0) { throw 'Selected sites were rediscovered.' }
    $after = Manifest $target.Root
    if (@(Compare-Object $before $after -Property Path,Hash).Count -ne 0) { throw 'Original sources changed.' }
    $plan = Get-Content (Join-Path $output 'report.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $summary = [pscustomobject]@{
        Target=$target.Name; Files=$plan.Files.Count; Candidates=$candidates.Count; AddedEvents=$plan.AddedEventCounters
        ChangedFiles=$plan.ChangedFiles; Diagnostics=$plan.DiagnosticCount
        QuantityRecommendations=@($candidates | Where-Object { $null -ne $_.RecommendedQuantity }).Count
        AddedQuantityCounters=$plan.AddedQuantityCounters
        SecondPassCandidates=$secondCandidates.Count; OriginalFiles=$before.Count; OriginalChanges=0
    }
    $summary | ConvertTo-Json | Set-Content (Join-Path $runRoot ($target.Name + '-summary.json')) -Encoding utf8
    $summary | ConvertTo-Json
}
Write-Output "Artifacts: $runRoot"
