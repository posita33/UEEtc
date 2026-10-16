using UEEtc.Core.Features.EventCounters;

internal static class AutomaticCounterTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Automatic batch preview keeps cumulative count and adds independent scope and quantity metrics", () => WithSource(
            "void F::Batch(const TArray<int>& Items) { if (Ready) { for (auto Item : Items) { Use(Item); } } }", (service, scan) =>
            {
                var c = scan.Candidates.Single(); var selection = EventCounterDefaults.Create(c);
                var plan = service.BuildPlan(scan, [selection]); var file = plan.Files.Single(); var row = file.Functions.Single();
                Check(plan.AddedDecrements == 1 && plan.AddedQuantityCounters == 1 && !plan.RequiresQuantityConfirmation);
                Check(row.CounterMeasurement == "Cumulative" && row.ActiveScopeCounter is not null && row.Counter != row.ActiveScopeCounter);
                Check(file.ProposedText.Contains("TRACE_COUNTER_INCREMENT(" + row.Counter + ");") && !file.ProposedText.Contains("TRACE_COUNTER_DECREMENT(" + row.Counter + ");"));
                Check(file.ProposedText.Contains("TRACE_COUNTER_DECREMENT(" + row.ActiveScopeCounter + ");") && file.ProposedText.Contains("Items.Num()"));
                Check(EventCounterDefaults.Explain(c).Contains("累積回数は保持"));
            })),
        ("Scalar input gets automatic ADD but no inferred lifecycle", () => WithSource(
            "void F::Submit(uint32 NumBytes) { if (Ready) { SubmitAsync(); } }", (service, scan) =>
            {
                var plan = service.BuildPlan(scan, scan.Candidates.Select(EventCounterDefaults.Create));
                Check(plan.AddedQuantityCounters == 1 && plan.AddedDecrements == 0);
            })),
        ("Unknown quantities, nested loops and ambiguous quantities do not trigger automatic scope pairs", () =>
        {
            foreach (string source in new[] {
                "void F::Work() { if (Ready) { StartAsync(); } }",
                "void F::Work(const TArray<int>& Items) { if (Ready) { auto L = [&]() { for (auto I : Items) Use(I); }; Run(L); } }",
                "void F::Work(const TArray<int>& Items, uint32 NumBytes) { if (Ready) { for (auto I : Items) Use(I); } }" })
                WithSource(source, (service, scan) => {
                    var plan = service.BuildPlan(scan, scan.Candidates.Select(EventCounterDefaults.Create));
                    Check(plan.AddedQuantityCounters == 0 && plan.AddedDecrements == 0);
                });
        }),
        ("Automatic scope names avoid existing identifiers and remain idempotent", () => WithSource(
            "int F_Batch_If1_ActiveScopes; void F::Batch(const TArray<int>& Items) { if (Ready) { for (auto Item : Items) Use(Item); } }", (service, scan) =>
            {
                var plan = service.BuildPlan(scan, scan.Candidates.Select(EventCounterDefaults.Create)); var file = plan.Files.Single();
                Check(file.Functions.Single().ActiveScopeCounter != "F_Batch_If1_ActiveScopes");
                File.WriteAllBytes(Path.Combine(scan.Snapshot.RootPath, file.RelativePath), file.ProposedBytes);
                Check(service.Analyze(scan.Snapshot.RootPath).Candidates.Count == 0);
            })),
        ("Explicit single-scope mode rejects duplicate companion counters", () => WithSource(
            "void F::Work() { if (Ready) { Run(); } }", (service, scan) =>
            {
                var c = scan.Candidates.Single();
                Check(service.TryBuildPreview(scan, [new(c.Id, "Metric", "Metric", Measurement: CounterMeasurement.ActiveScope, IncludeActiveScope: true)]).Plan is null);
            }))
    ];
    private static void WithSource(string source, Action<EventCounterService, EventCounterScan> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "UEEtc-auto-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { File.WriteAllText(Path.Combine(root, "F.cpp"), "#include \"F.h\"\n" + source); var service = new EventCounterService(); action(service, service.Analyze(root)); }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Automatic counter assertion failed"); }
}
