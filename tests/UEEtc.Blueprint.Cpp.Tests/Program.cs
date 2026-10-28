using UEEtc.Blueprint;
using UEEtc.Blueprint.Cpp;

string Pin(string id, string name, string direction, string category, string links = "", string value = "") =>
    $"CustomProperties Pin (PinId={id},PinName=\"{name}\",Direction=\"EGPD_{direction}\",PinType.PinCategory=\"{category}\",DefaultValue=\"{value}\",LinkedTo=({links}),)";
string Node(string name, string type, params string[] body) => $"Begin Object Class=/Script/BlueprintGraph.{type} Name=\"{name}\"\n" + string.Join('\n', body) + "\nEnd Object";
string Var(string name, string type, int index) => $"NewVariables({index})=(VarName=\"{name}\",VarType=(PinCategory=\"{type}\"))";
string Property(string key, string name) => $"{key}=(MemberName=\"{name}\",bSelfContext=True)";
string Math(string name) => $"FunctionReference=(MemberParent=\"/Script/CoreUObject.Class'/Script/Engine.KismetMathLibrary'\",MemberName=\"{name}\")";
string fixture = "Begin Object Class=/Script/Engine.Blueprint Name=\"BP_Counter\"\nParentClass=\"/Script/CoreUObject.Class'/Script/Engine.Actor'\"\n" +
    Var("Count", "int", 0) + "\n" + Var("Accepted", "bool", 1) + "\nFunctionGraphs(0)=\"/Script/Engine.EdGraph'Increment'\"\n" +
    "Begin Object Class=/Script/Engine.EdGraph Name=\"Increment\"\n" +
    Node("Entry", "K2Node_FunctionEntry", Pin("E", "then", "Output", "exec", "Set S,")) + "\n" +
    Node("Get", "K2Node_VariableGet", Property("VariableReference", "Count"), Pin("G", "Count", "Output", "int")) + "\n" +
    Node("Add", "K2Node_PromotableOperator", Math("Add_IntInt"), Pin("A", "A", "Input", "int", "Get G,"), Pin("B", "B", "Input", "int", value: "1"), Pin("R", "ReturnValue", "Output", "int")) + "\n" +
    Node("Set", "K2Node_VariableSet", Property("VariableReference", "Count"), Pin("S", "execute", "Input", "exec", "Entry E,"), Pin("V", "Count", "Input", "int", "Add R,"), Pin("T", "then", "Output", "exec", "Branch X,")) + "\n" +
    Node("Less", "K2Node_PromotableOperator", Math("Less_IntInt"), Pin("L", "A", "Input", "int", "Get G,"), Pin("M", "B", "Input", "int", value: "3"), Pin("N", "ReturnValue", "Output", "bool")) + "\n" +
    Node("Branch", "K2Node_IfThenElse", Pin("X", "execute", "Input", "exec"), Pin("C", "Condition", "Input", "bool", "Less N,"), Pin("Y", "then", "Output", "exec", "Yes I,"), Pin("Z", "else", "Output", "exec", "No I,")) + "\n" +
    Node("Yes", "K2Node_VariableSet", Property("VariableReference", "Accepted"), Pin("I", "execute", "Input", "exec"), Pin("V", "Accepted", "Input", "bool", value: "true")) + "\n" +
    Node("No", "K2Node_VariableSet", Property("VariableReference", "Accepted"), Pin("I", "execute", "Input", "exec"), Pin("V", "Accepted", "Input", "bool", value: "false")) + "\n" +
    "End Object\nEnd Object\n";
