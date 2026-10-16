using System.Text;
using UEEtc.Core;

var tests = new (string Name, Action Run)[]
{
    ("Scopes, counter declarations and primary include order", () =>
    {
        var file = Analyze("void F::Work() { Run(); }");
        Equal(1, file.AddedFunctions);
        Contains(file.ProposedText, "TRACE_CPUPROFILER_EVENT_SCOPE_STR");
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
    ("Existing macros in code are skipped, macros in comments are ignored", () =>
    {
        var file = Analyze("void F::A() { TRACE_CPUPROFILER_EVENT_SCOPE(A); Run(); }\nvoid F::B() { QUICK_SCOPE_CYCLE_COUNTER(B); Run(); }\nvoid F::C() { /* TRACE_COUNTER_INCREMENT(C); */ Run(); }");
        Equal(1, file.AddedFunctions);
        Equal("F::C", file.Functions.Single(f => f.Status == "Add").Name);
    }),
    ("Overloads and namespace names have different counter IDs", () =>
    {
        var file = Analyze("void Work(int a) { Run(); }\nvoid Work(float a) { Run(); }\nnamespace A { void Work() { Run(); } }\nnamespace B { void Work() { Run(); } }");
        Equal(4, file.AddedFunctions);
        Equal(4, file.Functions.Select(f => f.Counter).Distinct().Count());
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
    ("CPU only and counter only", () =>
    {
        var cpu = Analyze("void Work() { Run(); }", new(true, false));
        Contains(cpu.ProposedText, "TRACE_CPUPROFILER_EVENT_SCOPE_STR");
        Assert(!cpu.ProposedText.Contains("TRACE_COUNTER_INCREMENT"));
        var counter = Analyze("void Work() { Run(); }", new(false, true));
        Contains(counter.ProposedText, "TRACE_COUNTER_INCREMENT");
        Assert(!counter.ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR"));
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
        string backup = service.Apply(plan);
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
        var plan = new AnalysisPlan("unused", new(), [file], []);
        Contains(ReportExporter.CreatePatch(plan), "\\ No newline at end of file");
    })
};

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
    new CppInstrumenter().Analyze("Module/Fixture.cpp", Encoding.UTF8.GetBytes("#include \"Fixture.h\"\n" + body), options ?? new());
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
    string root = Path.Combine(Path.GetTempPath(), "UEEtc-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { action(root); }
    finally
    {
        // Only delete the exact temporary root created by this test.
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid temporary path");
        Directory.Delete(root, recursive: true);
    }
}
