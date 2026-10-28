param([string]$EngineRoot = 'C:/Program Files/Epic Games/UE_5.8')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$run = Join-Path $repo ('artifacts/generated-cpp-validation/' + [Guid]::NewGuid().ToString('N'))
& dotnet run --project (Join-Path $repo 'tests/UEEtc.Blueprint.Cpp.Tests') -c Release -- --emit-plugin $run
if ($LASTEXITCODE -ne 0) { throw 'Converter regression tests failed.' }
$plugin = Join-Path $run 'BlueprintConversion'
@'
#if WITH_DEV_AUTOMATION_TESTS
#include "ConvertedBlueprint.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGeneratedCounterTest, "UEEtc.GeneratedBlueprint.Counter", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FGeneratedCounterTest::RunTest(const FString& Parameters)
{
    const UWorld::InitializationValues Values = UWorld::InitializationValues().AllowAudioPlayback(false).CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false).ShouldSimulatePhysics(false);
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &Values);
    AConverted_BP_Counter* Pawn = World->SpawnActor<AConverted_BP_Counter>();
    if (!TestNotNull(TEXT("Generated class can spawn"), Pawn)) { World->DestroyWorld(false); return false; }
    Pawn->Fn_Increment();
    TestEqual(TEXT("First increment"), Pawn->Var_Count, 1);
    TestTrue(TEXT("First true branch"), Pawn->Var_Accepted);
    Pawn->Fn_Increment();
    TestEqual(TEXT("Second increment"), Pawn->Var_Count, 2);
    TestTrue(TEXT("Second true branch"), Pawn->Var_Accepted);
    Pawn->Fn_Increment();
    TestEqual(TEXT("Third increment"), Pawn->Var_Count, 3);
    TestFalse(TEXT("False branch after threshold"), Pawn->Var_Accepted);
    Pawn->Var_Count = MAX_int32;
    Pawn->Fn_Increment();
    TestEqual(TEXT("Integer arithmetic wraps using unsigned operations"), Pawn->Var_Count, MIN_int32);
    World->DestroyWorld(false);
    return true;
}
#endif
'@ | Set-Content -LiteralPath (Join-Path $plugin 'Source/BlueprintConversion/Private/GeneratedTests.cpp') -Encoding utf8
$hostDirectory = Join-Path $run 'HostProject'
New-Item -ItemType Directory -Path $hostDirectory | Out-Null
$project = Join-Path $hostDirectory 'HostProject.uproject'
# Isolate the fixture from optional Engine plugins (for example a locally installed Fab with missing build rules).
@'
{"FileVersion":3,"DisableEnginePluginsByDefault":true,"AdditionalPluginDirectories":[".."],"Plugins":[{"Name":"BlueprintConversion","Enabled":true}]}
'@ | Set-Content -LiteralPath $project -Encoding utf8
& (Join-Path $EngineRoot 'Engine/Build/BatchFiles/Build.bat') UnrealEditor Win64 Development "-Project=$project" "-plugin=$plugin/BlueprintConversion.uplugin" -NoHotReload -NoUBA
if ($LASTEXITCODE -ne 0) { throw 'Generated C++ UHT/compiler validation failed.' }
$report = Join-Path $run 'AutomationReport'
& (Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe') $project.Replace('\','/') -unattended -NullRHI -nosound -NoSplash -NoP4 -stdout '-ExecCmds=Automation RunTests UEEtc.GeneratedBlueprint' '-TestExit=Automation Test Queue Empty' "-ReportExportPath=$report" *> (Join-Path $run 'automation.log')
if ($LASTEXITCODE -ne 0) { throw 'Generated C++ runtime automation failed.' }
$index = Get-Content -LiteralPath (Join-Path $report 'index.json') -Raw | ConvertFrom-Json
if (($index.succeeded + $index.succeededWithWarnings) -lt 1 -or $index.failed -ne 0) { throw 'Missing or failed generated-code tests.' }
Write-Output "PASS generated plugin UHT/C++ build and runtime branch/arithmetic behavior: $run"
