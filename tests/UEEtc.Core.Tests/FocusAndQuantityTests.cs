using System.Text;
using System.Text.Json;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Features.EventCounters;

internal static class FocusAndQuantityTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Focused CPU keeps loops, processing and multi-action work", () =>
        {
            Check(Cpu("for (int i=0;i<3;++i) Run();") == 1);
            Check(Cpu("Serialize(Archive);") == 1);
            Check(Cpu("Prepare(); Build(); Submit(); Finish();") == 1);
        }),
        ("Focused CPU skips tiny methods, queries and deferred lambdas", () =>
        {
            Check(Cpu("return GetValue();") == 0);
            Check(Cpu("GetA(); IsB(); HasC(); CanD();") == 0);
            Check(Cpu("auto f = []() noexcept(true) -> void { for (;;) Run(); }; Register(f);") == 0);
            Check(Cpu("auto text = \"for (;;) Serialize();\"; /* Wait(); */ Run();") == 0);
        }),
        ("Focused CPU evaluates only the region after Super", () =>
        {
            Check(Cpu("for (int i=0;i<3;++i) Prepare(); Super::Work(); Run();") == 0);
            Check(Cpu("Super::Work(); for (int i=0;i<3;++i) Run();") == 1);
            var broad = new CppInstrumenter().Analyze("F.cpp", Encoding.UTF8.GetBytes("#include \"Fixture.h\"\nvoid F::Work() { Run(); }"), new());
            Check(broad.AddedCpuScopes == 1);
        }),
        ("ADD is opt-in, separate, unit labelled and idempotent", () => WithScan("uint32 Size", "", (service, scan, root) =>
        {
            var c = scan.Candidates.Single(); Check(c.AddValues.SequenceEqual(new[] { "Size" }));
            var count = new EventCounterSelection(c.Id, "SuccessCount", "IO/Success");
            Check(service.BuildPlan(scan, [count]).AddedQuantityCounters == 0);
            var selection = count with { Quantity = new("Bytes", "IO/Transferred", "Size", "bytes", true) };
            var plan = service.BuildPlan(scan, [selection]);
            Check(plan.AddedEventCounters == 1 && plan.AddedQuantityCounters == 1);
            var text = plan.Files.Single().ProposedText;
            Check(text.Contains("TRACE_COUNTER_ADD(Bytes, Size)") && text.Contains("IO/Transferred [bytes]"));
            Check(plan.Files.Single().Functions.Single().QuantityValue == "Size");
            File.WriteAllBytes(Path.Combine(root, "F.cpp"), plan.Files.Single().ProposedBytes);
            Check(service.Analyze(root).Candidates.Count == 0);
            Check(JsonSerializer.Deserialize<EventCounterSelection>(JsonSerializer.Serialize(selection)) == selection);
        })),
        ("ADD requires semantic confirmation and rejects arbitrary expressions", () => WithScan("uint32 Size", "", (service, scan, _) =>
        {
            var c = scan.Candidates.Single();
            foreach (string value in new[] { "Size++", "GetSize()", "Size * 2", "Unknown", "-1", "4294967296" })
                Throws(() => service.BuildPlan(scan, [new(c.Id, "Count", "Count", new("Amount", "Amount", value, "bytes", true))]));
            Throws(() => service.BuildPlan(scan, [new(c.Id, "Count", "Count", new("Amount", "Amount", "Size", "bytes"))]));
            Throws(() => service.BuildPlan(scan, [new(c.Id, "Count", "Count", new("Amount", "Amount", "Size", "", true))]));
            Check(service.BuildPlan(scan, [new(c.Id, "Count", "Count", new("Amount", "Amount", "4294967295", "items", true))]).AddedQuantityCounters == 1);
        })),
        ("ADD excludes unsafe types, prior uses and shadows", () =>
        {
            foreach (string type in new[] { "int Size", "uint64 Size", "uint32& Size", "Alias Size", "void (*Callback)(uint32 Size)" })
                WithScan(type, "", (_, scan, _) => Check(scan.Candidates.Single().AddValues.Count == 0));
            WithScan("uint32 Size", "Read(Size);", (_, scan, _) => Check(scan.Candidates.Single().AddValues.Count == 0));
        }),
        ("Count and quantity identifiers cannot collide", () => WithScan("uint32 Size", "", (service, scan, _) =>
        {
            var c = scan.Candidates.Single();
            var row = service.BuildPlan(scan, [new(c.Id, "Metric", "Metric", new("Metric", "Metric", "Size", "items", true))]).Files.Single().Functions.Single();
            Check(row.Counter != row.QuantityCounter);
        }))
    ];
    private static int Cpu(string body) => new CppInstrumenter().Analyze("F.cpp", Encoding.UTF8.GetBytes("#include \"Fixture.h\"\nvoid F::Work() { " + body + " }"), new(FocusedCpu: true)).AddedCpuScopes;
    private static void WithScan(string parameter, string before, Action<EventCounterService, EventCounterScan, string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "UEEtc-quantity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "F.cpp"), "#include \"Fixture.h\"\nvoid Work(" + parameter + ") { " + before + " if (bSuccess) { Submit(); } }");
            var service = new EventCounterService(); action(service, service.Analyze(root), root);
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Focused CPU / quantity assertion failed"); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid quantity rejection"); }
}
