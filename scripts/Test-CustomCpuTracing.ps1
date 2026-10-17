param([string]$EngineRoot = 'C:/Program Files/Epic Games/UE_5.8/Engine')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/custom-cpu-cpp/' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $runRoot 'source'; $stubs = Join-Path $runRoot 'stubs'
New-Item -ItemType Directory -Force $source,(Join-Path $stubs 'ProfilingDebugging'),(Join-Path $stubs 'Tracing') | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'examples/Tracing/ProjectCpuTrace.h') -Destination (Join-Path $stubs 'Tracing/ProjectCpuTrace.h')
@'
#include "Fixture.h"
void Derived::Work(int Mode) {
    Super::Work(Mode);
    if (Live != EXPECTED) ++Errors;
    if (Mode == 1) return;
    if (Mode == 2) throw 2;
    ++Completed;
}
'@ | Set-Content (Join-Path $source 'Fixture.cpp') -Encoding utf8
@'
#pragma once
#include <string_view>
inline int Entries=0, Exits=0, Live=0, Errors=0, Completed=0, BaseCalls=0;
struct Base { void Work(int) { if (Live) ++Errors; ++BaseCalls; } };
struct Derived : Base { using Super=Base; void Work(int Mode); };
int main() {
    Derived value; value.Work(0); value.Work(1);
    try { value.Work(2); return 1; } catch (int code) { if (code != 2) return 2; }
    return Errors || BaseCalls != 3 || Completed != 1 || Live || Entries != 3*EXPECTED || Exits != Entries;
}
'@ | Set-Content (Join-Path $stubs 'Fixture.h') -Encoding utf8
$engineHeader = Join-Path $EngineRoot 'Source/Runtime/Core/Public/ProfilingDebugging/CpuProfilerTrace.h'
function MacroDefinitions([bool]$Enabled) {
    $lines = [IO.File]::ReadAllLines($engineHeader)
    foreach ($name in @('TRACE_CPUPROFILER_EVENT_DECLARE','TRACE_CPUPROFILER_EVENT_SCOPE_USE','TRACE_CPUPROFILER_EVENT_SCOPE_STR_CONDITIONAL','TRACE_CPUPROFILER_EVENT_SCOPE_STR')) {
        $indices = @(for ($i=0; $i -lt $lines.Length; $i++) { if ($lines[$i] -match ('^#define\s+' + $name + '\(')) { $i } })
        if (!$indices.Count) { throw "Engine macro missing: $name" }
        $index = if ($Enabled) { $indices[0] } else { $indices[-1] }
        do { $line = $lines[$index++]; $line } while ($line.TrimEnd().EndsWith('\'))
    }
}
$backend = @'
#pragma once
#define UE_JOIN_IMPL(a,b) a##b
#define UE_JOIN(a,b) UE_JOIN_IMPL(a,b)
using uint32=unsigned int;
struct FCpuProfilerTrace {
    struct FEventScope {
        FEventScope(uint32& id, const char* name, bool enabled, const char*, uint32) {
            if (!enabled || std::string_view(name) != "Derived Work") ++Errors;
            id=1; ++Entries; ++Live;
        }
        ~FEventScope() { ++Exits; --Live; }
    };
};
'@
@($backend,'#if TEST_CPU_ENABLED',(MacroDefinitions $true),'#else',(MacroDefinitions $false),'#endif') |
    Set-Content (Join-Path $stubs 'ProfilingDebugging/CpuProfilerTrace.h') -Encoding utf8
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
$output = Join-Path $runRoot 'preview'
dotnet $cli cpu $source --all-functions --cpu-macro-name PROJECT_TRACE_CPU_SCOPE_STR --cpu-header Tracing/ProjectCpuTrace.h --report $output
if ($LASTEXITCODE -ne 0) { throw 'Custom CPU preview failed.' }
git -C $source apply --check (Join-Path $output 'changes.patch')
if ($LASTEXITCODE -ne 0) { throw 'Custom CPU patch check failed.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'MSVC x64 tools not found.' }
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64' -or !(Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    Import-Module (Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
}
foreach ($config in @('DEBUG','DEVELOPMENT','TEST','SHIPPING')) { foreach ($enabled in @(0,1)) {
    $expected = if ($enabled -and $config -in @('DEVELOPMENT','TEST')) { 1 } else { 0 }
    $flags = @(foreach ($name in @('DEBUG','DEVELOPMENT','TEST','SHIPPING')) { "/DUE_BUILD_${name}=" + [int]($name -eq $config) })
    $tag = "$config-$enabled"; $exe = Join-Path $runRoot "$tag.exe"
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /utf-8 @flags "/DTEST_CPU_ENABLED=$enabled" "/DEXPECTED=$expected" "/I$stubs" "/Fo$(Join-Path $runRoot "$tag.obj")" "/Fe$exe" (Join-Path $output 'modified/Fixture.cpp')
    if ($LASTEXITCODE -ne 0) { throw "Custom CPU compilation failed: $tag" }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw "Custom CPU runtime failed: $tag ($LASTEXITCODE)" }
    Write-Output "Custom CPU PASS ($tag): Development/Test gating, fixed name, post-Super lifetime, return and exception"
} }
Write-Output "Artifacts: $runRoot"
