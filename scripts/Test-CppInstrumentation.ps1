param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Join-Path $repoRoot ('artifacts/cpp-smoke/' + [guid]::NewGuid().ToString('N'))
$sourceDir = Join-Path $fixtureRoot 'source'
$reportDir = Join-Path $fixtureRoot 'report'
$stubDir = Join-Path $fixtureRoot 'stubs'
New-Item -ItemType Directory -Path $sourceDir, (Join-Path $stubDir 'ProfilingDebugging') -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $sourceDir 'Fixture.cpp'), @'
#include "Fixture.h"
#include "UnityPart.cpp"
void FUnity::A_Work() { ++WorkDone; }
void FFixture::Work() {
    auto raw = R"tag({ " not a function })tag";
    (void)raw;
    auto lambda = []() { ++WorkDone; };
    lambda();
}
int FFixture::Value(int v) const { return v + 1'000; }
int FFixture::Value(float v) const { return static_cast<int>(v); }
namespace A { void Work() { ++WorkDone; } }
namespace B { void Work() { ++WorkDone; } }
#if TRACE_TEST_ENABLED
void Conditional() { ++WorkDone; }
#else
void Conditional() { WorkDone += 1; }
#endif
int main() {
    FFixture fixture;
    fixture.Work();
    if (fixture.Value(1) != 1001 || fixture.Value(2.0f) != 2) return 1;
    A::Work(); B::Work(); Conditional();
    FUnity::A_Work(); FUnity_A::Work();
    if (WorkDone != 6) return 2;
#if TRACE_TEST_ENABLED
    if (CounterEntries != 9 || ScopeEntries != 9 || ScopeExits != 8) return 3;
#else
    if (CounterEntries != 0 || ScopeEntries != 0 || ScopeExits != 0) return 4;
#endif
    return 0;
}
'@, $utf8)
[System.IO.File]::WriteAllText((Join-Path $sourceDir 'UnityPart.cpp'), @'
#include "Fixture.h"
void FUnity_A::Work() { ++WorkDone; }
'@, $utf8)
[System.IO.File]::WriteAllText((Join-Path $stubDir 'Fixture.h'), @'
#pragma once
#include <atomic>
inline int WorkDone = 0;
inline int CounterEntries = 0;
inline int ScopeEntries = 0;
inline int ScopeExits = 0;
#define TEXT(value) L##value
class FFixture {
public:
    FFixture() {}
    void Work();
    int Value(int v) const;
    int Value(float v) const;
};
class FUnity { public: static void A_Work(); };
class FUnity_A { public: static void Work(); };
'@, $utf8)
# These stubs check generated placement and lifetime with both enabled/disabled macro expansions.
# They are not substitutes for a build using Unreal's actual toolchain and headers.
[System.IO.File]::WriteAllText((Join-Path $stubDir 'ProfilingDebugging/CpuProfilerTrace.h'), @'
#pragma once
#if TRACE_TEST_ENABLED
struct TestScope { TestScope() { ++ScopeEntries; } ~TestScope() { ++ScopeExits; } };
#define TRACE_CPUPROFILER_EVENT_SCOPE_STR(Name) TestScope TestScopeInstance;
#else
#define TRACE_CPUPROFILER_EVENT_SCOPE_STR(Name)
#endif
'@, $utf8)
[System.IO.File]::WriteAllText((Join-Path $stubDir 'ProfilingDebugging/CountersTrace.h'), @'
#pragma once
#if TRACE_TEST_ENABLED
struct TestCounter {
    std::atomic<long long> Value{0};
    explicit TestCounter(const wchar_t*) {}
    void Increment() { ++Value; ++CounterEntries; }
};
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display) TestCounter __GTraceCounter##Name(Display);
#define TRACE_COUNTER_INCREMENT(Name) __GTraceCounter##Name.Increment();
#else
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display)
#define TRACE_COUNTER_INCREMENT(Name)
#endif
'@, $utf8)
dotnet run --project (Join-Path $repoRoot 'src/UEEtc.Console') --configuration Release -- $sourceDir --report $reportDir
if ($LASTEXITCODE -ne 0) { throw 'Fixture instrumentation failed.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'MSVC C++ build tools were not found.' }
Import-Module (Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
foreach ($enabled in @(0, 1)) {
    $objectPath = Join-Path $fixtureRoot "fixture-$enabled.obj"
    $exePath = Join-Path $fixtureRoot "fixture-$enabled.exe"
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /utf-8 "/DTRACE_TEST_ENABLED=$enabled" "/I$stubDir" "/Fo$objectPath" "/Fe$exePath" (Join-Path $reportDir 'modified/Fixture.cpp')
    if ($LASTEXITCODE -ne 0) { throw "C++ compilation failed (trace=$enabled)." }
    & $exePath
    if ($LASTEXITCODE -ne 0) { throw "C++ runtime assertions failed (trace=$enabled, exit=$LASTEXITCODE)." }
    Write-Output "C++ compile/runtime PASS (trace=$enabled)"
}
Write-Output "Artifacts: $fixtureRoot"
