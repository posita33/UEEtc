namespace UEEtc.Core.Features.FunctionTracing;

// CallCounters is the legacy function-entry mode, separate from selected event counters.
public sealed record InstrumentationOptions(bool CpuScopes = true, bool CallCounters = false, bool FocusedCpu = false,
    CpuMacroKind CpuMacro = CpuMacroKind.Auto);

public enum CpuMacroKind { Auto, Scope, StaticString }
