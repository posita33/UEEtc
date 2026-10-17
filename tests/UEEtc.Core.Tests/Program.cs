using System.Text;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Plans;
using UEEtc.Core.Reporting;

var tests = new (string Name, Action Run)[]
{
    ("Scopes, counter declarations and primary include order", () =>
    {
        var file = Analyze("void F::Work() { Run(); }");
        Equal(1, file.AddedFunctions);
        Contains(file.ProposedText, "TRACE_CPUPROFILER_EVENT_SCOPE(");
        Contains(file.ProposedText, "TRACE_DECLARE_ATOMIC_INT_COUNTER");
        Contains(file.ProposedText, "TRACE_COUNTER_INCREMENT");
        Assert(file.ProposedText.StartsWith("#include \"Fixture.h\"\n#include \"ProfilingDebugging/CpuProfilerTrace.h\""));
    }),
    ("Idempotence", () =>
    {
        var first = Analyze("void F::Work() { Run(); }");
        var second = new CppInstrumenter().Analyze(first.RelativePath, first.ProposedBytes, new());
        Assert(!second.HasChanges);
    }),
    ("Comments, escaped strings, raw strings and lambdas", () =>
    {
        var file = Analyze("""
            // void Fake::Call() { }
            void F::Work() {
                auto text = u8R"tag({ fake(); " })tag";
                auto quoted = "\" } //";
                auto lambda = []() { Run(); };
                /* } void Fake::Call() { */
                lambda();
            }
            """);
        Equal(1, file.AddedFunctions);
        Equal(0, file.Diagnostics.Count);
    }),
    ("Constructors, destructors, empty functions and inline classes", () =>
    {
        var file = Analyze("F::F() : Value{1} { Init(); }\nF::~F() { Cleanup(); }\nvoid F::Empty() {}\nclass X { void Inline() { Work(); } };\nvoid F::Work() { Run(); }");
        Equal(1, file.AddedFunctions);
    }),
    ("Constexpr, consteval, templates and coroutine exclusion", () =>
    {
        var file = Analyze("constexpr int F::Value() { return 1; }\nconsteval int Value() { return 2; }\ntemplate<class T> T Value(T t) { return t; }\nTask Work() { co_return; }");
        Equal(0, file.AddedFunctions);
        Assert(file.Functions.Count >= 3);
    }),
    ("Existing CPU macros suppress CPU only, comments are ignored", () =>
    {
        var file = Analyze("void F::A() { TRACE_CPUPROFILER_EVENT_SCOPE(A); Run(); }\nvoid F::B() { QUICK_SCOPE_CYCLE_COUNTER(B); Run(); }\nvoid F::C() { /* TRACE_COUNTER_INCREMENT(C); */ Run(); }");
        Equal(3, file.AddedFunctions);
        Equal(1, file.AddedCpuScopes);
        Equal(3, file.AddedCallCounters);
        Equal("F::C", file.Functions.Single(f => f.CpuScopeAdded).Name);
    }),
    ("Overloads and namespace names have different counter IDs", () =>
    {
        var file = Analyze("void Work(int a) { Run(); }\nvoid Work(float a) { Run(); }\nnamespace A { void Work() { Run(); } }\nnamespace B { void Work() { Run(); } }");
        Equal(4, file.AddedFunctions);
        Equal(4, file.Functions.Select(f => f.Counter).Distinct().Count());
    }),
    ("Counter identifiers show class and function in declaration, increment and report", () =>
    {
        var file = Analyze("void ABlockBreakerGameMode::BeginPlay() { Run(); }");
        string counter = file.Functions.Single().Counter!;
        Equal("ABlockBreakerGameMode_BeginPlay_CallCount", counter);
        Contains(file.ProposedText, "TRACE_DECLARE_ATOMIC_INT_COUNTER(" + counter + ",");
        Contains(file.ProposedText, "TRACE_COUNTER_INCREMENT(" + counter + ");");
        Assert(System.Text.RegularExpressions.Regex.IsMatch(counter, @"^[A-Za-z][A-Za-z0-9_]*$"));
    }),
    ("Readable counter names avoid reserved double underscores", () =>
    {
        const string body = "#include \"Fixture.h\"\nvoid F::_Work_Impl() { Run(); }";
        var instrumenter = new CppInstrumenter();
        var first = instrumenter.Analyze("ModuleA/Fixture.cpp", Encoding.UTF8.GetBytes(body), new(true, true));
        var second = instrumenter.Analyze("ModuleB/Fixture.cpp", Encoding.UTF8.GetBytes(body), new(true, true));
        string name = first.Functions.Single().Counter!;
        Equal("F_Work_Impl_CallCount", name);
        Assert(!name.Contains("__", StringComparison.Ordinal));
        // Standalone analysis resolves only this file; whole-tree analysis handles cross-file collisions.
        Equal(name, second.Functions.Single().Counter);
    }),
    ("Only overloaded candidates receive hash suffixes", () =>
    {
        var file = Analyze("void F::Work(int a) { Run(); }\nvoid F::Work(float a) { Run(); }\nvoid F::Other() { Run(); }");
        var overloads = file.Functions.Where(f => f.Name == "F::Work").ToArray();
        Equal(2, overloads.Length);
        Assert(overloads.All(f => System.Text.RegularExpressions.Regex.IsMatch(f.Counter!, @"^F_Work_CallCount_[0-9A-F]{16}$")));
        Equal("F_Other_CallCount", file.Functions.Single(f => f.Name == "F::Other").Counter);
        Assert(overloads.All(f => f.Reason.Contains("衝突")));
    }),
    ("Namespaces contribute readable names without unnecessary hashes", () =>
    {
        var file = Analyze("namespace A { void Work() { Run(); } } namespace B { void Work() { Run(); } }");
        Equal("A_Work_CallCount", file.Functions[0].Counter);
        Equal("B_Work_CallCount", file.Functions[1].Counter);
    }),
    ("Normalized scoped names that collide remain independent", () =>
    {
        var file = Analyze("void A_B::Work() { Run(); }\nvoid A::B_Work() { Run(); }");
        Equal(2, file.Functions.Select(f => f.Counter).Distinct().Count());
        Assert(file.Functions.All(f => f.Counter!.StartsWith("A_B_Work_CallCount_", StringComparison.Ordinal)));
    }),
    ("Whole-tree naming avoids collisions across files deterministically", () => WithTemporaryRoot(root =>
    {
        Write(root, "B.cpp", "#include \"Fixture.h\"\nvoid A::B_Work() { Run(); }");
        Write(root, "A.cpp", "#include \"Fixture.h\"\nvoid A_B::Work() { Run(); }");
        var service = new InstrumentationService();
        var first = service.Analyze(root, new(true, true));
        var second = service.Analyze(root, new(true, true));
        var counters = first.Files.SelectMany(f => f.Functions).Select(f => f.Counter).ToArray();
        Equal(2, counters.Distinct().Count());
        Assert(counters.All(c => c!.StartsWith("A_B_Work_CallCount_", StringComparison.Ordinal)));
        Equal(string.Join(",", counters), string.Join(",", second.Files.SelectMany(f => f.Functions).Select(f => f.Counter)));
        Assert(first.Files.All(f => !f.ProposedText.Contains("UEEtcPendingCounter_")));
        service.Apply(first, Path.Combine(Path.GetDirectoryName(root)!, "Backups"));
        Equal(0, service.Analyze(root).ChangedFiles);
    })),
    ("Existing names and macro-expanded variable names in headers are reserved", () => WithTemporaryRoot(root =>
    {
        Write(root, "Work.cpp", "#include \"Fixture.h\"\nvoid F::Work() { Run(); }\nvoid F::Other() { Run(); }");
        Write(root, "Fixture.h", "TRACE_DECLARE_ATOMIC_INT_COUNTER(F_Work_CallCount, TEXT(\"Existing\"));\nint __GTraceCounterF_Other_CallCount;");
        var plan = new InstrumentationService().Analyze(root, new(true, true));
        Assert(plan.Files.Single().Functions.All(f => f.Counter!.Contains("_CallCount_", StringComparison.Ordinal)));
        Contains(File.ReadAllText(Path.Combine(root, "Fixture.h")), "F_Work_CallCount,");
    })),
    ("Reserved fallback names receive a further deterministic suffix", () => WithTemporaryRoot(root =>
    {
        const string source = "#include \"Fixture.h\"\nvoid Work(int a) { Run(); }\nvoid Work(float a) { Run(); }";
        var original = new CppInstrumenter().Analyze("Work.cpp", Encoding.UTF8.GetBytes(source), new(true, true));
        string reserved = original.Functions[0].Counter!;
        Write(root, "Work.cpp", source);
        Write(root, "Fixture.hpp", "int __GTraceCounter" + reserved + ";");
        var plan = new InstrumentationService().Analyze(root, new(true, true));
        Equal(reserved + "_2", plan.Files.Single().Functions[0].Counter);
    })),
    ("Intentional repeated increments preserve one existing shared counter", () =>
    {
        const string body = "TRACE_DECLARE_ATOMIC_INT_COUNTER(SharedHits, TEXT(\"Hits\"));\nvoid One() { TRACE_COUNTER_INCREMENT(SharedHits); }\nvoid Two() { TRACE_COUNTER_INCREMENT(SharedHits); }\nvoid NewWork() { Run(); }";
        var file = Analyze(body);
        Equal(3, file.AddedFunctions);
        Equal(3, file.AddedCpuScopes);
        Equal(1, file.AddedCallCounters);
        Equal(2, Occurrences(file.ProposedText, "TRACE_COUNTER_INCREMENT(SharedHits)"));
        Equal(1, Occurrences(file.ProposedText, "TRACE_DECLARE_ATOMIC_INT_COUNTER(SharedHits,"));
        Contains(file.ProposedText, "TRACE_COUNTER_INCREMENT(NewWork_CallCount)");
    }),
    ("Alternate conditional definitions share a single declaration", () =>
    {
        var file = Analyze("#if A\nvoid Work() { RunA(); }\n#else\nvoid Work() { RunB(); }\n#endif\n");
        Equal(2, file.AddedFunctions);
        Equal(1, Occurrences(file.ProposedText, "TRACE_DECLARE_ATOMIC_INT_COUNTER("));
    }),
    ("Ambiguous conditional brace structure is rejected", () =>
    {
        var file = Analyze("#if A\nvoid Work() {\n#else\nvoid Other() {\n#endif\nRun(); }\n");
        Assert(!file.HasChanges);
        Equal(1, file.Diagnostics.Count);
    }),
    ("Malformed literals and delimiters are rejected", () =>
    {
        foreach (string text in new[] { "void Work() {", "void Work() { /*", "void Work() { auto s = R\"(broken; }" })
        {
            var file = Analyze(text);
            Assert(!file.HasChanges && file.Diagnostics.Count == 1);
        }
    }),
    ("UTF-8 BOM, UTF-16 LE/BE and CRLF are retained", () =>
    {
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
        {
            string text = "#include \"Fixture.h\"\r\n// 日本語\r\nvoid Work() { Run(); }\r\n";
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
            var file = new CppInstrumenter().Analyze("Fixture.cpp", bytes, new());
            Equal(1, file.AddedFunctions);
            Assert(file.ProposedBytes.AsSpan().StartsWith(encoding.GetPreamble()));
            Assert(!file.ProposedText.Replace("\r\n", "").Contains('\n'));
            Contains(file.ProposedText, "日本語");
        }
    }),
    ("Non-UTF-8 and UTF-32 are diagnosed without conversion", () =>
    {
        foreach (byte[] bytes in new byte[][] { [0x82, 0xA0], [0xFF, 0xFE, 0, 0, 0x61, 0, 0, 0] })
        {
            var file = new CppInstrumenter().Analyze("Fixture.cpp", bytes, new());
            Assert(!file.HasChanges && file.Diagnostics.Count == 1);
        }
    }),
    ("Default options produce CPU only without counter dependencies", () =>
    {
        var file = new CppInstrumenter().Analyze("Fixture.cpp", Encoding.UTF8.GetBytes(Fixture()), new());
        Equal(1, file.AddedCpuScopes);
        Equal(0, file.AddedCallCounters);
        Assert(!file.ProposedText.Contains("CountersTrace.h") && !file.ProposedText.Contains("TRACE_DECLARE_") && !file.ProposedText.Contains("TRACE_COUNTER_"));
        Equal("未選択", file.Functions.Single().CounterDecision);
    }),
    ("Existing CPU instrumentation allows an explicitly requested counter", () =>
    {
        var file = Analyze("void F::Work() { TRACE_CPUPROFILER_EVENT_SCOPE(Work); Run(); }");
        Equal(0, file.AddedCpuScopes);
        Equal(1, file.AddedCallCounters);
        Equal(0, Occurrences(file.ProposedText, "CpuProfilerTrace.h"));
        Equal(1, Occurrences(file.ProposedText, "TRACE_CPUPROFILER_EVENT_SCOPE(Work)"));
        Equal("F_Work_CallCount", file.Functions.Single().Counter);
        Assert(file.Functions.Single().CpuDecision.Contains("既存"));
    }),
    ("Existing event counter allows CPU time without inferring counter meaning", () =>
    {
        var file = Analyze("void F::Work() { if (Hit) { TRACE_COUNTER_INCREMENT(CacheHits); } Run(); }");
        Equal(1, file.AddedCpuScopes);
        Equal(0, file.AddedCallCounters);
        Contains(file.Functions.Single().CounterDecision, "自動推定しない");
        Assert(!file.ProposedText.Contains("TRACE_DECLARE_") && !file.ProposedText.Contains("CountersTrace.h"));
        Equal(1, Occurrences(file.ProposedText, "TRACE_COUNTER_INCREMENT(CacheHits)"));
    }),
    ("Both kinds existing are preserved without changes", () =>
    {
        var file = Analyze("void Work() { TRACE_CPUPROFILER_EVENT_SCOPE(Work); TRACE_COUNTER_INCREMENT(Calls); Run(); }");
        Assert(!file.HasChanges);
        Equal(0, file.AddedFunctions);
        Assert(file.Functions.Single().CpuDecision.Contains("既存") && file.Functions.Single().CounterDecision.Contains("既存"));
    }),
    ("CPU-first then both adds only the counter and remains idempotent", () =>
    {
        var instrumenter = new CppInstrumenter();
        var cpu = instrumenter.Analyze("Fixture.cpp", Encoding.UTF8.GetBytes(Fixture()), new());
        var both = instrumenter.Analyze("Fixture.cpp", cpu.ProposedBytes, new(true, true));
        Equal(0, both.AddedCpuScopes);
        Equal(1, both.AddedCallCounters);
        Equal(1, Occurrences(both.ProposedText, "TRACE_CPUPROFILER_EVENT_SCOPE("));
        Equal(1, Occurrences(both.ProposedText, "TRACE_COUNTER_INCREMENT("));
        Assert(!instrumenter.Analyze("Fixture.cpp", both.ProposedBytes, new(true, true)).HasChanges);
    }),
    ("Counter-first then both adds only CPU and remains idempotent", () =>
    {
        var instrumenter = new CppInstrumenter();
        var counter = instrumenter.Analyze("Fixture.cpp", Encoding.UTF8.GetBytes(Fixture()), new(false, true));
        var both = instrumenter.Analyze("Fixture.cpp", counter.ProposedBytes, new(true, true));
        Equal(1, both.AddedCpuScopes);
        Equal(0, both.AddedCallCounters);
        Equal(1, Occurrences(both.ProposedText, "TRACE_DECLARE_ATOMIC_INT_COUNTER("));
        Equal(1, Occurrences(both.ProposedText, "TRACE_COUNTER_INCREMENT("));
        Assert(!instrumenter.Analyze("Fixture.cpp", both.ProposedBytes, new(true, true)).HasChanges);
    }),
    ("Unselected missing kind does not create an automatic pair", () =>
    {
        var cpu = Analyze("void Work() { TRACE_CPUPROFILER_EVENT_SCOPE(Work); Run(); }", new(true, false));
        var counter = Analyze("void Work() { TRACE_COUNTER_INCREMENT(Hits); Run(); }", new(false, true));
        Assert(!cpu.HasChanges && !counter.HasChanges);
    }),
    ("CPU only and counter only", () =>
    {
        var cpu = Analyze("void Work() { Run(); }", new(true, false));
        Contains(cpu.ProposedText, "TRACE_CPUPROFILER_EVENT_SCOPE(");
        Assert(!cpu.ProposedText.Contains("TRACE_COUNTER_INCREMENT"));
        Equal(1, cpu.AddedCpuScopes);
        Equal(0, cpu.AddedCallCounters);
        var counter = Analyze("void Work() { Run(); }", new(false, true));
        Contains(counter.ProposedText, "TRACE_COUNTER_INCREMENT");
        Assert(!counter.ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR"));
        Assert(!counter.ProposedText.Contains("CpuProfilerTrace.h"));
        Equal(0, counter.AddedCpuScopes);
        Equal(1, counter.AddedCallCounters);
    }),
    ("Existing includes do not get duplicated", () =>
    {
        var file = Analyze("#include \"ProfilingDebugging/CpuProfilerTrace.h\"\n#include \"ProfilingDebugging/CountersTrace.h\"\nvoid Work() { Run(); }");
        Equal(1, Occurrences(file.ProposedText, "#include \"ProfilingDebugging/CpuProfilerTrace.h\""));
        Equal(1, Occurrences(file.ProposedText, "#include \"ProfilingDebugging/CountersTrace.h\""));
        Assert(file.ProposedText.IndexOf("CountersTrace.h", StringComparison.Ordinal) <
            file.ProposedText.IndexOf("TRACE_DECLARE_ATOMIC_INT_COUNTER", StringComparison.Ordinal));
    }),
    ("Conditional include does not suppress required unconditional include", () =>
    {
        var file = Analyze("#if CONDITION\n#include \"ProfilingDebugging/CountersTrace.h\"\n#endif\nvoid Work() { Run(); }");
        Equal(2, Occurrences(file.ProposedText, "#include \"ProfilingDebugging/CountersTrace.h\""));
    }),
    ("No unconditional include means no rewrite", () =>
    {
        var file = new CppInstrumenter().Analyze("Fixture.cpp", Encoding.UTF8.GetBytes("void Work() { Run(); }"), new());
        Assert(!file.HasChanges);
        Equal(1, file.Functions.Count);
    }),
    ("Recursive discovery excludes generated, ThirdParty and header files", () => WithTemporaryRoot(root =>
    {
        Write(root, "Module/Work.cpp", Fixture());
        Write(root, "Module/Other.CPP", Fixture());
        Write(root, "Module/Work.h", Fixture());
        Write(root, "Module/Work.gen.cpp", Fixture());
        Write(root, "Intermediate/Work.cpp", Fixture());
        Write(root, "ThirdParty/Work.cpp", Fixture());
        Write(root, ".ueetc-backups/old/Work.cpp", Fixture());
        var plan = new InstrumentationService().Analyze(root);
        Equal(2, plan.Files.Count);
        Equal(2, plan.ChangedFiles);
        Assert(File.ReadAllText(Path.Combine(root, "Module/Work.cpp")) == Fixture());
    })),
    ("Export is separate, does not overwrite and preserves source bytes", () => WithTemporaryRoot(root =>
    {
        Write(root, "Source/Work.cpp", Fixture());
        string source = Path.Combine(root, "Source");
        string output = Path.Combine(root, "Export");
        var plan = new InstrumentationService().Analyze(source);
        ReportExporter.Export(plan, output);
        Assert(File.Exists(Path.Combine(output, "modified/Work.cpp")));
        Assert(File.ReadAllBytes(Path.Combine(source, "Work.cpp")).AsSpan().SequenceEqual(plan.Files[0].OriginalBytes));
        Throws<IOException>(() => ReportExporter.Export(plan, output));
        Throws<IOException>(() => ReportExporter.Export(plan, Path.Combine(source, "Output")));
        Contains(File.ReadAllText(Path.Combine(output, "changes.patch")), "@@ -1,");
    })),
    ("Explicit apply creates byte-identical backups and is idempotent", () => WithTemporaryRoot(root =>
    {
        Write(root, "Module/Work.cpp", Fixture());
        var service = new InstrumentationService();
        var plan = service.Analyze(root);
        string backup = service.Apply(plan, Path.Combine(Path.GetDirectoryName(root)!, "Backups"));
        Assert(File.ReadAllBytes(Path.Combine(backup, "Module/Work.cpp")).AsSpan().SequenceEqual(plan.Files[0].OriginalBytes));
        Assert(File.ReadAllBytes(Path.Combine(root, "Module/Work.cpp")).AsSpan().SequenceEqual(plan.Files[0].ProposedBytes));
        Equal(0, service.Analyze(root).ChangedFiles);
    })),
    ("Apply rejects stale plans before writing any source", () => WithTemporaryRoot(root =>
    {
        Write(root, "A.cpp", Fixture());
        Write(root, "B.cpp", Fixture());
        var service = new InstrumentationService();
        var plan = service.Analyze(root);
        Write(root, "B.cpp", Fixture() + "\n// edited");
        Throws<IOException>(() => service.Apply(plan));
        Equal(Fixture(), File.ReadAllText(Path.Combine(root, "A.cpp")));
    })),
    ("Read-only source prevents apply", () => WithTemporaryRoot(root =>
    {
        Write(root, "Work.cpp", Fixture());
        string path = Path.Combine(root, "Work.cpp");
        var service = new InstrumentationService();
        var plan = service.Analyze(root);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try { Throws<IOException>(() => service.Apply(plan)); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    })),
    ("Bad root and cancellation are handled", () =>
    {
        Throws<ArgumentException>(() => new InstrumentationService().Analyze("relative"));
        WithTemporaryRoot(root =>
        {
            Throws<ArgumentException>(() => new InstrumentationService().Analyze(root, new(false, false)));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Throws<OperationCanceledException>(() => new InstrumentationService().Analyze(root, cancellationToken: cts.Token));
        });
    }),
    ("An include after function definitions is not a safe declaration anchor", () =>
    {
        var file = new CppInstrumenter().Analyze("Fixture.cpp", Encoding.UTF8.GetBytes(
            "void Work() { Run(); }\n#include \"Fixture.h\"\n"), new());
        Assert(!file.HasChanges);
    }),
    ("Core apply rejects plans with file diagnostics", () => WithTemporaryRoot(root =>
    {
        Write(root, "Work.cpp", Fixture());
        Write(root, "Invalid.cpp", "#include \"Fixture.h\"\nvoid Broken() {");
        var service = new InstrumentationService();
        var plan = service.Analyze(root);
        Equal(1, plan.DiagnosticCount);
        Throws<InvalidOperationException>(() => service.Apply(plan));
        Equal(Fixture(), File.ReadAllText(Path.Combine(root, "Work.cpp")));
    })),
    ("Numeric digit separators are not character literals", () =>
    {
        var file = Analyze("int Work() { return 1'000 + 0xFF'AA; }");
        Equal(1, file.AddedFunctions);
        Equal(0, file.Diagnostics.Count);
    }),
    ("Newline-free tail is represented in the review patch", () =>
    {
        var file = Analyze("void Work() { Run(); }");
        var plan = new AnalysisPlan("unused", "FunctionTracing", [file], []);
        Contains(ReportExporter.CreatePatch(plan), "\\ No newline at end of file");
    })
};

tests = tests.Concat(EventCounterTests.All).ToArray();
tests = tests.Concat(CpuPolicyTests.All).ToArray();
tests = tests.Concat(FocusAndQuantityTests.All).ToArray();
tests = tests.Concat(PreviewWorkflowTests.All).ToArray();
tests = tests.Concat(EventUsabilityTests.All).ToArray();
tests = tests.Concat(TraceLifetimeTests.All).ToArray();
tests = tests.Concat(AutomaticCounterTests.All).ToArray();
tests = tests.Concat(CombinedTracingTests.All).ToArray();
tests = tests.Concat(EngineAuditTests.All).ToArray();
tests = tests.Concat(BackupStorageTests.All).ToArray();
int failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static string Fixture() => "#include \"Fixture.h\"\nvoid Work() { Run(); }\n";
static FileAnalysis Analyze(string body, InstrumentationOptions? options = null) =>
    new CppInstrumenter().Analyze("Module/Fixture.cpp", Encoding.UTF8.GetBytes("#include \"Fixture.h\"\n" + body), options ?? new(true, true));
static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
static void Contains(string value, string part) => Assert(value.Contains(part, StringComparison.Ordinal));
static int Occurrences(string value, string part) => value.Split(part, StringSplitOptions.None).Length - 1;
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected exception {typeof(T).Name}");
}
static void Write(string root, string relative, string text)
{
    string path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text, new UTF8Encoding(false));
}
static void WithTemporaryRoot(Action<string> action)
{
    string container = Path.Combine(Path.GetTempPath(), "UEEtc-tests", Guid.NewGuid().ToString("N"));
    string root = Path.Combine(container, "Source");
    Directory.CreateDirectory(root);
    try { action(root); }
    finally
    {
        // Only delete the exact temporary root created by this test.
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(container).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid temporary path");
        Directory.Delete(container, recursive: true);
    }
}
