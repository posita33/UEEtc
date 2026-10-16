param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/event-cpp/' + [guid]::NewGuid().ToString('N'))
$sourceDir = Join-Path $runRoot 'source'
$stubDir = Join-Path $runRoot 'stubs'
New-Item -ItemType Directory -Path $sourceDir,(Join-Path $stubDir 'ProfilingDebugging') -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $sourceDir 'Fixture.cpp'), @'
#include "Fixture.h"
int Work(int value) {
    if (value > 0) { return value + 1; }
    else if (value == 0) { return 7; }
    else { return -1; }
}
int main() {
    const int a = Work(1);
    const int b = Work(0);
    const int c = Work(-1);
    if (a != 2 || b != 7 || c != -1) { return 1; }
    if (CounterTotal != (TEST_COUNTER_ENABLED ? 3 : 0)) { return 2; }
    return 0;
}
'@, $utf8)
[System.IO.File]::WriteAllText((Join-Path $stubDir 'Fixture.h'), @'
#pragma once
#include <atomic>
inline int CounterTotal = 0;
#define TEXT(value) L##value
'@, $utf8)
[System.IO.File]::WriteAllText((Join-Path $stubDir 'ProfilingDebugging/CountersTrace.h'), @'
#pragma once
#if TEST_COUNTER_ENABLED
struct Counter {
    std::atomic<int> Value{0};
    explicit Counter(const wchar_t*) {}
    void Increment() { ++Value; ++CounterTotal; }
};
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display) Counter __GTraceCounter##Name(Display);
#define TRACE_COUNTER_INCREMENT(Name) __GTraceCounter##Name.Increment();
#else
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display)
#define TRACE_COUNTER_INCREMENT(Name)
#endif
'@, $utf8)
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
$discovery = Join-Path $runRoot 'discovery'
dotnet $cli counters $sourceDir --report $discovery
if ($LASTEXITCODE -ne 0) { throw 'Discovery failed.' }
$candidates = Get-Content (Join-Path $discovery 'candidates.json') -Raw -Encoding utf8 | ConvertFrom-Json
$selection = Get-Content (Join-Path $discovery 'selection.json') -Raw -Encoding utf8 | ConvertFrom-Json
$selection.Selections = @($candidates | Where-Object Function -eq 'Work' | ForEach-Object {
    [pscustomobject]@{ Id = $_.Id; CounterName = $_.SuggestedName; DisplayName = $_.DisplayName }
})
if ($selection.Selections.Count -ne 3) { throw 'Expected three Work branches.' }
$selectionPath = Join-Path $runRoot 'reviewed-selection.json'
$selection | ConvertTo-Json -Depth 6 | Set-Content $selectionPath -Encoding utf8
$output = Join-Path $runRoot 'selected'
dotnet $cli counters $sourceDir --selection $selectionPath --report $output
if ($LASTEXITCODE -ne 0) { throw 'Selected preview failed.' }
git -C $sourceDir apply --check (Join-Path $output 'changes.patch')
if ($LASTEXITCODE -ne 0) { throw 'Patch check failed.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'MSVC x64 tools not found.' }
Import-Module (Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
foreach ($enabled in @(0,1)) {
    $exe = Join-Path $runRoot "fixture-$enabled.exe"
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /utf-8 "/DTEST_COUNTER_ENABLED=$enabled" "/I$stubDir" "/Fo$(Join-Path $runRoot "fixture-$enabled.obj")" "/Fe$exe" (Join-Path $output 'modified/Fixture.cpp')
    if ($LASTEXITCODE -ne 0) { throw "Compile failed ($enabled)." }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw "Runtime count or branch semantics changed ($enabled, exit $LASTEXITCODE)." }
    Write-Output "Event C++ compile/runtime PASS (counters=$enabled)"
}
Write-Output "Artifacts: $runRoot"
