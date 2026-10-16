$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/goto-cpp/' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $runRoot 'source'; $stubs = Join-Path $runRoot 'stubs'
New-Item -ItemType Directory -Force $source,(Join-Path $stubs 'ProfilingDebugging') | Out-Null
@'
#include "Fixture.h"
void Process(const TArray<int>& Items, bool Jump) {
    if (Jump) goto Work;
    if (!Items.IsEmpty()) {
Work:
        for (auto Item : Items) { Use(Item); }
    }
}
void Derived::Work(bool Jump) {
    if (Jump) goto Body;
    Super::Work();
Body:
    for (int i=0;i<3;++i) Use(i);
}
'@ | Set-Content (Join-Path $source 'Fixture.cpp') -Encoding utf8
@'
#pragma once
#include <vector>
#define TEXT(value) L##value
#define UE_JOIN_IMPL(a,b) a##b
#define UE_JOIN(a,b) UE_JOIN_IMPL(a,b)
template<class T> struct TArray : std::vector<T> {
    int Num() const { return static_cast<int>(this->size()); }
    bool IsEmpty() const { return this->empty(); }
};
inline void Use(int) {}
struct Base { void Work() {} };
struct Derived : Base { using Super = Base; void Work(bool Jump); };
struct CpuGuard { CpuGuard() {} ~CpuGuard() {} };
'@ | Set-Content (Join-Path $stubs 'Fixture.h') -Encoding utf8
@'
#pragma once
#if TEST_TRACE_ENABLED
#define TRACE_CPUPROFILER_EVENT_SCOPE(Name) CpuGuard UE_JOIN(Cpu_,__LINE__);
#else
#define TRACE_CPUPROFILER_EVENT_SCOPE(Name)
#endif
'@ | Set-Content (Join-Path $stubs 'ProfilingDebugging/CpuProfilerTrace.h') -Encoding utf8
@'
#pragma once
#if TEST_TRACE_ENABLED
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display) int UE_JOIN(__GTraceCounter,Name)=0;
#define TRACE_COUNTER_INCREMENT(Name) ++UE_JOIN(__GTraceCounter,Name);
#define TRACE_COUNTER_ADD(Name, Value) UE_JOIN(__GTraceCounter,Name) += Value;
#else
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display)
#define TRACE_COUNTER_INCREMENT(Name)
#define TRACE_COUNTER_ADD(Name, Value)
#endif
'@ | Set-Content (Join-Path $stubs 'ProfilingDebugging/CountersTrace.h') -Encoding utf8
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
$output = Join-Path $runRoot 'preview'
dotnet $cli combined $source --report $output
if ($LASTEXITCODE -ne 0) { throw 'Goto analysis failed.' }
$plan = Get-Content (Join-Path $output 'report.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($plan.AddedCpuScopes -ne 1 -or $plan.AddedEventCounters -ne 1 -or $plan.AddedQuantityCounters -ne 1 -or $plan.AddedDecrements -ne 0) { throw 'Unexpected goto instrumentation policy.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'MSVC x64 tools not found.' }
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64' -or !(Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    Import-Module (Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
}
foreach ($fixture in @(@{Name='original';Path=(Join-Path $source 'Fixture.cpp')},@{Name='generated';Path=(Join-Path $output 'modified/Fixture.cpp')})) {
    foreach ($enabled in @(0,1)) {
        $tag = $fixture.Name + "-$enabled"
        & cl.exe /nologo /c /std:c++20 /EHsc /W4 /WX /utf-8 "/DTEST_TRACE_ENABLED=$enabled" "/I$stubs" "/Fo$(Join-Path $runRoot "$tag.obj")" $fixture.Path
        if ($LASTEXITCODE -ne 0) { throw "Goto compilation failed: $tag" }
        Write-Output "Goto compile PASS ($tag): branch entry and post-Super jumps remain valid"
    }
}
Write-Output "Artifacts: $runRoot"
