using System.Text;
using System.Text.Json;
using UEEtc.Core.Features.CombinedTracing;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Source;

internal static class CombinedTracingTests
{
    private const string Batch = "#include \"Fixture.h\"\nvoid F::Work(const TArray<int>& Items) { Super::Work(); if (Ready) { for (auto Item : Items) { Use(Item); } } }\n";
    public static (string Name, Action Run)[] All =>
    [
        ("Combined plan contains CPU, events, quantities and matching scope decrements", () => WithSource(Batch, root =>
        {
            var plan = new CombinedTracingService().Analyze(root); var f = plan.Files.Single();
            Check(plan.Feature == "CombinedTracing" && plan.AddedCpuScopes == 1 && plan.AddedEventCounters == 1 && plan.AddedQuantityCounters == 1 && plan.AddedDecrements == 1);
            Check(plan.AddedCallCounters == 0 && plan.DiagnosticCount == 0 && !plan.RequiresQuantityConfirmation);
            Check(f.ProposedText.IndexOf("Super::Work", StringComparison.Ordinal) < f.ProposedText.IndexOf("TRACE_CPUPROFILER_EVENT_SCOPE(", StringComparison.Ordinal));
            foreach (string header in new[] { "CpuProfilerTrace.h", "CountersTrace.h", "ScopeExit.h" }) Check(f.ProposedText.Split(header).Length == 2);
            Check(f.ProposedText.StartsWith("#include \"Fixture.h\"\n#include \"ProfilingDebugging/CpuProfilerTrace.h\"", StringComparison.Ordinal));
            Check(f.OriginalText == Batch && File.ReadAllText(Path.Combine(root, "A.cpp")) == Batch);
            Check(!JsonSerializer.Serialize(plan).Contains("Insertions", StringComparison.Ordinal));
        })),
        ("Combined focus uses original code rather than generated counter calls", () => WithSource(
            "#include \"Fixture.h\"\nvoid F::Work(uint32 NumBytes) { if (Ready) { Use(); } }", root =>
            {
                var plan = new CombinedTracingService().Analyze(root);
                Check(plan.AddedCpuScopes == 0 && plan.AddedEventCounters == 1 && plan.AddedQuantityCounters == 1);
                Check(new CombinedTracingService().Analyze(root, focusedCpu: false).AddedCpuScopes == 1);
            })),
        ("Combined tracing preserves existing instrumentation independently", () =>
        {
            WithSource("#include \"Fixture.h\"\nvoid F::Work() { TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"Existing\"); if (Ready) { Use(); } }", root => {
                var plan = new CombinedTracingService().Analyze(root, focusedCpu: false);
                Check(plan.AddedCpuScopes == 0 && plan.AddedEventCounters == 1 && plan.Files.Single().ProposedText.Contains("SCOPE_STR(\"Existing\")"));
            });
            WithSource("#include \"Fixture.h\"\nvoid F::Work() { if (Ready) { TRACE_COUNTER_INCREMENT(Existing); for (int i=0;i<3;++i) Use(); } }", root => {
                var plan = new CombinedTracingService().Analyze(root);
                Check(plan.AddedCpuScopes == 1 && plan.AddedEventCounters == 0);
            });
        }),
        ("Combined output reanalysis adds neither feature twice", () => WithSource(Batch, root =>
        {
            var service = new CombinedTracingService(); var first = service.Analyze(root);
            File.WriteAllBytes(Path.Combine(root, "A.cpp"), first.Files.Single().ProposedBytes);
            Check(service.Analyze(root).ChangedFiles == 0);
        })),
        ("Combined generation preserves BOM, CRLF and explicit STR choice", () => WithSource(Batch, root =>
        {
            var utf8 = new UTF8Encoding(true); string text = Batch.Replace("\n", "\r\n", StringComparison.Ordinal);
            File.WriteAllBytes(Path.Combine(root, "A.cpp"), [..utf8.GetPreamble(), ..utf8.GetBytes(text)]);
            var plan = new CombinedTracingService().Analyze(root, cpuMacro: CpuMacroKind.StaticString); var f = plan.Files.Single();
            Check(plan.AddedStringCpuScopes == 1 && f.ProposedBytes.AsSpan().StartsWith(utf8.GetPreamble()));
            Check(!f.ProposedText.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'));
        })),
        ("Combined scan visits each cpp once and avoids cross-file counter collisions", () => WithSource(Batch, root =>
        {
            File.WriteAllText(Path.Combine(root, "B.cpp"), Batch); var progress = new Visits();
            var plan = new CombinedTracingService().Analyze(root, progress: progress);
            Check(progress.Paths.Count == 2 && progress.Paths.Distinct().Count() == 2);
            Check(plan.Files.SelectMany(f => f.Functions).Where(f => f.EventCounterAdded).Select(f => f.Counter).Distinct().Count() == 2);
        })),
        ("Combined plan backs up original bytes once for both features and only applies selected files", () => WithSource(Batch, root =>
        {
            File.WriteAllText(Path.Combine(root, "B.cpp"), Batch);
            var plan = new CombinedTracingService().Analyze(root); var selected = plan with { Files = plan.Files.Where(f => f.RelativePath == "A.cpp").ToArray() };
            string backup = new SourceWorkspace().Apply(selected);
            Check(File.ReadAllText(Path.Combine(backup, "A.cpp")) == Batch && Directory.GetFiles(backup, "*.cpp", SearchOption.AllDirectories).Length == 1);
            Check(File.ReadAllText(Path.Combine(root, "A.cpp")).Contains("TRACE_CPUPROFILER") && File.ReadAllText(Path.Combine(root, "A.cpp")).Contains("TRACE_COUNTER_ADD"));
            Check(File.ReadAllText(Path.Combine(root, "B.cpp")) == Batch);
        })),
        ("Combined plan rejects stale source before applying either feature", () => WithSource(Batch, root =>
        {
            var plan = new CombinedTracingService().Analyze(root); File.AppendAllText(Path.Combine(root, "A.cpp"), "// Changed\n");
            try { new SourceWorkspace().Apply(plan); throw new Exception("Expected stale plan rejection"); } catch (IOException) { }
            Check(!Directory.Exists(Path.Combine(root, ".ueetc-backups")));
        })),
        ("Combined malformed input remains diagnosed and cancellable", () => WithSource("#include \"Fixture.h\"\nvoid F::Work() {", root =>
        {
            var plan = new CombinedTracingService().Analyze(root); Check(plan.DiagnosticCount > 0 && plan.ChangedFiles == 0);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { new CombinedTracingService().Analyze(root, cancellationToken: cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        }))
    ];
    private sealed class Visits : IProgress<string>
    {
        public List<string> Paths { get; } = [];
        public void Report(string value) => Paths.Add(value);
    }
    private static void WithSource(string source, Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "UEEtc-combined-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { File.WriteAllText(Path.Combine(root, "A.cpp"), source); action(root); }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Combined tracing assertion failed"); }
}
