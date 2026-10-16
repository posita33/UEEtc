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
        var eventPlan = events.BuildPlan(scan, scan.Candidates.Select(EventCounterDefaults.Create), cancellationToken);
        var cpu = new CppInstrumenter();
        var files = eventPlan.Files.Select(file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cpuFile = cpu.Analyze(file.RelativePath, file.OriginalBytes, new(FocusedCpu: focusedCpu, CpuMacro: cpuMacro));
            var insertions = file.Insertions.Concat(cpuFile.Insertions).ToArray();
            var merged = file with
            {
                Functions = cpuFile.Functions.Concat(file.Functions).ToArray(),
                Diagnostics = cpuFile.Diagnostics.Concat(file.Diagnostics).Distinct(StringComparer.Ordinal).ToArray(),
                Insertions = insertions
            };
            if (insertions.Length == 0) return merged;
            var document = SourceDocument.Read(file.OriginalBytes);
            string text = SourceInsertion.Apply(document.Text, insertions);
            return merged with { OriginalText = document.Text, ProposedText = text, ProposedBytes = document.Encode(text) };
        }).ToArray();
        return eventPlan with { Feature = "CombinedTracing", Files = files };
    }
}
