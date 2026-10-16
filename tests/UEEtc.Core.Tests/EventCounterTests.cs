using System.Text;
using System.Text.Json;
using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Source;
using UEEtc.Core.Reporting;

internal static class EventCounterTests
{
    public static (string Name, Action Run)[] All =>
    [
        Test("Discovery changes nothing and initial selection is empty", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root);
            Check(scan.Candidates.Count == 2 && scan.Snapshot.ChangedFiles == 0);
            Check(service.CreateSelection(scan).Selections.Count == 0);
            Check(service.BuildPlan(scan, []).ChangedFiles == 0);
        }),
        Test("Only selected branch receives an event and never a CPU scope", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var c = scan.Candidates.Single(c => c.Branch == "Else");
            var plan = service.BuildPlan(scan, [Select(c)]); var file = plan.Files.Single();
            Check(plan.AddedEventCounters == 1 && plan.AddedCpuScopes == 0 && plan.AddedCallCounters == 0);
            Check(!file.ProposedText.Contains("CpuProfilerTrace.h") && !file.ProposedText.Contains("TRACE_CPUPROFILER"));
            Check(file.ProposedText.IndexOf("else", StringComparison.Ordinal) < file.ProposedText.IndexOf("TRACE_COUNTER_INCREMENT", StringComparison.Ordinal));
            Check(file.ProposedText.Contains("TRACE_COUNTER_INCREMENT(" + c.SuggestedName + ")"));
        }),
        Test("Names and Insights labels are editable and escaped", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var file = service.BuildPlan(scan, [new(scan.Candidates[0].Id, "CacheHitCount", "Cache/Hit \"verified\"")]).Files.Single();
            Check(file.ProposedText.Contains("TRACE_COUNTER_INCREMENT(CacheHitCount)"));
            Check(file.ProposedText.Contains("Cache/Hit \\\"verified\\\""));
        }),
        Test("Existing counter suppresses only its direct branch", root =>
        {
            Write(root, "void Work() { if (bOk) { TRACE_COUNTER_INCREMENT(Existing); Act(); } else { Fail(); } }");
            var scan = new EventCounterService().Analyze(root);
            Check(scan.Candidates.Count == 1 && scan.Candidates[0].Branch == "Else");
        }),
        Test("Existing CPU scopes do not suppress event discovery", root =>
        {
            Write(root, "void Work() { TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"Work\"); if (bOk) { Act(); } }");
            Check(new EventCounterService().Analyze(root).Candidates.Count == 1);
        }),
        Test("Reanalysis keeps selected branches instrumented without duplicating", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var plan = service.BuildPlan(scan, scan.Candidates.Select(Select));
            File.WriteAllBytes(Path.Combine(root, "Fixture.cpp"), plan.Files.Single().ProposedBytes);
            var second = service.Analyze(root);
            Check(second.Candidates.Count == 0 && second.Snapshot.ChangedFiles == 0);
            Check(service.BuildPlan(second, []).ChangedFiles == 0);
        }),
        Test("Nested candidate identity survives instrumentation of another branch", root =>
        {
            Write(root, "void Work() { if (bOk) { if (bHit) { Act(); } } else { Fail(); } }");
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var inner = scan.Candidates.Single(c => c.Condition == "bHit");
            var plan = service.BuildPlan(scan, [Select(inner)]);
            File.WriteAllBytes(Path.Combine(root, "Fixture.cpp"), plan.Files.Single().ProposedBytes);
            var second = service.Analyze(root);
            Check(second.Candidates.Select(c => c.Id).ToHashSet().SetEquals(scan.Candidates.Where(c => c.Id != inner.Id).Select(c => c.Id)));
        }),
        Test("Duplicate requested names and existing header identifiers are disambiguated", root =>
        {
            File.WriteAllText(Path.Combine(root, "Fixture.h"), "// __GTraceCounterEvents\n", new UTF8Encoding(false));
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var plan = service.BuildPlan(scan, scan.Candidates.Select(c => new EventCounterSelection(c.Id, "Events", "Branches")));
            var names = plan.Files.Single().Functions.Select(f => f.Counter!).ToArray();
            Check(names.Distinct().Count() == 2 && names.All(n => n.StartsWith("Events_", StringComparison.Ordinal)));
            Check(plan.Files.Single().ProposedText.Contains("TEXT(\"Branches ["));
        }),
        Test("Same signatures in different files receive unique allocated names", root =>
        {
            File.Copy(Path.Combine(root, "Fixture.cpp"), Path.Combine(root, "Other.cpp"));
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var plan = service.BuildPlan(scan, scan.Candidates.Select(Select));
            Check(plan.Files.SelectMany(f => f.Functions).Select(f => f.Counter).Distinct().Count() == 4);
        }),
        Test("Invalid and repeated IDs and invalid names are rejected", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root); var c = scan.Candidates[0];
            Throws<ArgumentException>(() => service.BuildPlan(scan, [Select(c), Select(c)]));
            Throws<ArgumentException>(() => service.BuildPlan(scan, [new("unknown", "Test", "Test")]));
            Throws<ArgumentException>(() => service.BuildPlan(scan, [new(c.Id, "bad-name", "Test")]));
            Throws<ArgumentException>(() => service.BuildPlan(scan, [new(c.Id, "Bad__Name", "Test")]));
            Throws<ArgumentException>(() => service.BuildPlan(scan, [new(c.Id, "Test", "bad\nlabel")]));
        }),
        Test("Selection JSON roundtrip and stale source fingerprint", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var selection = service.CreateSelection(scan) with { Selections = [Select(scan.Candidates[0])] };
            var roundtrip = JsonSerializer.Deserialize<EventCounterSelectionFile>(JsonSerializer.Serialize(selection))!;
            Check(service.BuildPlan(scan, roundtrip).AddedEventCounters == 1);
            File.AppendAllText(Path.Combine(root, "Fixture.cpp"), "\n// changed\n");
            Throws<InvalidOperationException>(() => service.BuildPlan(service.Analyze(root), roundtrip));
        }),
        Test("Selection fingerprint also invalidates after header name changes", root =>
        {
            File.WriteAllText(Path.Combine(root, "Fixture.h"), "int Existing;", new UTF8Encoding(false));
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var selection = service.CreateSelection(scan) with { Selections = [Select(scan.Candidates[0])] };
            File.AppendAllText(Path.Combine(root, "Fixture.h"), " int AddedName;");
            Throws<InvalidOperationException>(() => service.BuildPlan(service.Analyze(root), selection));
        }),
        Test("Unbraced branches, loops, lambdas, switch and constexpr are excluded", root =>
        {
            Write(root, """
                void Work() {
                    if (bOk) Act();
                    for (int i=0; i<5; ++i) { if (bOk) { Act(); } }
                    while (bOk) { if (bHit) { Act(); } }
                    for (;;) if (bOk) { Act(); }
                    while (bOk) if (bHit) { Act(); } else { Miss(); }
                    do if (bOk) { Act(); } while (bOk);
                    auto callback = [] { if (bOk) { Act(); } };
                    switch (Result) { case 1: if (bOk) { Act(); } }
                }
                void CompileTime() { if constexpr (true) { if (bOk) { Act(); } } else { Fail(); } }
                void Empty() {}
                """);
            Check(new EventCounterService().Analyze(root).Candidates.Count == 0);
        }),
        Test("Templates, constructors, inline classes and conditional code are excluded", root =>
        {
            Write(root, """
                F::F() { if (bOk) { Act(); } }
                template<class T> void Work(T t) { if (t) { Act(); } }
                class F { void Inline() { if (bOk) { Act(); } } };
                #if ENABLED
                void Conditional() { if (bOk) { Act(); } }
                #endif
                void InternalDirective() {
                #if ENABLED
                    if (bOk) { Act(); }
                #endif
                }
                """);
            Check(new EventCounterService().Analyze(root).Candidates.Count == 0);
        }),
        Test("Comment and literal fake branches are ignored", root =>
        {
            Write(root, "void Work() { auto s = R\"tag(if (bOk) { Fake(); })tag\"; /* if (bOk) { Fake(); } */ if (bOk) { Act(); } }");
            Check(new EventCounterService().Analyze(root).Candidates.Count == 1);
        }),
        Test("Else-if conditions remain independent and are not wrapped", root =>
        {
            Write(root, "void Work() { if (bFail) { Fail(); } else if (bHit) { Hit(); } else { Miss(); } }");
            var scan = new EventCounterService().Analyze(root);
            Check(scan.Candidates.Count == 3);
            Check(scan.Candidates.Single(c => c.Branch == "Else").Condition == "bHit");
        }),
        Test("Malformed syntax produces diagnostics and cannot be applied", root =>
        {
            Write(root, "void Work() { if (bOk) { Act(); }");
            var scan = new EventCounterService().Analyze(root);
            Check(scan.Snapshot.DiagnosticCount == 1 && scan.Candidates.Count == 0);
            Throws<InvalidOperationException>(() => new SourceWorkspace().Apply(scan.Snapshot));
        }),
        Test("A safe include anchor is required", root =>
        {
            File.WriteAllText(Path.Combine(root, "Fixture.cpp"), "void Work() { if (bOk) { Act(); } }", new UTF8Encoding(false));
            Check(new EventCounterService().Analyze(root).Candidates.Count == 0);
        }),
        Test("BOM and CRLF survive event insertion", root =>
        {
            const string text = "#include \"Fixture.h\"\r\nvoid Work() { if (bOk) { Act(); } }\r\n";
            File.WriteAllText(Path.Combine(root, "Fixture.cpp"), text, new UnicodeEncoding(false, true));
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var file = service.BuildPlan(scan, scan.Candidates.Select(Select)).Files.Single();
            Check(file.ProposedBytes[0] == 0xff && file.ProposedBytes[1] == 0xfe);
            Check(!file.ProposedText.Replace("\r\n", "").Contains('\n'));
        }),
        Test("Event apply retains backup and rejects stale previews", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var plan = service.BuildPlan(scan, scan.Candidates.Select(Select));
            string backup = new SourceWorkspace().Apply(plan);
            Check(File.ReadAllBytes(Path.Combine(backup, "Fixture.cpp")).SequenceEqual(plan.Files.Single().OriginalBytes));
            Throws<IOException>(() => new SourceWorkspace().Apply(plan));
        }),
        Test("CPU can be applied after event counters without pairing", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var plan = service.BuildPlan(scan, scan.Candidates.Select(Select));
            new SourceWorkspace().Apply(plan);
            var cpu = new InstrumentationService().Analyze(root);
            Check(cpu.AddedCpuScopes == 1 && cpu.AddedEventCounters == 0 && cpu.AddedCallCounters == 0);
            Check(cpu.Files.Single().ProposedText.Contains("TRACE_COUNTER_INCREMENT"));
        }),
        Test("Exported report identifies the feature and event sites", root =>
        {
            var service = new EventCounterService(); var scan = service.Analyze(root);
            var plan = service.BuildPlan(scan, scan.Candidates.Select(Select));
            Check(plan.Feature == "EventCounters" && ReportExporter.Summary(plan).Contains("イベント 2"));
            Check(ReportExporter.CreatePatch(plan).Contains("TRACE_COUNTER_INCREMENT"));
        }),
        Test("Cancellation and invalid source paths are rejected", root =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            Throws<OperationCanceledException>(() => new EventCounterService().Analyze(root, cancellationToken: cts.Token));
            Throws<ArgumentException>(() => new EventCounterService().Analyze("relative"));
        })
    ];

    private static (string, Action) Test(string name, Action<string> action) => ("Events: " + name, () =>
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-event-tests")) + Path.DirectorySeparatorChar;
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Write(root, "void Work() { if (bCacheHit) { Act(); } else { Fail(); } }"); action(root); }
        finally { if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary path"); Directory.Delete(root, true); }
    });
    private static EventCounterSelection Select(EventCounterCandidate c) => new(c.Id, c.SuggestedName, c.DisplayName);
    private static void Write(string root, string body) => File.WriteAllText(Path.Combine(root, "Fixture.cpp"), "#include \"Fixture.h\"\n" + body, new UTF8Encoding(false));
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
