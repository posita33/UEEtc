using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Plans;
using UEEtc.Core.Source;

namespace UEEtc.Core.Features.CombinedTracing;

public sealed class CombinedTracingService
{
    public AnalysisPlan Analyze(string rootPath, bool focusedCpu = true, CpuMacroKind cpuMacro = CpuMacroKind.Auto,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        // One filesystem discovery and one original-byte snapshot for both independent policies.
        var events = new EventCounterService();
        var scan = events.Analyze(rootPath, progress, cancellationToken);
        var eventProposals = events.Propose(scan, scan.Candidates.Select(EventCounterDefaults.Create).ToArray(), cancellationToken);
        var cpu = new CppInstrumenter();
        var files = eventProposals.Select(proposal =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = proposal.Source;
            var cpuProposal = cpu.Propose(file.RelativePath, file.OriginalBytes, new(FocusedCpu: focusedCpu, CpuMacro: cpuMacro));
            return FileProposal.Merge(cpuProposal, proposal).Render();
        }).ToArray();
        return scan.Snapshot with { Feature = "CombinedTracing", Files = files };
    }
}