BlueprintReport Report(string source) => new("BP_Counter.uasset", "/Game/BP_Counter", new string('A', 64), "5.8", true,
    [UnrealTextParser.Parse("Blueprint", source), UnrealTextParser.Parse("Defaults", "Begin Object Name=\"Defaults\"\nCount=0\nAccepted=False\nEnd Object")], []);
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
var code = BlueprintCppEmitter.Generate(Report(fixture));
Check(code.IsComplete && code.FunctionCount == 1, "basic data flow and branch supported: " + string.Join(";", code.Diagnostics));
Check(code.Implementation.Contains("static_cast<uint32>") && code.Implementation.Contains(" ? "), "arithmetic and branch are emitted");
Check(!code.Header.Contains("#error") && code.Header.Contains("Var_Count = 0"), "complete subset has typed defaults and no incomplete guard");
Check(BlueprintCppEmitter.Generate(Report(fixture)).Header == code.Header && BlueprintCppEmitter.Generate(Report(fixture)).Implementation == code.Implementation, "deterministic generation");
var unsupported = BlueprintCppEmitter.Generate(Report(fixture.Replace("K2Node_VariableSet Name=\"Set\"", "K2Node_CallFunction Name=\"Set\"")));
Check(!unsupported.IsComplete && unsupported.Header.Contains("#error") && unsupported.Diagnostics.Any(d => d.Node == "Set"), "unsupported exec node is guarded and located");
Check(unsupported.Implementation.Contains("Unconverted Blueprint node"), "incomplete state fails instead of silently continuing");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("PinCategory=\"int\"", "PinCategory=\"struct\""))).IsComplete, "unsupported variable and pin types rejected");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("Get G,", "Missing G,"))).IsComplete, "dangling data link diagnosed");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("Set S,", "Missing S,"))).IsComplete, "dangling exec link diagnosed");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("PinName=\"B\",Direction=\"EGPD_Input\",PinType.PinCategory=\"int\",DefaultValue=\"1\"", "PinName=\"B\",Direction=\"EGPD_Input\",PinType.PinCategory=\"int\",DefaultValue=\"1;evil()\""))).IsComplete, "literal injection rejected");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("Add_IntInt", "Divide_IntInt"))).IsComplete, "unsafe unsupported math cannot compile silently");
Check(!BlueprintCppEmitter.Generate(Report(fixture) with { Warnings = ["UE load error"] }).IsComplete, "extraction warnings require review");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("bSelfContext=True", "bSelfContext=False"))).IsComplete, "external variables cannot be confused with this actor");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("/Script/Engine.KismetMathLibrary'", "/Script/Other.KismetMathLibrary'"))).IsComplete, "math allowlist checks declaring class");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("Get G,", "Add R,"))).IsComplete, "cyclic data expression is rejected");
string returned = "Begin Object Class=/Script/Engine.Blueprint Name=\"BP_Return\"\nParentClass=\"/Script/CoreUObject.Class'/Script/Engine.Actor'\"\nFunctionGraphs(0)=\"/Script/Engine.EdGraph'Double'\"\nBegin Object Class=/Script/Engine.EdGraph Name=\"Double\"\n" +
    Node("Entry", "K2Node_FunctionEntry", Pin("E", "then", "Output", "exec", "Result I,"), Pin("V", "Value", "Output", "int")) + "\n" +
    Node("Multiply", "K2Node_PromotableOperator", Math("Multiply_IntInt"), Pin("A", "A", "Input", "int", "Entry V,"), Pin("B", "B", "Input", "int", value: "2"), Pin("O", "ReturnValue", "Output", "int")) + "\n" +
    Node("Result", "K2Node_FunctionResult", Pin("I", "execute", "Input", "exec"), Pin("R", "ReturnValue", "Input", "int", "Multiply O,")) + "\nEnd Object\nEnd Object";
