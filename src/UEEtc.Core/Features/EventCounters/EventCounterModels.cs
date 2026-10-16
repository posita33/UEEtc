using UEEtc.Core.Plans;

namespace UEEtc.Core.Features.EventCounters;

public sealed record EventCounterCandidate(string Id, string RelativePath, string Function, int Line,
    string Branch, string Condition, string Hint, string Reason, string SuggestedName, string DisplayName)
{
    public IReadOnlyList<string> AddValues { get; init; } = [];
    public QuantityRecommendation? RecommendedQuantity { get; init; }
    public string? ScopeTrackingExclusion { get; init; }
    internal int Position { get; init; }
    internal string Indent { get; init; } = "";
}

public sealed record EventCounterSelection(string Id, string CounterName, string DisplayName, QuantityCounterSelection? Quantity = null,
    CounterMeasurement Measurement = CounterMeasurement.Cumulative, bool IncludeActiveScope = false);
public sealed record EventCounterPreviewResult(AnalysisPlan? Plan, IReadOnlyList<string> Errors);

public sealed class EventCounterScan
{
    public AnalysisPlan Snapshot { get; }
    public IReadOnlyList<EventCounterCandidate> Candidates { get; }
    internal HashSet<string> ReservedNames { get; }
    internal IReadOnlyDictionary<string, EventSourceFile> Sources { get; }
    internal EventCounterScan(AnalysisPlan snapshot, List<EventCounterCandidate> candidates, HashSet<string> reserved,
        IReadOnlyDictionary<string, EventSourceFile> sources)
    {
        Snapshot = snapshot;
        Candidates = candidates.AsReadOnly();
        ReservedNames = reserved;
        Sources = sources;
    }
}

public sealed record EventCounterSelectionFile(string RootPath, string Fingerprint, IReadOnlyList<EventCounterSelection> Selections);
