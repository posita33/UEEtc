using System.Security.Cryptography;
using System.Text.Json;
using UEEtc.Core.Plans;
using UEEtc.Core.Source;

namespace UEEtc.Core.Features.EventCounters;

public sealed class EventCounterService
{
    public EventCounterScan Analyze(string rootPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var candidates = new List<EventCounterCandidate>();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, EventSourceFile>(StringComparer.Ordinal);
        var discovery = new EventCandidateDiscovery(candidates, sources, cancellationToken);
        var snapshot = new SourceWorkspace().Analyze(rootPath, "EventCounters", discovery.Read,
            text => reserved.UnionWith(System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]*").Select(m => m.Value)), progress, cancellationToken);
        var duplicateLabels = candidates.GroupBy(c => c.DisplayName, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return new(snapshot, candidates.Select(c => duplicateLabels.Contains(c.DisplayName) ? c with { DisplayName = c.DisplayName + " [" + c.Id + "]" } : c).ToList(), reserved, sources);
    }

    public AnalysisPlan BuildPlan(EventCounterScan scan, IEnumerable<EventCounterSelection> selections, CancellationToken cancellationToken = default)
        => Build(scan, selections.ToArray(), false, cancellationToken);

    public AnalysisPlan BuildPreview(EventCounterScan scan, IEnumerable<EventCounterSelection> selections, CancellationToken cancellationToken = default)
        => Build(scan, selections.ToArray(), true, cancellationToken);

    public EventCounterPreviewResult TryBuildPreview(EventCounterScan scan, IEnumerable<EventCounterSelection> selections, CancellationToken cancellationToken = default)
    {
        var requested = selections.ToArray();
        var errors = EventSelectionValidator.Validate(scan, requested, true);
        return errors.Count > 0 ? new(null, errors) : new(Render(scan, requested, cancellationToken), []);
    }

    internal IReadOnlyList<FileProposal> Propose(EventCounterScan scan, EventCounterSelection[] selections, CancellationToken cancellationToken)
    {
        RequireValid(scan, selections, false);
        return EventCounterPlanBuilder.Propose(scan, selections, cancellationToken);
    }

    private static AnalysisPlan Build(EventCounterScan scan, EventCounterSelection[] selections, bool allowUnconfirmed, CancellationToken cancellationToken)
    {
        RequireValid(scan, selections, allowUnconfirmed);
        return Render(scan, selections, cancellationToken);
    }

    private static void RequireValid(EventCounterScan scan, EventCounterSelection[] selections, bool allowUnconfirmed)
    {
        var errors = EventSelectionValidator.Validate(scan, selections, allowUnconfirmed);
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
    }

    private static AnalysisPlan Render(EventCounterScan scan, EventCounterSelection[] selections, CancellationToken cancellationToken) =>
        scan.Snapshot with
        {
            Files = EventCounterPlanBuilder.Propose(scan, selections, cancellationToken).Select(p => p.Render()).ToArray(),
            RequiresQuantityConfirmation = selections.Any(s => s.Quantity is { MeaningConfirmed: false })
        };

    public EventCounterSelectionFile CreateSelection(EventCounterScan scan) => new(scan.Snapshot.RootPath, Fingerprint(scan), []);
    public AnalysisPlan BuildPlan(EventCounterScan scan, EventCounterSelectionFile selection, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(selection.RootPath, scan.Snapshot.RootPath, StringComparison.OrdinalIgnoreCase) || selection.Fingerprint != Fingerprint(scan))
            throw new InvalidOperationException("選択ファイルのソースが解析時点と異なります。候補を再解析してください。");
        return BuildPlan(scan, selection.Selections ?? throw new ArgumentException("Selections がありません。"), cancellationToken);
    }

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Fingerprint(EventCounterScan scan) => EventCounterIdentity.Hash(string.Join("\n", scan.Snapshot.Files.Select(f => f.RelativePath + ":" + Convert.ToHexString(SHA256.HashData(f.OriginalBytes)))) + "\n" + string.Join("\n", scan.ReservedNames.Order(StringComparer.Ordinal)));
}
