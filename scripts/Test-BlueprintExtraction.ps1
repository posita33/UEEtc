param([string]$Editor = 'C:/Program Files/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/blueprint-integration/' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $runRoot 'Project 日本語 Space'
New-Item -ItemType Directory -Force $projectRoot | Out-Null
$project = Join-Path $projectRoot 'Fixture.uproject'
# Prove that command-line plugin enabling works without editing the project descriptor.
@'
{"FileVersion":3,"EngineAssociation":"5.8","Plugins":[{"Name":"PythonScriptPlugin","Enabled":false},{"Name":"EditorScriptingUtilities","Enabled":false}]}
'@ | Set-Content $project -Encoding utf8
$create = Join-Path $runRoot 'create-fixture.py'
@'
import unreal
factory = unreal.BlueprintFactory()
factory.set_editor_property("parent_class", unreal.Actor)
bp = unreal.AssetToolsHelpers.get_asset_tools().create_asset("BP_Fixture", "/Game", unreal.Blueprint, factory)
library = unreal.BlueprintEditorLibrary
assert library.add_member_variable(bp, "Count", library.get_basic_type_by_name("int"))
assert library.add_member_variable(bp, "Origin", library.get_struct_type(unreal.load_object(None, "/Script/CoreUObject.Vector")))
library.add_function_graph(bp, "ComputeCount")
library.compile_blueprint(bp)
defaults = unreal.get_default_object(bp.generated_class())
defaults.set_editor_property("count", 42)
defaults.set_editor_property("origin", unreal.Vector(10, 20, 30))
unreal.EditorAssetLibrary.save_loaded_asset(bp)
material = unreal.AssetToolsHelpers.get_asset_tools().create_asset("M_NotBlueprint", "/Game", unreal.Material, unreal.MaterialFactoryNew())
unreal.EditorAssetLibrary.save_loaded_asset(material)
'@ | Set-Content $create -Encoding utf8
& $Editor $project.Replace('\','/') -run=pythonscript ('-script=' + $create.Replace('\','/')) '-EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities' -unattended -NullRHI -nosound -NoSplash -NoP4 -stdout -UTF8Output *> (Join-Path $runRoot 'create.log')
if ($LASTEXITCODE -ne 0) { throw "Blueprint fixture creation failed. See $runRoot/create.log" }
$asset = Join-Path $projectRoot 'Content/BP_Fixture.uasset'
$material = Join-Path $projectRoot 'Content/M_NotBlueprint.uasset'
$before = @($asset,$project,$material) | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 }
$cli = Join-Path $repoRoot 'src/UEEtc.Blueprint.Console/bin/Release/net10.0/UEEtc.Blueprint.Console.dll'
$tests = Join-Path $repoRoot 'tests/UEEtc.Blueprint.Tests/bin/Release/net10.0/UEEtc.Blueprint.Tests.dll'
$oldTmp = $env:TMP; $oldTemp = $env:TEMP
try {
    # Exercise the Engine's quoted -script parser, not just file IO with ASCII paths.
    $env:TMP = Join-Path $runRoot 'Temp 日本語 Space'; $env:TEMP = $env:TMP
    New-Item -ItemType Directory -Force $env:TMP | Out-Null
    foreach ($keep in @($false,$true)) {
        $output = Join-Path $runRoot $(if ($keep) { 'Keep Layout' } else { 'Remove Layout' })
        $flags = @(if ($keep) { '--keep-layout' })
        & dotnet $cli $asset $project $Editor $output @flags
        if ($LASTEXITCODE -ne 0) { throw "Real Blueprint analysis failed (keep=$keep)." }
        $reportFolder = Get-ChildItem -LiteralPath $output -Directory | Select-Object -First 1
        $report = Get-Content (Join-Path $reportFolder.FullName 'analysis.json') -Raw | ConvertFrom-Json
        $blueprint = $report.Documents | Where-Object Name -eq Blueprint
        $defaults = $report.Documents | Where-Object Name -eq Defaults
        if ($blueprint.Graphs.Count -lt 2 -or $defaults.Objects[0].Properties.Count -ne '42' -or $defaults.Objects[0].Properties.Origin -notmatch 'X=10') { throw 'Graph/variable/default extraction was incomplete.' }
        if ($keep -and $blueprint.RemovedLayoutProperties -ne 0) { throw 'Keep-layout changed coordinates.' }
        if (!$keep -and $blueprint.RemovedLayoutProperties -eq 0) { throw 'Layout was not removed.' }
        Write-Output "PASS real Blueprint: graph, variables, CDO Count=42/Origin vector, keep-layout=$keep"
    }
    & dotnet $cli $material $project $Editor (Join-Path $runRoot 'Unsupported')
    if ($LASTEXITCODE -eq 0) { throw 'Non-Blueprint material was incorrectly accepted.' }
    Write-Output 'PASS non-Blueprint rejection'
    & dotnet $tests --cancel-editor $Editor $project
    if ($LASTEXITCODE -ne 0) { throw 'Real Editor cancellation failed.' }
    foreach ($entry in $before) {
        if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Input changed: $($entry.Path)" }
    }
    Write-Output 'PASS asset/material/uproject SHA256 unchanged'
}
finally { $env:TMP = $oldTmp; $env:TEMP = $oldTemp }
Write-Output "Artifacts: $runRoot"
