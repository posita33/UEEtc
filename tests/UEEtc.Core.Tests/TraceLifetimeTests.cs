using System.Text;
using System.Text.Json;
using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Features.FunctionTracing;

internal static class TraceLifetimeTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Auto and plain CPU macros keep the same fixed label", () =>
        {
            foreach (var kind in new[] { CpuMacroKind.Auto, CpuMacroKind.Scope })
            {
                var f = Cpu("void AThing::Work() { Run(); }", kind);
                Check(f.ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE(AThing Work)"));
                Check(f.AddedPlainCpuScopes == 1 && f.AddedStringCpuScopes == 0 && f.Functions.Single().CpuScopeLabel == "AThing Work");
            }
            var str = Cpu("void AThing::Work() { Run(); }", CpuMacroKind.StaticString);
            Check(str.ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"AThing Work\")") && str.AddedStringCpuScopes == 1);
            Check(str.Functions.Single().CpuDecision.Contains("静的ID"));
        }),
        ("Unsafe plain text falls back to a fixed string without generating dynamic TEXT", () =>
        {
            foreach (var kind in new[] { CpuMacroKind.Auto, CpuMacroKind.Scope })
            {
                var f = Cpu("void F::Wörk() { Run(); }", kind);
                Check(f.AddedStringCpuScopes == 1 && f.ProposedText.Contains("SCOPE_STR(\"F Wörk\")") && !f.ProposedText.Contains("SCOPE_TEXT"));
            }
        }),
        ("Existing STR, SCOPE and dynamic TEXT are preserved under every choice", () =>
        {
            foreach (string macro in new[] { "TRACE_CPUPROFILER_EVENT_SCOPE(Existing)", "TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"Existing\")", "TRACE_CPUPROFILER_EVENT_SCOPE_TEXT(Name)" })
                foreach (var kind in Enum.GetValues<CpuMacroKind>()) Check(!Cpu("void F::Work() { " + macro + "; Run(); }", kind).HasChanges);
        }),
        ("Existing counter instrumentation does not qualify a tiny function for focused CPU tracing", () =>
        {
            const string text = "#include \"F.h\"\nvoid F::Work() { TRACE_COUNTER_INCREMENT(A); ON_SCOPE_EXIT { TRACE_COUNTER_DECREMENT(A); }; TRACE_COUNTER_ADD(B, 1); Run(); }";
            var f = new CppInstrumenter().Analyze("F.cpp", Encoding.UTF8.GetBytes(text), new(FocusedCpu: true));
            Check(f.AddedCpuScopes == 0);
        }),
        ("Cumulative events never get an automatic decrement", () => Scan((service, scan, _) =>
        {
            var c = scan.Candidates[0]; var plan = service.BuildPlan(scan, [new(c.Id, "Calls", "F Work if1")]);
            Check(plan.AddedDecrements == 0 && !plan.Files.Single().ProposedText.Contains("TRACE_COUNTER_DECREMENT"));
        })),
        ("Active scope adds an exit guard, a matching counter and the required include", () => Scan((service, scan, root) =>
        {
            var c = scan.Candidates[0];
            var selection = new EventCounterSelection(c.Id, "CurrentScopes", "F Work if1 ActiveScopes", Measurement: CounterMeasurement.ActiveScope);
            var plan = service.BuildPlan(scan, [selection]); var file = plan.Files.Single();
            Check(plan.AddedDecrements == 1 && file.Functions.Single().CounterMeasurement == "ActiveScope");
            Check(file.ProposedText.Contains("#include \"Misc/ScopeExit.h\""));
            Check(file.ProposedText.Contains("TRACE_COUNTER_INCREMENT(CurrentScopes)") && file.ProposedText.Contains("TRACE_COUNTER_DECREMENT(CurrentScopes)"));
            Check(file.ProposedText.IndexOf("ON_SCOPE_EXIT", StringComparison.Ordinal) < file.ProposedText.IndexOf("if (Early)", StringComparison.Ordinal));
            File.WriteAllBytes(Path.Combine(root, "F.cpp"), file.ProposedBytes);
            var remaining = service.Analyze(root);
            Check(remaining.Candidates.All(r => r.Id != c.Id) && remaining.Candidates.Any(r => r.Branch == "Else"));
            string json = JsonSerializer.Serialize(selection, EventCounterService.JsonOptions);
            Check(json.Contains("\"ActiveScope\"") && JsonSerializer.Deserialize<EventCounterSelection>(json, EventCounterService.JsonOptions) == selection);
        })),
        ("Existing ScopeExit include is not duplicated", () => Scan((service, scan, root) =>
        {
            string path = Path.Combine(root, "F.cpp"); File.WriteAllText(path, File.ReadAllText(path).Replace("#include \"F.h\"", "#include \"F.h\"\n#include \"Misc/ScopeExit.h\"", StringComparison.Ordinal));
            scan = service.Analyze(root); var c = scan.Candidates[0];
            string text = service.BuildPlan(scan, [new(c.Id, "Active", "Active", Measurement: CounterMeasurement.ActiveScope)]).Files.Single().ProposedText;
            Check(text.Split("#include \"Misc/ScopeExit.h\"").Length == 2);
        })),
        ("Bad measurement modes are validation results", () => Scan((service, scan, _) =>
        {
            var c = scan.Candidates[0]; var bad = new EventCounterSelection(c.Id, "Active", "Active", Measurement: (CounterMeasurement)99);
            Check(service.TryBuildPreview(scan, [bad]).Plan is null);
        }))
    ];
    private static UEEtc.Core.Plans.FileAnalysis Cpu(string source, CpuMacroKind kind) => new CppInstrumenter().Analyze("F.cpp", Encoding.UTF8.GetBytes("#include \"F.h\"\n" + source), new(CpuMacro: kind));
    private static void Scan(Action<EventCounterService, EventCounterScan, string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "UEEtc-lifetime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "F.cpp"), "#include \"F.h\"\nvoid F::Work() { if (Ready) { if (Early) return; Run(); } else { Fail(); } }");
            var service = new EventCounterService(); action(service, service.Analyze(root), root);
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Trace lifetime assertion failed"); }
}
