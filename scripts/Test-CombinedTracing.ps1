param([string]$EngineRoot = 'C:/Program Files/Epic Games/UE_5.8/Engine')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/combined-cpp/' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $runRoot 'source'; $stubs = Join-Path $runRoot 'stubs'
New-Item -ItemType Directory -Force $source,(Join-Path $stubs 'ProfilingDebugging'),(Join-Path $stubs 'Misc') | Out-Null
Copy-Item -LiteralPath (Join-Path $EngineRoot 'Source/Runtime/Core/Public/Misc/ScopeExit.h') -Destination (Join-Path $stubs 'Misc/ScopeExit.h')
@'
#include "Fixture.h"
void Derived::Process(const TArray<int>& Items, bool Early, bool Throw) {
    Super::Process();
    if (!Items.IsEmpty()) {
        LocalLifetime local;
        for (auto Item : Items) {
            if (Early) return;
            if (Throw) throw Item;
            ++Processed;
        }
    }
}
'@ | Set-Content (Join-Path $source 'Fixture.cpp') -Encoding utf8
@'
#pragma once
#include <atomic>
#include <cwchar>
#include <string_view>
#include <vector>
#define TEXT(value) L##value
inline std::atomic<int> Calls{0}, Current{0}, Peak{0}, Quantity{0};
inline int BaseCalls = 0, Processed = 0, Errors = 0, CpuEntries = 0, CpuExits = 0, CpuLive = 0;
template<class T> struct TArray : std::vector<T> {
    using std::vector<T>::vector;
    int Num() const { return static_cast<int>(this->size()); }
    bool IsEmpty() const { return this->empty(); }
};
struct Base { void Process() { ++BaseCalls; } };
struct Derived : Base { using Super = Base; void Process(const TArray<int>& Items, bool Early, bool Throw); };
struct CpuScope {
    explicit CpuScope(const char* name) {
        if (std::string_view(name) != "Derived Process" || BaseCalls != CpuEntries + 1) ++Errors;
        ++CpuEntries; ++CpuLive;
    }
    ~CpuScope() { ++CpuExits; --CpuLive; }
};
struct LocalLifetime {
    ~LocalLifetime() { if (Current != TEST_COUNTER_ENABLED || CpuLive != TEST_CPU_ENABLED) ++Errors; }
};
int main() {
    Derived value; TArray<int> items{1,2,3};
    value.Process({}, false, false);
    value.Process(items, false, false);
    value.Process(items, true, false);
    try { value.Process(items, false, true); return 1; } catch (int code) { if (code != 1) return 2; }
    if (Errors || BaseCalls != 4 || Processed != 3) return 3;
    if (Calls != TEST_COUNTER_ENABLED * 3 || Quantity != TEST_COUNTER_ENABLED * 9 || Current != 0 || Peak != TEST_COUNTER_ENABLED) return 4;
    if (CpuEntries != TEST_CPU_ENABLED * 4 || CpuExits != CpuEntries || CpuLive != 0) return 5;
    return 0;
}
'@ | Set-Content (Join-Path $stubs 'Fixture.h') -Encoding utf8
@'
#pragma once
#if TEST_CPU_ENABLED
#define CPU_JOIN_IMPL(a,b) a##b
#define CPU_JOIN(a,b) CPU_JOIN_IMPL(a,b)
#define TRACE_CPUPROFILER_EVENT_SCOPE_STR(Name) CpuScope CPU_JOIN(Cpu_,__LINE__)(Name);
#define TRACE_CPUPROFILER_EVENT_SCOPE(Name) TRACE_CPUPROFILER_EVENT_SCOPE_STR(#Name)
#else
#define TRACE_CPUPROFILER_EVENT_SCOPE_STR(Name)
#define TRACE_CPUPROFILER_EVENT_SCOPE(Name)
#endif
'@ | Set-Content (Join-Path $stubs 'ProfilingDebugging/CpuProfilerTrace.h') -Encoding utf8
@'
#pragma once
#if TEST_COUNTER_ENABLED
struct Counter {
    bool Scope;
    explicit Counter(const wchar_t* name) : Scope(std::wcsstr(name, L"ActiveScopes") != nullptr) {}
    void Increment() { if (!Scope) { ++Calls; return; } int current = ++Current; int peak = Peak.load(); while (current > peak && !Peak.compare_exchange_weak(peak, current)) {} }
    void Decrement() { --Current; }
    void Add(int amount) { Quantity += amount; }
};
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display) Counter __GTraceCounter##Name(Display);
#define TRACE_COUNTER_INCREMENT(Name) __GTraceCounter##Name.Increment();
#define TRACE_COUNTER_DECREMENT(Name) __GTraceCounter##Name.Decrement();
#define TRACE_COUNTER_ADD(Name, Value) __GTraceCounter##Name.Add(Value);
#else
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display)
#define TRACE_COUNTER_INCREMENT(Name)
#define TRACE_COUNTER_DECREMENT(Name)
#define TRACE_COUNTER_ADD(Name, Value)
#endif
'@ | Set-Content (Join-Path $stubs 'ProfilingDebugging/CountersTrace.h') -Encoding utf8
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'MSVC x64 tools not found.' }
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64' -or !(Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    Import-Module (Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
}
foreach ($macro in @('auto','str')) {
    $output = Join-Path $runRoot $macro
    dotnet $cli combined $source --cpu-macro $macro --report $output
    if ($LASTEXITCODE -ne 0) { throw 'Combined preview failed.' }
    git -C $source apply --check (Join-Path $output 'changes.patch')
    if ($LASTEXITCODE -ne 0) { throw 'Combined patch check failed.' }
    foreach ($cpuEnabled in @(0,1)) { foreach ($counterEnabled in @(0,1)) {
        $tag = "$macro-$cpuEnabled-$counterEnabled"; $exe = Join-Path $runRoot "$tag.exe"
        & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /utf-8 "/DTEST_CPU_ENABLED=$cpuEnabled" "/DTEST_COUNTER_ENABLED=$counterEnabled" "/I$stubs" "/Fo$(Join-Path $runRoot "$tag.obj")" "/Fe$exe" (Join-Path $output 'modified/Fixture.cpp')
        if ($LASTEXITCODE -ne 0) { throw "Combined compile failed: $tag" }
        & $exe
        if ($LASTEXITCODE -ne 0) { throw "Combined runtime failed: $tag ($LASTEXITCODE)" }
        Write-Output "Combined PASS ($tag): CPU after Super, cumulative count, input quantity, scope exit, early return, exception"
    } }
}
Write-Output "Artifacts: $runRoot"
