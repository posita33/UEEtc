using System.Text;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Plans;

internal static class CpuPolicyTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("CPU label contains only class and function, independent of path", () =>
        {
            var file = Analyze("void UMyComponent::BeginPlay() { Work(); }");
            Check(file.ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE(UMyComponent BeginPlay)"));
            var other = Analyze("void UMyComponent::BeginPlay() { Work(); }", path: "Other/Deep/File.cpp");
            Check(CpuLine(file) == CpuLine(other));
            Check(!CpuLine(file).Contains("UEEtc/") && !CpuLine(file).Contains('['));
        }),
        ("CPU labels use the nearest class and unqualified free function name", () =>
        {
            var file = Analyze("namespace UE::Game { void AThing::Run() { Work(); } void FreeWork() { Work(); } }\nvoid UE::Game::Other::Run() { Work(); }");
            Check(file.ProposedText.Contains("SCOPE(AThing Run)"));
            Check(file.ProposedText.Contains("SCOPE(Other Run)"));
            Check(file.ProposedText.Contains("SCOPE(FreeWork)"));
        }),
        ("CPU overload labels intentionally share the requested short name", () =>
        {
            var file = Analyze("void AThing::Run(int n) { Work(); } void AThing::Run(float n) { Work(); }");
            Check(file.AddedCpuScopes == 2 && file.ProposedText.Split("SCOPE(AThing Run)").Length == 3);
        }),
        ("Existing CPU labels remain unchanged", () =>
        {
            const string body = "void AThing::Run() { TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"Original/path [hash]\"); Work(); }";
            var file = Analyze(body);
            Check(!file.HasChanges && file.ProposedText.EndsWith(body, StringComparison.Ordinal));
        }),
        ("CPU scope follows a matching standalone Super call", () =>
        {
            var file = Analyze("void UMyComponent::BeginPlay() { Super::BeginPlay(); Work(); }");
            Check(file.AddedCpuScopes == 1);
            Check(file.ProposedText.IndexOf("Super::BeginPlay();", StringComparison.Ordinal) < file.ProposedText.IndexOf("TRACE_CPUPROFILER", StringComparison.Ordinal));
            Check(file.Functions.Single().CpuDecision.Contains("Super"));
        }),
        ("Super arguments with nested calls, literals and lambdas are parsed", () =>
        {
            var file = Analyze("void AThing::Work(int n) { Prepare(); Super::Work(Call(n, \";)\"), [] { return 1; }()); DerivedWork(); }");
            Check(file.AddedCpuScopes == 1);
            Check(file.ProposedText.IndexOf("DerivedWork();", StringComparison.Ordinal) > file.ProposedText.IndexOf("TRACE_CPUPROFILER", StringComparison.Ordinal));
            Check(file.ProposedText.IndexOf("return 1;", StringComparison.Ordinal) < file.ProposedText.IndexOf("TRACE_CPUPROFILER", StringComparison.Ordinal));
        }),
        ("Comment and string Super spellings do not move the CPU scope", () =>
        {
            var file = Analyze("void AThing::Work() { /* Super::Work(); */ auto text = R\"x(Super::Work();)x\"; DerivedWork(); }");
            Check(file.AddedCpuScopes == 1 && file.ProposedText.IndexOf("TRACE_CPUPROFILER", StringComparison.Ordinal) < file.ProposedText.IndexOf("auto text", StringComparison.Ordinal));
        }),
        ("Other parent helpers do not exclude the start of this function", () =>
        {
            var file = Analyze("void AThing::Work() { Super::Helper(); DerivedWork(); }");
            Check(file.AddedCpuScopes == 1 && file.ProposedText.IndexOf("TRACE_CPUPROFILER", StringComparison.Ordinal) < file.ProposedText.IndexOf("Super::Helper", StringComparison.Ordinal));
        }),
        ("Conditional, returned, assigned and lambda Super calls require manual review", () =>
        {
            foreach (string body in new[] {
                "void AThing::Work() { if (bOk) { Super::Work(); } DerivedWork(); }",
                "void AThing::Work() { if (bOk) Super::Work(); DerivedWork(); }",
                "int AThing::Work() { return Super::Work(); }",
                "void AThing::Work() { auto result = Super::Work(); DerivedWork(); }",
                "void AThing::Work() { auto callback = [] { Super::Work(); }; DerivedWork(); }" })
            {
                var file = Analyze(body);
                Check(file.AddedCpuScopes == 0 && !file.HasChanges && file.Functions.Single().CpuDecision.Contains("手動確認"));
            }
        }),
        ("Multiple or conditionally compiled Super calls require manual review", () =>
        {
            foreach (string body in new[] {
                "void AThing::Work() { Super::Work(); Super::Work(); DerivedWork(); }",
                "void AThing::Work() {\n#if ENABLED\nSuper::Work();\n#endif\nDerivedWork(); }" })
                Check(!Analyze(body).HasChanges);
        }),
        ("A function containing only Super has no remaining CPU region", () =>
        {
            var file = Analyze("void AThing::Work() { Super::Work(); /* no derived work */ }");
            Check(!file.HasChanges && file.Functions.Single().CpuDecision.Contains("処理がない"));
        }),
        ("Legacy entry counters stay before Super while CPU starts after it", () =>
        {
            var file = Analyze("void AThing::Work() { Super::Work(); DerivedWork(); }", new(true, true));
            int counter = file.ProposedText.IndexOf("TRACE_COUNTER_INCREMENT(", StringComparison.Ordinal);
            int parent = file.ProposedText.IndexOf("Super::Work();", StringComparison.Ordinal);
            int cpu = file.ProposedText.IndexOf("TRACE_CPUPROFILER_EVENT_SCOPE(", StringComparison.Ordinal);
            Check(counter < parent && parent < cpu && file.AddedCallCounters == 1 && file.AddedCpuScopes == 1);
            var second = new CppInstrumenter().Analyze(file.RelativePath, file.ProposedBytes, new(true, true));
            Check(!second.HasChanges);
        }),
        ("Unsafe Super CPU placement does not suppress an opted-in entry counter", () =>
        {
            var file = Analyze("int AThing::Work() { return Super::Work(); }", new(true, true));
            Check(file.AddedCpuScopes == 0 && file.AddedCallCounters == 1);
            Check(!file.ProposedText.Contains("CpuProfilerTrace.h"));
        }),
        ("Super placement retains BOM, CRLF and existing comments", () =>
        {
            const string text = "#include \"Fixture.h\"\r\nvoid AThing::Work()\r\n{\r\n\tSuper::Work(); // base\r\n\tDerivedWork();\r\n}\r\n";
            var file = new CppInstrumenter().Analyze("Fixture.cpp", [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(text)], new());
            Check(file.ProposedBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            Check(!file.ProposedText.Replace("\r\n", "").Contains('\n'));
            Check(file.ProposedText.Contains("// base") && file.ProposedText.Contains("\tDerivedWork();"));
        }),
        ("QUICK_SCOPE_CYCLE_COUNTER remains an existing CPU measurement", () =>
        {
            var file = Analyze("void AThing::Work() { QUICK_SCOPE_CYCLE_COUNTER(IA_Existing); DerivedWork(); }");
            Check(!file.HasChanges && file.Functions.Single().CpuDecision.Contains("既存"));
        })
    ];

    private static FileAnalysis Analyze(string body, InstrumentationOptions? options = null, string path = "Module/Fixture.cpp") =>
        new CppInstrumenter().Analyze(path, Encoding.UTF8.GetBytes("#include \"Fixture.h\"\n" + body), options ?? new());
    private static string CpuLine(FileAnalysis file) => file.ProposedText.Split('\n').Single(line => line.Contains("TRACE_CPUPROFILER_EVENT_SCOPE"));
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
