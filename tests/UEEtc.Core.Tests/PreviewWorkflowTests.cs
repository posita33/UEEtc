using System.Text;
using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Reporting;
using UEEtc.Core.Source;

internal static class PreviewWorkflowTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("CPU insertions follow tabs, two spaces, four spaces and mixed local blocks", () =>
        {
            foreach (string indent in new[] { "\t", "  ", "    ", "\t  " })
            {
                string body = "#include \"F.h\"\r\nvoid F::Work()\r\n{\r\n" + indent + "Run();\r\n}\r\n";
                var file = new CppInstrumenter().Analyze("F.cpp", Encoding.UTF8.GetBytes(body), new(true, true));
                Check(file.ProposedText.Contains("\r\n" + indent + "TRACE_CPUPROFILER"));
                Check(file.ProposedText.Contains("\r\n" + indent + "TRACE_COUNTER_INCREMENT"));
                Check(!file.ProposedText.Replace("\r\n", "").Contains('\n'));
            }
        }),
        ("Counter declarations and nested branch insertions use spaces", () => WithScan("uint32 NumBytes", "    ", (service, scan, _) =>
        {
            var c = scan.Candidates.Single();
            var text = service.BuildPlan(scan, [new(c.Id, "Count", "Count", new("Bytes", "Bytes", "NumBytes", "bytes", true))]).Files.Single().ProposedText;
            Check(text.Contains("\n    TRACE_DECLARE_ATOMIC_INT_COUNTER"));
            Check(text.Contains("\n        TRACE_COUNTER_INCREMENT") && text.Contains("\n        TRACE_COUNTER_ADD"));
            Check(!text.Contains('\t'));
        })),
        ("Block indentation beats continuation alignment for declarations", () =>
        {
            const string text = "#include \"F.h\"\nvoid F::Work()\n{\n    Run(first,\n       second);\n}\n";
            var file = new CppInstrumenter().Analyze("F.cpp", Encoding.UTF8.GetBytes(text), new(true, true));
            Check(file.ProposedText.Contains("\n    TRACE_DECLARE_ATOMIC_INT_COUNTER"));
        }),
        ("Unconfirmed recommendations are previewable but cannot be applied", () => WithScan("uint32 NumBytes", "\t", (service, scan, root) =>
        {
            var c = scan.Candidates.Single(); Check(c.RecommendedQuantity is { Value: "NumBytes", Unit: "bytes" });
            var selection = new EventCounterSelection(c.Id, "Count", "Count", new("Bytes", "Bytes", "NumBytes", "bytes"));
            var preview = service.BuildPreview(scan, [selection]);
            Check(preview.RequiresQuantityConfirmation && preview.AddedQuantityCounters == 1);
            Reject(() => new SourceWorkspace().Apply(preview));
            Reject(() => service.BuildPlan(scan, [selection]));
            Check(File.ReadAllBytes(Path.Combine(root, "F.cpp")).AsSpan().SequenceEqual(scan.Snapshot.Files.Single().OriginalBytes));
            Check(!Directory.Exists(Path.Combine(root, ".ueetc-backups")));
            Check(!service.BuildPreview(scan, [selection with { Quantity = selection.Quantity! with { MeaningConfirmed = true } }]).RequiresQuantityConfirmation);
        })),
        ("Recommendations reject ambiguous units, duplicate choices and unsafe parameter types", () =>
        {
            foreach (string parameters in new[] { "uint32 Size", "uint32 NumBytes, uint32 ByteCount", "uint64 NumBytes", "int NumBytes", "uint32& NumBytes" })
                WithScan(parameters, "  ", (_, scan, _) => Check(scan.Candidates.Single().RecommendedQuantity is null));
            WithScan("uint32 ItemCount", "  ", (_, scan, _) => Check(scan.Candidates.Single().RecommendedQuantity?.Unit == "items"));
        }),
        ("Changed line anchors preserve repeated braces and separate insertion blocks", () =>
        {
            const string original = "header\nvoid A()\n{\n  One();\n}\nvoid B()\n{\n  Two();\n}\n";
            const string proposed = "header\ninclude\nvoid A()\n{\n  One();\n}\nvoid B()\n{\n  trace\n  Two();\n}\n";
            Check(ChangedLineMap.Compare(original, proposed).SequenceEqual(new[] { 2, 9 }));
            Check(ChangedLineMap.Compare(original, original).Count == 0);
            Check(ChangedLineMap.Compare(original.Replace("\n", "\r\n"), proposed).SequenceEqual(new[] { 2, 9 }));
            Check(ChangedLineMap.Compare("void A() { Run(); }", "void A() {\n trace;\n Run(); }").SequenceEqual(new[] { 1, 2, 3 }));
        }),
        ("Large change preview avoids a quadratic line matrix", () =>
        {
            string original = string.Join('\n', Enumerable.Range(0, 10000).Select(i => "Line " + i));
            string proposed = original.Replace("Line 5000", "Added\nLine 5000", StringComparison.Ordinal);
            Check(ChangedLineMap.Compare(original, proposed).SequenceEqual(new[] { 5001 }));
        })
    ];
    private static void WithScan(string parameters, string indent, Action<EventCounterService, EventCounterScan, string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "UEEtc-workflow-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "F.cpp"), "#include \"F.h\"\nvoid Work(" + parameters + ")\n{\n" + indent + "if (bSuccess)\n" + indent + "{\n" + indent + indent + "Run();\n" + indent + "}\n}\n");
            var service = new EventCounterService(); action(service, service.Analyze(root), root);
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Preview workflow assertion failed"); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return; } throw new Exception("Expected unconfirmed quantity rejection"); }
}
