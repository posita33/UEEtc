// Copy this header into a module's Public/Tracing folder and configure its include path in UEEtc.
#pragma once

#include "ProfilingDebugging/CpuProfilerTrace.h"

// Keep the Engine scope in the caller's lexical scope; do not wrap it in do/while or a function.
#if UE_BUILD_DEVELOPMENT || UE_BUILD_TEST
#define PROJECT_TRACE_CPU_SCOPE_STR(Name) TRACE_CPUPROFILER_EVENT_SCOPE_STR(Name)
#else
#define PROJECT_TRACE_CPU_SCOPE_STR(Name)
#endif
