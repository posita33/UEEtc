param(
    [string]$LyraSource = 'D:/Projects/posita/UE/R_A_D/LyraStarterGame/Source',
    [string]$BlockBreakerSource = 'D:/Projects/posita/git/Game/BlockBreaker/Source'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/reviewed-events/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
$targets = @(
    @{Name='blockbreaker'; Root=$BlockBreakerSource; Rules=@(
        @{Function='ABlockBreakerGameMode::SpawnNewBall'; Condition='!BallClass || !PlayerController'; Branch='If'; Name='BallSpawnInvalidArguments'; Display='BlockBreaker/Ball/InvalidSpawnArguments'},
        @{Function='ABlockBreakerGameMode::SpawnNewBall'; Condition='ABall* NewBall = GetWorld()->SpawnActor<ABall>(BallClass, SpawnTransform, SpawnParams)'; Branch='If'; Name='BallSpawnSucceeded'; Display='BlockBreaker/Ball/SpawnSucceeded'},
        @{Function='ABlockBreakerGameMode::OnBrickDestroyedEx'; Condition='Before == DestructibleBricksCount'; Branch='If'; Name='BrickRemovalFallbacks'; Display='BlockBreaker/Bricks/RemovalFallbacks'},
        @{Function='ABlockBreakerGameMode::CheckClearCondition'; Condition='bShouldClear'; Branch='If'; Name='RoundClearDecisions'; Display='BlockBreaker/Round/ClearDecisions'},
        @{Function='ABlockBreakerGameMode::PlayerDied'; Condition='RemainingLives > 0'; Branch='Else'; Name='PlayerDeathGameOverDecisions'; Display='BlockBreaker/Lives/GameOverDecisions'}
    )},
    @{Name='lyra'; Root=$LyraSource; Rules=@(
        @{Function='ULyraGameplayAbility::DoesAbilitySatisfyTagRequirements'; Condition='bBlocked'; Branch='If'; Name='AbilityTagBlockedRejections'; Display='Lyra/Ability/TagBlockedRejections'},
        @{Function='ULyraGameplayAbility::DoesAbilitySatisfyTagRequirements'; Condition='bMissing'; Branch='If'; Name='AbilityTagMissingRejections'; Display='Lyra/Ability/TagMissingRejections'}
    )}
)
foreach ($target in $targets) {
    $before = @(Get-ChildItem -LiteralPath $target.Root -File -Recurse | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    $discovery = Join-Path $runRoot ($target.Name + '-discovery')
    dotnet $cli counters $target.Root --report $discovery
    if ($LASTEXITCODE -ne 0) { throw 'Discovery failed.' }
    $candidates = @(Get-Content (Join-Path $discovery 'candidates.json') -Raw -Encoding utf8 | ConvertFrom-Json)
    $selection = Get-Content (Join-Path $discovery 'selection.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $selection.Selections = @(foreach ($rule in $target.Rules) {
        $found = @($candidates | Where-Object { $_.Function -eq $rule.Function -and $_.Condition -eq $rule.Condition -and $_.Branch -eq $rule.Branch })
        if ($found.Count -ne 1) { throw "Source changed; review the example again: $($rule.Name)" }
        [pscustomobject]@{Id=$found[0].Id; CounterName=$rule.Name; DisplayName=$rule.Display}
    })
    $selectionPath = Join-Path $runRoot ($target.Name + '-reviewed.json')
    $selection | ConvertTo-Json -Depth 6 | Set-Content $selectionPath -Encoding utf8
    $output = Join-Path $runRoot ($target.Name + '-preview')
    dotnet $cli counters $target.Root --selection $selectionPath --report $output
    if ($LASTEXITCODE -ne 0) { throw 'Preview failed.' }
    git -C $target.Root apply --check (Join-Path $output 'changes.patch')
    if ($LASTEXITCODE -ne 0) { throw 'Patch check failed.' }
    $after = @(Get-ChildItem -LiteralPath $target.Root -File -Recurse | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    if (@(Compare-Object $before $after -Property Path,Hash).Count -ne 0) { throw 'Original sources changed.' }
}
Write-Output "Reviewed examples exported; source unchanged: $runRoot"
