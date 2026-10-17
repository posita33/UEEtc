using System.Text;
using UEEtc.Core.Features.CombinedTracing;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Plans;

internal static class CpuCustomizationTests
{
    private static readonly CpuTraceSettings Custom = new("PROJECT_TRACE_CPU_SCOPE_STR", "Tracing/ProjectCpuTrace.h");
    private const string Body = "void F::Work() { Super::Work(); if (Ready) { for (int i=0;i<4;++i) Use(); } }";
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Custom CPU macro receives a fixed literal and only its configured header", () =>
        {
            var file = Analyze();
            Check(file.ProposedText.StartsWith("#include \"Fixture.h\"\n#include \"Tracing/ProjectCpuTrace.h\"\n"));
            Check(file.ProposedText.Contains("PROJECT_TRACE_CPU_SCOPE_STR(\"F Work\")"));
            Check(!file.ProposedText.Contains("ProfilingDebugging/CpuProfilerTrace.h") && !file.ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE("));
            var row = file.Functions.Single();
            Check(row.CpuMacro == Custom.MacroName && row.CpuScopeLabel == "F Work" && row.CpuDecision.Contains(Custom.Header));
        }),
        ("Configured CPU header is not duplicated and exact include paths are distinguished", () =>
        {
            foreach (string include in new[] { "\"Tracing/ProjectCpuTrace.h\"", "<Tracing/ProjectCpuTrace.h>", "\"Tracing\\ProjectCpuTrace.h\"" })
                Check(Analyze("#include " + include + "\n").ProposedText.Split("#include").Length == 3);
            var different = Analyze("#include \"Other/ProjectCpuTrace.h\"\n");
            Check(different.ProposedText.Contains("#include \"Tracing/ProjectCpuTrace.h\""));
            Check(Analyze(settings: Custom with { Header = "Tracing\\ProjectCpuTrace.h" }).ProposedText.Contains("#include \"Tracing/ProjectCpuTrace.h\""));
        }),
        ("Default STR name can be provided by a project header", () =>
        {
            var file = Analyze(settings: new(Header: Custom.Header));
            Check(file.ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"F Work\")") && file.ProposedText.Contains(Custom.Header));
            Check(!file.ProposedText.Contains("ProfilingDebugging/CpuProfilerTrace.h"));
            Check(Analyze(settings: Custom with { MacroName = "TRACE_CPUPROFILER_EVENT_SCOPE_STR_MYPROJECT" }).ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR_MYPROJECT(\"F Work\")"));
        }),
        ("Custom CPU output is idempotent under the same settings", () =>
        {
            var first = Analyze();
            var second = new CppInstrumenter().Analyze("Fixture.cpp", first.ProposedBytes, new(CpuTrace: Custom));
            Check(!second.HasChanges && second.Functions.Single().CpuDecision.Contains("既存"));
        }),
        ("Custom CPU preserves existing Engine macros and ignores comment and literal lookalikes", () =>
        {
            foreach (string macro in new[] { "TRACE_CPUPROFILER_EVENT_SCOPE(Old)", "TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"Old\")", "TRACE_CPUPROFILER_EVENT_SCOPE_TEXT(Name)" })
                Check(!Analyze(body: "void F::Work() { " + macro + "; Run(); }").HasChanges);
            Check(Analyze(body: "void F::Work() { /* PROJECT_TRACE_CPU_SCOPE_STR(\"Fake\") */ Use(\"PROJECT_TRACE_CPU_SCOPE_STR()\"); }").AddedCpuScopes == 1);
        }),
        ("Invalid CPU macro and header settings fail before source traversal", () =>
        {
            foreach (string name in new[] { "", "1BAD", "BAD(X)", "Bad::Scope", "BAD\nCODE", "TRACE_CPUPROFILER_EVENT_SCOPE", "TRACE_CPUPROFILER_EVENT_SCOPE_TEXT" })
                Reject(() => new CombinedTracingService().Analyze("nonexistent", cpuTrace: new(name)));
            foreach (string header in new[] { "", "../Bad.h", "/Bad.h", "C:/Bad.h", "Bad\".h", "Bad\n.h", "a//b.h" })
                Reject(() => new InstrumentationService().Analyze("nonexistent", new(CpuTrace: new(Header: header))));
        }),
        ("Combined custom CPU preserves independent event generation and reanalysis", () => WithSource(root =>
        {
            var service = new CombinedTracingService(); var plan = service.Analyze(root, cpuTrace: Custom);
            Check(plan.AddedCpuScopes == 1 && plan.AddedEventCounters == 1);
            var file = plan.Files.Single();
            Check(file.ProposedText.Contains(Custom.Header) && file.ProposedText.Contains("TRACE_COUNTER_INCREMENT"));
            Check(File.ReadAllText(Path.Combine(root, "Fixture.cpp")) == file.OriginalText);
            File.WriteAllBytes(Path.Combine(root, "Fixture.cpp"), file.ProposedBytes);
            Check(service.Analyze(root, cpuTrace: Custom).ChangedFiles == 0);
        }))
    ];

    private static FileAnalysis Analyze(string extraInclude = "", CpuTraceSettings? settings = null, string body = Body) =>
        new CppInstrumenter().Analyze("Fixture.cpp", Encoding.UTF8.GetBytes("#include \"Fixture.h\"\n" + extraInclude + body), new(CpuTrace: settings ?? Custom));
    private static void WithSource(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-custom-cpu"));
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { File.WriteAllText(Path.Combine(root, "Fixture.cpp"), "#include \"Fixture.h\"\n" + Body); action(root); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary path");
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool value) { if (!value) throw new Exception("CPU customization assertion failed"); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid CPU settings rejection"); }
}
