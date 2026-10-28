param(
    [Parameter(Mandatory)][string]$CropoutProject,
    [Parameter(Mandatory)][string]$BuiltPlugin,
    [string]$EngineRoot = 'C:/Program Files/Epic Games/UE_5.8'
)
$ErrorActionPreference = 'Stop'
$sourceProject = (Resolve-Path -LiteralPath $CropoutProject).Path
$sourceRoot = Split-Path -Parent $sourceProject
$pluginRoot = Split-Path -Parent (Resolve-Path -LiteralPath $BuiltPlugin).Path
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo ('artifacts/villager-cropout-copy/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
# Snapshot all project source assets/configs, not just BP_Villager.
$sourceFiles = @($sourceProject)
foreach ($name in @('Content','Config')) {
    $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $sourceRoot $name) -File -Recurse | Select-Object -ExpandProperty FullName
}
$before = $sourceFiles | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 }
foreach ($name in @('Content','Config','Plugins')) {
    $path = Join-Path $sourceRoot $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $output $name) -Recurse }
}
$copyProject = Join-Path $output 'CropoutCopy.uproject'
Copy-Item -LiteralPath $sourceProject -Destination $copyProject
$copyPlugin = Join-Path $output 'Plugins/CropoutVillager'
if (Test-Path -LiteralPath $copyPlugin) { throw 'Source already contains a CropoutVillager plugin; refusing to overwrite.' }
if (!(Test-Path -LiteralPath (Join-Path $pluginRoot 'Binaries/Win64/UnrealEditor-CropoutVillager.dll'))) { throw 'A built Editor plugin is required.' }
New-Item -ItemType Directory -Path $copyPlugin -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $pluginRoot 'CropoutVillager.uplugin') -Destination $copyPlugin
foreach ($name in @('Source','Binaries','Content','Config','Resources','Scripts')) {
    $path = Join-Path $pluginRoot $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $copyPlugin $name) -Recurse }
}
$descriptor = Join-Path $copyPlugin 'CropoutVillager.uplugin'
$editor = Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
foreach ($script in @('prepare_assets','verify_prepared_assets')) {
    $scriptPath = Join-Path $repo "examples/BlueprintConversion/CropoutVillager/Scripts/$script.py"
    $log = Join-Path $output "$script.log"
    $fullLog = Join-Path $output "$script-full.log"
    & $editor $copyProject.Replace('\','/') -run=pythonscript ('-script=' + $scriptPath.Replace('\','/')) "-PLUGIN=$($descriptor.Replace('\','/'))" '-EnablePlugins=CropoutVillager,PythonScriptPlugin,EditorScriptingUtilities' -unattended -NullRHI -nosound -NoSplash -NoP4 -NoAutoSave -stdout "-abslog=$($fullLog.Replace('\','/'))" *> $log
    # Cropout's legacy CVar can set exit code 1 after the script succeeds. Require our final completion marker.
    $marker = if ($script -eq 'prepare_assets') { 'UEETC_NATIVE_VILLAGER_PREPARED:' } else { 'UEETC_NATIVE_VILLAGER_VERIFIED:' }
    if ($LASTEXITCODE -notin @(0,1) -or !(Select-String -LiteralPath $fullLog -SimpleMatch $marker -Quiet)) { throw "Setup verification failed: $fullLog" }
}
foreach ($entry in $before) {
    if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Original input changed: $($entry.Path)" }
}
Write-Output "PASS native BP/table preparation, separate-process reload, original content/config SHA256 unchanged: $output"
