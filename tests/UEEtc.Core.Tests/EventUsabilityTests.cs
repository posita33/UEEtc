using System.Text;
using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Features.FunctionTracing;

internal static class EventUsabilityTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Generated source contains no application branding and labels start with the function", () =>
        {
            var file = new CppInstrumenter().Analyze("Deep/Long/Unwanted/File.cpp", Encoding.UTF8.GetBytes("#include \"F.h\"\nvoid AThing::Work() { Run(); }"), new(true, true));
            Check(!file.ProposedText.Contains("UEEtc") && !file.ProposedText.Contains("Deep/Long"));
            Check(file.ProposedText.Contains("TEXT(\"AThing Work Calls"));
            WithSource("namespace Game { void AThing::Work(uint32 NumBytes) { if (bSuccess) { Finish(); } } }", (service, scan) =>
            {
                var c = scan.Candidates.Single(); Check(c.DisplayName == "AThing Work if1");
                var plan = service.BuildPlan(scan, [new(c.Id, c.SuggestedName, c.DisplayName, c.RecommendedQuantity!.CreateInputSelection(c))]);
                string generated = plan.Files.Single().ProposedText;
                Check(!generated.Contains("UEEtc") && !generated.Contains("Deep/Long") && generated.Contains("AThing Work if1 InputBytes [bytes]"));
            });
        }),
        ("Short labels disambiguate overloads at the end without paths", () => WithSource("void F::Work(int a) { if (a) { Run(); } } void F::Work(float a) { if (a) { Run(); } }", (_, scan) =>
        {
            Check(scan.Candidates.Select(c => c.DisplayName).Distinct().Count() == 2);
            Check(scan.Candidates.All(c => c.DisplayName.StartsWith("F Work if1 [", StringComparison.Ordinal) && !c.DisplayName.Contains('/')));
        })),
        ("Enabling incomplete ADD returns validation messages without throwing", () => WithSource("void F::Work(uint32 Size) { if (bOk) { Run(); } }", (service, scan) =>
        {
            var c = scan.Candidates.Single();
            foreach (var q in new[] { new QuantityCounterSelection("Amount", "Amount", "", ""), new("Amount", "Amount", "Size", ""), new("Amount", "Amount", "Unsafe()", "bytes"), new("bad name", "Amount", "Size", "bytes") })
            {
                var result = service.TryBuildPreview(scan, [new(c.Id, c.SuggestedName, c.DisplayName, q)]);
                Check(result.Plan is null && result.Errors.Count > 0);
            }
        })),
        ("Const UE batch parameters recommend an input count even after a Num guard", () =>
        {
            foreach (string type in new[] { "const TArray<int>&", "TConstArrayView<int>", "const TConstArrayView<FRequest>&" })
                WithSource("void F::Work(" + type + " Requests) { if (!Requests.IsEmpty()) { for (int i=0;i<Requests.Num();++i) { Submit(); } } }", (service, scan) =>
                {
                    var c = scan.Candidates.Single(); Check(c.AddValues.Contains("Requests.Num()") && c.RecommendedQuantity is { Unit: "items" });
                    var q = c.RecommendedQuantity!.CreateInputSelection(c);
                    Check(q.MeaningConfirmed && q.DisplayName.StartsWith("F Work if1 InputItems", StringComparison.Ordinal));
                    var plan = service.BuildPlan(scan, [new(c.Id, c.SuggestedName, c.DisplayName, q)]);
                    Check(!plan.RequiresQuantityConfirmation && plan.Files.Single().ProposedText.Contains("TRACE_COUNTER_ADD(" + q.CounterName + ", Requests.Num())"));
                });
        }),
        ("Unknown or mutated containers are not allowed to supply automatic ADD expressions", () =>
        {
            foreach (string type in new[] { "Unknown", "TArray<int>&", "const TArray<TArray<int>>&" })
                WithSource("void F::Work(" + type + " Requests) { if (bOk) { Submit(); } }", (_, scan) => Check(scan.Candidates.Single().RecommendedQuantity is null));
            WithSource("void F::Work(const TArray<int>& Requests) { Modify(Requests); if (bOk) { Submit(); } }", (_, scan) => Check(scan.Candidates.Single().RecommendedQuantity is null));
        }),
        ("Scalar and container ambiguity produces no automatic recommendation", () => WithSource("void F::Work(uint32 NumBytes, const TArray<int>& Requests) { if (bOk) { for (int i=0;i<Requests.Num();++i) { Submit(); } } }", (_, scan) =>
        {
            var c = scan.Candidates.Single(); Check(c.AddValues.Count == 2 && c.RecommendedQuantity is null);
        })),
        ("Unrelated input arrays and nested guard branches do not receive automatic quantities", () =>
            WithSource("void F::Work(const TArray<int>& Requests) { if (bError) { return; } if (bOk) { if (bOther) { Log(); } for (const auto& Item : Requests) { Submit(Item); } } }", (_, scan) =>
            {
                Check(scan.Candidates.Count == 3 && scan.Candidates.Count(c => c.RecommendedQuantity is not null) == 1);
            }))
    ];
    private static void WithSource(string source, Action<EventCounterService, EventCounterScan> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "UEEtc-usability-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { File.WriteAllText(Path.Combine(root, "F.cpp"), "#include \"F.h\"\n" + source); var service = new EventCounterService(); action(service, service.Analyze(root)); }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Event usability assertion failed"); }
}
