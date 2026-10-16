using UEEtc.Core.Source;
using UEEtc.Core.Plans;

namespace UEEtc.Core.Features.FunctionTracing;

// Entry-count options are retained for existing Console scripts.
// Event counters are a separate feature; the CPU screen requests CPU only.
public sealed class InstrumentationService
{
    public AnalysisPlan Analyze(string rootPath, InstrumentationOptions? options = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (!options.CpuScopes && !options.CallCounters)
            throw new ArgumentException("CPU スコープまたは進入回数カウンターを選択してください。");
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var instrumenter = new CppInstrumenter();
        var plan = new SourceWorkspace().Analyze(rootPath, "FunctionTracing",
            (path, bytes) => instrumenter.AnalyzeUnresolved(path, bytes, options),
            options.CallCounters ? text => reserved.UnionWith(CounterNaming.Identifiers(text)) : null,
            progress, cancellationToken);
        return plan with { Files = CounterNaming.Resolve(plan.Files, reserved, cancellationToken) };
    }

    public string Apply(AnalysisPlan plan) => new SourceWorkspace().Apply(plan);
}