var returns = BlueprintCppEmitter.Generate(Report(returned));
Check(returns.IsComplete && returns.Header.Contains("int32 In_Value_0, int32& Out_ReturnValue_1"), "input and output arguments preserve types");
Check(returns.Implementation.Contains("Out_ReturnValue_1 = static_cast<int32>"), "return node writes the calculated output");
var sequence = BlueprintCppEmitter.Generate(Report(fixture.Replace("K2Node_IfThenElse Name=\"Branch\"", "K2Node_ExecutionSequence Name=\"Branch\"")));
Check(sequence.IsComplete && sequence.Implementation.Contains("Pending.Add("), "sequence uses continuations rather than treating outputs as a branch");
Check(!BlueprintCppEmitter.Generate(Report(fixture.Replace("EdGraph'Increment'", "EdGraph'Lost'"))).IsComplete, "missing user graph is diagnosed");
string literalRoot = "Begin Object Class=/Script/Engine.Blueprint Name=\"BP_Literal\"\nParentClass=\"/Script/CoreUObject.Class'/Script/Engine.Actor'\"\n" + Var("Text", "string", 0) + "\nEnd Object";
BlueprintReport StringReport(string value) => Report(literalRoot) with { Documents = [UnrealTextParser.Parse("Blueprint", literalRoot), UnrealTextParser.Parse("Defaults", "Begin Object Name=\"Defaults\"\nText=\"" + value + "\"\nEnd Object")] };
var escaped = BlueprintCppEmitter.Generate(StringReport(@"line\nquoted\""text\"""));
Check(escaped.IsComplete && escaped.Header.Contains(@"line\nquoted\""text\"""), "string escapes are decoded once and safely quoted for C++");
Check(!BlueprintCppEmitter.Generate(StringReport(@"unknown\q")).IsComplete, "unknown string escapes cannot silently change values");
Check(BlueprintCppEmitter.Generate(StringReport(@"path\\query")).IsComplete, "escaped backslash is not decoded twice");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel(); bool cancelled = false;
    try { BlueprintCppEmitter.Generate(Report(fixture), cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "cancellation reaches graph compiler");
}
string temp = Path.Combine(Path.GetTempPath(), "UEEtc-Cpp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
try
{
    var fake = new FakeAnalysis(Report(fixture), temp);
    var converted = await new BlueprintCppConversionService(fake).ConvertAsync(new("a", "b", "c", "d"));
    Check(fake.Calls == 1 && converted.Code.IsComplete, "conversion extracts exactly once");
    Check(File.Exists(Path.Combine(converted.Directory, "BlueprintConversion/Source/BlueprintConversion/Public/ConvertedBlueprint.h")), "native plugin sources are written");
    Check(File.Exists(Path.Combine(converted.Directory, "conversion.json")) && File.Exists(Path.Combine(converted.Directory, "conversion.md")), "machine and human diagnostics are written");
    Check(!Directory.GetDirectories(temp, ".cpp-*").Any(), "staging is committed atomically");
    bool duplicateRejected = false;
    try { await new BlueprintCppConversionService(fake).ConvertAsync(new("a", "b", "c", "d")); }
    catch (IOException) { duplicateRejected = true; }
    Check(duplicateRejected && File.Exists(Path.Combine(converted.Directory, "conversion.json")) && !Directory.GetDirectories(temp, ".cpp-*").Any(), "existing C++ output is preserved and failed staging is cleaned");
    using var cancel = new CancellationTokenSource();
    string cancelledDirectory = Path.Combine(temp, "Cancelled");
    var progress = new ImmediateProgress(_ => cancel.Cancel());
    bool stopped = false;
    try { await new BlueprintCppConversionService(new FakeAnalysis(Report(fixture), cancelledDirectory)).ConvertAsync(new("a", "b", "c", "d"), progress, cancel.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Check(stopped && !Directory.Exists(cancelledDirectory), "cancellation after extraction publishes no C++ output");
}
finally { if (Path.GetFullPath(temp).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) Directory.Delete(temp, true); }

if (args is ["--emit-plugin", var output])
{
    foreach (var file in BlueprintCppConversionService.PluginFiles(code))
    {
        string path = Path.Combine(Path.GetFullPath(output), file.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, file.Value);
    }
}
Console.WriteLine($"Blueprint C++ tests: {passed} passed");

sealed class FakeAnalysis(BlueprintReport report, string directory) : IBlueprintAnalysisService
{
    public int Calls { get; private set; }
    public Task<BlueprintAnalysisResult> AnalyzeAsync(BlueprintAnalysisOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    { Calls++; return Task.FromResult(new BlueprintAnalysisResult(directory, "", "", report)); }
}
sealed class ImmediateProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
