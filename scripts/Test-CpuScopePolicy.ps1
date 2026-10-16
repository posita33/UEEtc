param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/cpu-policy-cpp/' + [guid]::NewGuid().ToString('N'))
$sourceDir = Join-Path $runRoot 'source'
$stubDir = Join-Path $runRoot 'stubs'
New-Item -ItemType Directory -Path $sourceDir,(Join-Path $stubDir 'ProfilingDebugging') -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $sourceDir 'Fixture.cpp'), @'
#include "Fixture.h"
void Derived::Work(int value) {
    BeforeSuper();
    Super::Work(value);
    AfterSuper();
    if (value == 0) return;
    ++DerivedTail;
}
'@, $utf8)
[System.IO.File]::WriteAllText((Join-Path $stubDir 'Fixture.h'), @'
#pragma once
#include <atomic>
#include <cstring>
inline int ActiveScopes = 0, ScopeEntries = 0, ScopeExits = 0, CounterEntries = 0;
inline int Errors = 0, BeforeCalls = 0, BaseCalls = 0, AfterCalls = 0, DerivedTail = 0;
#define TEXT(value) L##value
class Base {
public:
    void Work(int) {
        if (ActiveScopes != 0) ++Errors;
        // Legacy entry counts must already have incremented before entering the base method.
        if (CounterEntries != (TEST_COUNTER_ENABLED ? BaseCalls + 1 : 0)) ++Errors;
        ++BaseCalls;
    }
};
class Derived : public Base {
    using Super = Base;
public:
    void Work(int value);
    void BeforeSuper() { if (ActiveScopes != 0) ++Errors; ++BeforeCalls; }
    void AfterSuper() { if (ActiveScopes != TEST_CPU_ENABLED) ++Errors; ++AfterCalls; }
};
// The caller is defined in a header so the fixture only instruments Derived::Work.
int main() {
    Derived item;
    item.Work(0);
    item.Work(1);
    if (ActiveScopes != 0 || Errors != 0) return 1;
    if (ScopeEntries != TEST_CPU_ENABLED * 2 || ScopeExits != TEST_CPU_ENABLED * 2) return 2;
    if (BeforeCalls != 2 || BaseCalls != 2 || AfterCalls != 2 || DerivedTail != 1) return 3;
    if (CounterEntries != TEST_COUNTER_ENABLED * 2) return 4;
    return 0;
}
'@, $utf8)
[System.IO.File]::WriteAllText((Join-Path $stubDir 'ProfilingDebugging/CpuProfilerTrace.h'), @'
#pragma once
#if TEST_CPU_ENABLED
struct TestScope {
    explicit TestScope(const char* label) { if (std::strcmp(label, "Derived Work") != 0) ++Errors; ++ActiveScopes; ++ScopeEntries; }
    ~TestScope() { --ActiveScopes; ++ScopeExits; }
};
#define TRACE_CPUPROFILER_EVENT_SCOPE_STR(Name) TestScope ScopeInstance(Name);
#else
#define TRACE_CPUPROFILER_EVENT_SCOPE_STR(Name)
#endif
'@, $utf8)
[System.IO.File]::WriteAllText((Join-Path $stubDir 'ProfilingDebugging/CountersTrace.h'), @'
#pragma once
#if TEST_COUNTER_ENABLED
struct Counter { std::atomic<int> Value{0}; explicit Counter(const wchar_t*) {} void Increment() { ++Value; ++CounterEntries; } };
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display) Counter __GTraceCounter##Name(Display);
#define TRACE_COUNTER_INCREMENT(Name) __GTraceCounter##Name.Increment();
#else
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display)
#define TRACE_COUNTER_INCREMENT(Name)
#endif
'@, $utf8)
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
$output = Join-Path $runRoot 'preview'
dotnet $cli $sourceDir --both --report $output
if ($LASTEXITCODE -ne 0) { throw 'Preview failed.' }
git -C $sourceDir apply --check (Join-Path $output 'changes.patch')
if ($LASTEXITCODE -ne 0) { throw 'Patch check failed.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'MSVC x64 tools not found.' }
Import-Module (Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
foreach ($cpu in @(0,1)) {
    foreach ($counter in @(0,1)) {
        $tag = "cpu$cpu-counter$counter"
        $exe = Join-Path $runRoot "$tag.exe"
        & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /utf-8 "/DTEST_CPU_ENABLED=$cpu" "/DTEST_COUNTER_ENABLED=$counter" "/I$stubDir" "/Fo$(Join-Path $runRoot "$tag.obj")" "/Fe$exe" (Join-Path $output 'modified/Fixture.cpp')
        if ($LASTEXITCODE -ne 0) { throw "Compile failed ($tag)." }
        & $exe
        if ($LASTEXITCODE -ne 0) { throw "Label, scope lifetime or Super placement failed ($tag, exit $LASTEXITCODE)." }
        Write-Output "CPU policy C++ compile/runtime PASS ($tag)"
    }
}
Write-Output "Artifacts: $runRoot"
