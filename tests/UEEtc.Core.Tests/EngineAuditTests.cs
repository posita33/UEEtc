using System.Text;
using UEEtc.Core.Features.CombinedTracing;
using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Source;

internal static class EngineAuditTests
{
    private const string GotoBatch = "#include \"F.h\"\nvoid F::Batch(const TArray<int>& Items, bool Jump) { if (Jump) goto Work; if (!Items.IsEmpty()) { Work: for (auto Item : Items) Use(Item); } }";
    public static (string Name, Action Run)[] All =>
    [
        ("Goto batch keeps cumulative count and input quantity without inserting an unsafe lifetime", () => WithSource(GotoBatch, root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root); var candidate = scan.Candidates.Single();
            Check(candidate.ScopeTrackingExclusion?.Contains("goto", StringComparison.Ordinal) == true);
            var plan = service.BuildPlan(scan, [EventCounterDefaults.Create(candidate)]);
            Check(plan.AddedEventCounters == 1 && plan.AddedQuantityCounters == 1 && plan.AddedDecrements == 0);
            Check(!plan.Files.Single().ProposedText.Contains("ON_SCOPE_EXIT", StringComparison.Ordinal));
            Check(EventCounterDefaults.Explain(candidate).Contains("goto", StringComparison.Ordinal));
        })),
        ("Unsafe goto scope rejects explicit and companion selections as validation results", () => WithSource(GotoBatch, root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root); var c = scan.Candidates.Single();
            foreach (var selection in new[] { new EventCounterSelection(c.Id, "Active", "Active", Measurement: CounterMeasurement.ActiveScope), new(c.Id, "Count", "Count", IncludeActiveScope: true) })
            {
                var result = service.TryBuildPreview(scan, [selection]);
                Check(result.Plan is null && result.Errors.Any(e => e.Contains("goto", StringComparison.Ordinal)));
                try { service.BuildPlan(scan, [selection]); throw new Exception("Unsafe selection was accepted"); } catch (ArgumentException) { }
            }
        })),
        ("CPU after Super does not introduce a lifetime bypassed by goto", () =>
        {
            const string source = "#include \"F.h\"\nvoid F::Work(bool Jump) { if (Jump) goto Body; Super::Work(); Body: for (int i=0;i<3;++i) Use(); }";
            var f = new CppInstrumenter().Analyze("F.cpp", Encoding.UTF8.GetBytes(source), new());
            Check(f.AddedCpuScopes == 0 && f.Functions.Single().CpuDecision.Contains("goto", StringComparison.Ordinal));
        }),
        ("CPU at function entry remains safe for intra-function goto", () => WithSource(GotoBatch, root =>
        {
            var plan = new CombinedTracingService().Analyze(root);
            Check(plan.AddedCpuScopes == 1 && plan.AddedEventCounters == 1 && plan.AddedDecrements == 0);
        })),
        ("Goto in a comment or literal does not block normal scope measurement", () => WithSource(
            "#include \"F.h\"\nvoid F::Batch(const TArray<int>& Items) { /* goto Work; */ auto Text = \"goto\"; if (Ready) { for (auto Item : Items) Use(Item); } }", root =>
            {
                var plan = new CombinedTracingService().Analyze(root);
                Check(plan.AddedCpuScopes == 1 && plan.AddedDecrements == 1);
            })),
        ("Repeated previews use the captured original and reject stale application", () => WithSource(
            "#include \"F.h\"\nvoid F::Work(uint32 NumBytes) { if (Ready) { Use(); } }", root =>
            {
                var service = new EventCounterService(); var scan = service.Analyze(root);
                var selections = scan.Candidates.Select(EventCounterDefaults.Create).ToArray();
                var first = service.BuildPreview(scan, selections);
                File.WriteAllText(Path.Combine(root, "F.cpp"), "Changed externally");
                var second = service.BuildPreview(scan, selections);
                Check(first.Files.Single().ProposedBytes.AsSpan().SequenceEqual(second.Files.Single().ProposedBytes));
                try { new SourceWorkspace().Apply(second); throw new Exception("Expected stale source rejection"); } catch (IOException) { }
            }))
    ];
    private static void WithSource(string source, Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "UEEtc-audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { File.WriteAllText(Path.Combine(root, "F.cpp"), source); action(root); }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Engine audit assertion failed"); }
}
