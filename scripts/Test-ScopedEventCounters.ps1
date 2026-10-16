param([string]$EngineRoot = 'C:/Program Files/Epic Games/UE_5.8/Engine')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ('artifacts/scoped-events/' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $runRoot 'source'
$stubs = Join-Path $runRoot 'stubs'
New-Item -ItemType Directory -Path $source,(Join-Path $stubs 'ProfilingDebugging'),(Join-Path $stubs 'Misc') -Force | Out-Null
# ScopeExit.h is self-contained: compile the actual installed UE guard, without modifying Engine.
Copy-Item -LiteralPath (Join-Path $EngineRoot 'Source/Runtime/Core/Public/Misc/ScopeExit.h') -Destination (Join-Path $stubs 'Misc/ScopeExit.h')
@'
#include "Fixture.h"
int Track(int depth, int action) {
    if (depth >= 0) {
        LocalLifetime local;
        if (depth > 0) return Track(depth - 1, action) + 1;
        if (action == 1) return 7;
        if (action == 2) throw 9;
        return 3;
    }
    return 0;
}
'@ | Set-Content (Join-Path $source 'Fixture.cpp') -Encoding utf8
@'
#pragma once
#include <atomic>
inline std::atomic<int> Live{0}, Peak{0}, Entries{0}, Exits{0};
inline int Errors = 0;
#define TEXT(value) L##value
struct LocalLifetime {
    ~LocalLifetime() { if (TEST_COUNTER_ENABLED ? Live.load() <= 0 : Live.load() != 0) ++Errors; }
};
int Track(int depth, int action);
int main() {
    if (Track(2, 1) != 9) return 1;
    try { Track(2, 2); return 2; } catch (int code) { if (code != 9) return 3; }
    if (Track(0, 0) != 3 || Errors != 0) return 4;
    if (Entries != TEST_COUNTER_ENABLED * 7) return 5;
    if (Exits != TEST_COUNTER_ENABLED * TEST_ACTIVE_SCOPE * 7) return 6;
    if (Live != (TEST_COUNTER_ENABLED && !TEST_ACTIVE_SCOPE ? 7 : 0)) return 7;
    if (Peak != TEST_COUNTER_ENABLED * (TEST_ACTIVE_SCOPE ? 3 : 7)) return 8;
    return 0;
}
'@ | Set-Content (Join-Path $stubs 'Fixture.h') -Encoding utf8
@'
#pragma once
#if TEST_COUNTER_ENABLED
struct Counter {
    explicit Counter(const wchar_t*) {}
    void Increment() { ++Entries; int current = ++Live; int peak = Peak.load(); while (current > peak && !Peak.compare_exchange_weak(peak, current)) {} }
    void Decrement() { ++Exits; --Live; }
};
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display) Counter __GTraceCounter##Name(Display);
#define TRACE_COUNTER_INCREMENT(Name) __GTraceCounter##Name.Increment();
#define TRACE_COUNTER_DECREMENT(Name) __GTraceCounter##Name.Decrement();
#else
#define TRACE_DECLARE_ATOMIC_INT_COUNTER(Name, Display)
#define TRACE_COUNTER_INCREMENT(Name)
#define TRACE_COUNTER_DECREMENT(Name)
#endif
'@ | Set-Content (Join-Path $stubs 'ProfilingDebugging/CountersTrace.h') -Encoding utf8
$cli = Join-Path $repoRoot 'src/UEEtc.Console/bin/Release/net10.0/UEEtc.Console.dll'
$discovery = Join-Path $runRoot 'discovery'
dotnet $cli counters $source --report $discovery
if ($LASTEXITCODE -ne 0) { throw 'Discovery failed.' }
$candidate = @(Get-Content (Join-Path $discovery 'candidates.json') -Raw -Encoding utf8 | ConvertFrom-Json) | Where-Object Condition -eq 'depth >= 0'
if (@($candidate).Count -ne 1) { throw 'Expected one outer branch.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'MSVC x64 tools not found.' }
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64' -or !(Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    Import-Module (Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
}
foreach ($measurement in @('Cumulative','ActiveScope')) {
    $selection = Get-Content (Join-Path $discovery 'selection.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $selection.Selections = @([pscustomobject]@{ Id=$candidate.Id; CounterName='Metric'; DisplayName="Track $measurement"; Measurement=$measurement })
    $selectionFile = Join-Path $runRoot "$measurement.json"
    $selection | ConvertTo-Json -Depth 6 | Set-Content $selectionFile -Encoding utf8
    $output = Join-Path $runRoot $measurement
    dotnet $cli counters $source --selection $selectionFile --report $output
    if ($LASTEXITCODE -ne 0) { throw 'Preview failed.' }
    git -C $source apply --check (Join-Path $output 'changes.patch')
    if ($LASTEXITCODE -ne 0) { throw 'Patch check failed.' }
    foreach ($enabled in @(0,1)) {
        $active = [int]($measurement -eq 'ActiveScope')
        $tag = "$measurement-$enabled"
        $exe = Join-Path $runRoot "$tag.exe"
        & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /utf-8 "/DTEST_COUNTER_ENABLED=$enabled" "/DTEST_ACTIVE_SCOPE=$active" "/I$stubs" "/Fo$(Join-Path $runRoot "$tag.obj")" "/Fe$exe" (Join-Path $output 'modified/Fixture.cpp')
        if ($LASTEXITCODE -ne 0) { throw "Compile failed: $tag" }
        & $exe
        if ($LASTEXITCODE -ne 0) { throw "Counter balance / reentrancy / unwinding failed: $tag ($LASTEXITCODE)" }
        Write-Output "Scoped counter PASS ($tag): returns, recursion, exception unwinding, local destruction"
    }
}
Write-Output "Artifacts: $runRoot"
