using System.Text;
using System.Text.Json;
using System.Diagnostics;
using UEEtc.Blueprint;

if (args is ["--cancel-editor", var editor, var project])
{
    string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-blueprint-cancel-test"));
    string work = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
    var before = Process.GetProcessesByName("UnrealEditor-Cmd").Select(p => p.Id).ToHashSet();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try
    {
        try { await new UnrealEditorRunner().ExportAsync(new(editor, project, "/Game/BP_Probe", work), null, cts.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        if (Process.GetProcessesByName("UnrealEditor-Cmd").Any(p => !before.Contains(p.Id))) throw new Exception("Cancelled UE process remained running");
        Console.WriteLine("Real UE cancellation PASS: child process terminated and awaited"); return 0;
    }
    finally
    {
        if (!Path.GetFullPath(work).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary path");
        Directory.Delete(work, true);
    }
}

const string fixture = """
Begin Object Class=/Script/Engine.Blueprint Name="BP_Fixture"
   Begin Object Class=/Script/Engine.EdGraph Name="EventGraph"
      Begin Object Class=/Script/BlueprintGraph.K2Node_Event Name="Entry"
      End Object
      Begin Object Class=/Script/BlueprintGraph.K2Node_CallFunction Name="Call"
      End Object
   End Object
   Begin Object Name="EventGraph"
      Begin Object Name="Entry"
         NodePosX=-100
         NodePosY=200
         NodeWidth=300
         NodeHeight=50
         EventReference=(MemberName="ReceiveBeginPlay")
         NodeComment="C++移植のためのコメント"
         CustomProperties Pin (PinId=A,PinName="then",Direction="EGPD_Output",PinType.PinCategory="exec",LinkedTo=(Call B,),)
      End Object
      Begin Object Name="Call"
         FunctionReference=(MemberParent="/Script/Engine.KismetSystemLibrary",MemberName="PrintString")
         EnabledState=Disabled
         CustomProperties Pin (PinId=B,PinName="execute",PinType.PinCategory="exec",LinkedTo=(Entry A,),)
         CustomProperties Pin (PinId=C,PinName="Value",PinType.PinCategory="struct",PinType.PinSubCategoryObject="/Script/CoreUObject.Vector",DefaultValue="1,2,3",PinType.PinSubCategoryMemberReference=(MemberName="a,b"),)
         CustomProperties UserDefinedPin (PinName="Extra",PinType=(PinCategory="real"))
      End Object
      Schema=/Script/BlueprintGraph.EdGraphSchema_K2
      Nodes(0)="/Script/BlueprintGraph.K2Node_Event'Entry'"
      Nodes(1)="/Script/BlueprintGraph.K2Node_CallFunction'Call'"
   End Object
   Begin Object Class=/Script/Engine.SceneComponent Name="Scene"
      RelativeLocation=(X=100,Y=200,Z=300)
      NodePosX=777
   End Object
   ParentClass="/Script/Engine.Actor"
   UbergraphPages(0)="/Script/Engine.EdGraph'EventGraph'"
   NewVariables(0)=(VarName="Origin",VarType=(PinCategory="struct",PinSubCategoryObject="/Script/CoreUObject.Vector"),DefaultValue="1,2,3")
   LastEditedDocuments(0)=(SavedViewOffset=(X=10,Y=20),SavedZoomAmount=1.0)
End Object
""";

int passed = 0, failures = 0;
await Check("declarations and definitions merge into one graph with typed nodes", () =>
{
    var doc = UnrealTextParser.Parse("Blueprint", fixture);
    Assert(doc.Objects.Count == 5 && doc.Graphs.Single().Nodes.Count == 2);
    Assert(doc.Graphs.Single().Nodes[0].ClassPath.EndsWith("K2Node_Event"));
    Assert(doc.Graphs.Single().Role == "UbergraphPages");
    var auxiliary = UnrealTextParser.Parse("Blueprint", fixture.Replace("   ParentClass=", "   Begin Object Class=/Script/Engine.EdGraph Name=\"Auxiliary\"\n   End Object\n   ParentClass="));
    Assert(auxiliary.Graphs.Single(g => g.Name == "Auxiliary").Role.Contains("未分類"));
    return Task.CompletedTask;
});
await Check("layout removal retains runtime vectors and non-node properties", () =>
{
    var doc = UnrealTextParser.Parse("Blueprint", fixture);
    Assert(doc.RemovedLayoutProperties == 5 && !doc.Text.Contains("NodePosX=-100") && !doc.Text.Contains("LastEditedDocuments"));
    Assert(doc.Text.Contains("RelativeLocation=(X=100,Y=200,Z=300)") && doc.Text.Contains("NodePosX=777"));
    Assert(doc.Objects.Single(o => o.Name == "Entry").Properties.Keys.All(k => !k.StartsWith("NodePos")));
    Assert(doc.Objects.Single(o => o.Name == "Scene").Properties["NodePosX"] == "777");
    return Task.CompletedTask;
});
await Check("keep layout preserves placement and document viewport", () =>
{
    var doc = UnrealTextParser.Parse("Blueprint", fixture, false);
    Assert(doc.RemovedLayoutProperties == 0 && doc.Text.Contains("NodePosX=-100") && doc.Text.Contains("LastEditedDocuments"));
    return Task.CompletedTask;
});
await Check("pins keep exec links, input defaults, nested types and comma literals", () =>
{
    var doc = UnrealTextParser.Parse("Blueprint", fixture);
    var entry = doc.Objects.Single(o => o.Name == "Entry"); var call = doc.Objects.Single(o => o.Name == "Call");
    Assert(entry.Pins.Single().Links.Single() == new BlueprintLink("Call", "B"));
    Assert(call.Pins[0].Direction == "EGPD_Input" && doc.Warnings.Count == 0);
    Assert(UnrealTextParser.Value(call.Pins[1].Fields, "DefaultValue") == "1,2,3");
    Assert(call.Pins[1].Fields["PinType.PinSubCategoryMemberReference"] == "(MemberName=\"a,b\")");
    Assert(call.OtherEntries.Single().StartsWith("CustomProperties UserDefinedPin"));
    Assert(call.Properties["EnabledState"] == "Disabled");
    return Task.CompletedTask;
});
await Check("unresolved links produce a diagnostic without discarding connection", () =>
{
    var doc = UnrealTextParser.Parse("Blueprint", fixture.Replace("LinkedTo=(Call B,)", "LinkedTo=(Missing Z,)"));
    Assert(doc.Warnings.Count > 0 && doc.Objects.Single(o => o.Name == "Entry").Pins[0].Links[0].NodeName == "Missing");
    return Task.CompletedTask;
});
await Check("malformed object and pin text is rejected", async () =>
{
    foreach (string invalid in new[] { "", "End Object", "Begin Object Name=\"Bad\"", fixture.Replace("DefaultValue=\"1,2,3\"", "DefaultValue=\"unterminated") })
        await Reject<InvalidDataException>(() => Task.FromResult(UnrealTextParser.Parse("Blueprint", invalid)));
});
await Check("escaped quotes and nested tuples split without corrupting literals", () =>
{
    var fields = UnrealTextParser.Fields("(Name=\"a,\\\"b\\\"\",Value=(X=1,Y=(A=2,B=3)),Empty=(),)");
    Assert(fields.Count == 3 && fields["Value"] == "(X=1,Y=(A=2,B=3))");
    return Task.CompletedTask;
});
await WithRoot("asset mapping, project auto-detection and plugin mounts", async (root, options) =>
{
    Assert(BlueprintPaths.FindProject(options.AssetPath) == options.ProjectPath);
    Assert(BlueprintPaths.PackagePath(options.AssetPath, options.ProjectPath) == "/Game/Sub/BP_Fixture");
    Assert(BlueprintPaths.FindProject(Path.Combine(root, "does-not-exist", "BP.uasset")) is null);
    string plugin = Path.Combine(root, "Project", "Plugins", "Sample"); Directory.CreateDirectory(Path.Combine(plugin, "Content"));
    File.WriteAllText(Path.Combine(plugin, "Sample.uplugin"), "{}");
    string asset = Path.Combine(plugin, "Content", "BP.uasset"); File.WriteAllBytes(asset, [1]);
    Assert(BlueprintPaths.PackagePath(asset, options.ProjectPath) == "/Sample/BP");
    File.WriteAllText(Path.Combine(root, "Project", "Other.uproject"), "{}");
    Assert(BlueprintPaths.FindProject(options.AssetPath) is null);
    foreach (string malformed in new[] { "[]", "{\"EngineAssociation\":5.8}", "{broken" })
    {
        File.WriteAllText(options.ProjectPath, malformed);
        Assert(BlueprintPaths.FindEditor(options.ProjectPath).Contains("UE_5.8"));
    }
    await Task.CompletedTask;
});
await WithRoot("invalid paths and project-internal output fail before launching UE", async (root, options) =>
{
    var runner = new FakeRunner((_, _) => throw new Exception("Must not launch"));
    foreach (var invalid in new[] { options with { OutputRoot = Path.Combine(root, "Project", "Reports") },
        options with { AssetPath = "relative.uasset" }, options with { ProjectPath = "" }, options with { OutputRoot = "relative" } })
        await Reject<ArgumentException>(() => new BlueprintAnalysisService(runner).AnalyzeAsync(invalid));
    string content = Path.Combine(root, "ExternalPlugin", "Content"); Directory.CreateDirectory(content);
    File.WriteAllText(Path.Combine(root, "ExternalPlugin", "ExternalPlugin.uplugin"), "{}");
    string externalAsset = Path.Combine(content, "BP.uasset"); File.WriteAllBytes(externalAsset, [1]);
    await Reject<ArgumentException>(() => new BlueprintAnalysisService(runner).AnalyzeAsync(options with { AssetPath = externalAsset, OutputRoot = Path.Combine(content, "Reports") }));
});
await WithRoot("service generates facts and sanitized documents outside project without input writes", async (root, options) =>
{
    var runner = ExportFixture();
    var service = new BlueprintAnalysisService(runner);
    var first = await service.AnalyzeAsync(options); var second = await service.AnalyzeAsync(options);
    Assert(first.Directory != second.Directory && File.Exists(first.MarkdownPath) && File.Exists(first.JsonPath));
    Assert(File.ReadAllBytes(options.AssetPath).SequenceEqual(new byte[] { 1, 2, 3 }));
    Assert(!File.ReadAllText(Path.Combine(first.Directory, "Blueprint.txt")).Contains("NodePosX=-100"));
    Assert(File.ReadAllText(first.MarkdownPath).Contains("PrintString") && File.ReadAllText(first.MarkdownPath).Contains("RelativeLocation"));
    using var json = JsonDocument.Parse(File.ReadAllText(first.JsonPath));
    Assert(!json.RootElement.GetProperty("Documents")[0].TryGetProperty("Text", out _));
    Assert(!Directory.Exists(runner.Last!.WorkDirectory));
});
await WithRoot("keep layout flows through service to JSON and normalized text", async (_, options) =>
{
    var result = await new BlueprintAnalysisService(ExportFixture()).AnalyzeAsync(options with { RemoveLayout = false });
    Assert(!result.Report.LayoutRemoved && File.ReadAllText(Path.Combine(result.Directory, "Blueprint.txt")).Contains("NodePosX=-100"));
});
await WithRoot("runner failure preserves a diagnostic log and source bytes", async (_, options) =>
{
    var runner = new FakeRunner((request, _) => { File.WriteAllText(Path.Combine(request.WorkDirectory, "editor.log"), "Missing native module"); throw new IOException("Failed load"); });
    await Reject<IOException>(() => new BlueprintAnalysisService(runner).AnalyzeAsync(options));
    Assert(Directory.GetFiles(options.OutputRoot, "failed-*.log").Length == 1);
    Assert(File.ReadAllBytes(options.AssetPath).SequenceEqual(new byte[] { 1, 2, 3 }) && !Directory.Exists(runner.Last!.WorkDirectory));
});
await WithRoot("cancellation stops extraction and leaves no successful report", async (_, options) =>
{
    using var cts = new CancellationTokenSource();
    var runner = new FakeRunner(async (_, token) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, token); });
    await Reject<OperationCanceledException>(() => new BlueprintAnalysisService(runner).AnalyzeAsync(options, cancellationToken: cts.Token));
    Assert(!Directory.Exists(options.OutputRoot) && !Directory.Exists(runner.Last!.WorkDirectory));
});
await WithRoot("concurrent input changes are detected without restoring another edit", async (_, options) =>
{
    var runner = new FakeRunner((_, _) => { File.WriteAllBytes(options.AssetPath, [9]); throw new IOException("External edit"); });
    await Reject<IOException>(() => new BlueprintAnalysisService(runner).AnalyzeAsync(options));
    Assert(File.ReadAllBytes(options.AssetPath).SequenceEqual(new byte[] { 9 }) && !Directory.Exists(runner.Last!.WorkDirectory));
});
await Check("Markdown encodes asset-provided HTML and table delimiters", () =>
{
    var report = new BlueprintReport("<script>|.uasset", "/Game/Test", "Hash", "Fixture", true,
        [UnrealTextParser.Parse("Blueprint", fixture.Replace("PrintString", "<script>|"))], []);
    string md = BlueprintReportWriter.Markdown(report);
    Assert(!md.Contains("<script>") && md.Contains("&lt;script&gt;&#124;"));
    return Task.CompletedTask;
});

Console.WriteLine($"Blueprint tests: {passed} passed / {failures} failed");
return failures == 0 ? 0 : 1;

FakeRunner ExportFixture() => new((request, _) =>
{
    File.WriteAllText(Path.Combine(request.WorkDirectory, "Blueprint.copy"), fixture, new UnicodeEncoding(false, true));
    foreach (string name in new[] { "Class", "Defaults" }) File.WriteAllText(Path.Combine(request.WorkDirectory, name + ".copy"), "Begin Object Class=/Script/Engine.Actor Name=\"Root\"\nRelativeLocation=(X=1,Y=2,Z=3)\nEnd Object");
    File.WriteAllText(Path.Combine(request.WorkDirectory, "metadata.json"), "{\"EngineVersion\":\"Fixture-5.8\"}");
    Assert(request.PackagePath == "/Game/Sub/BP_Fixture");
    return Task.CompletedTask;
});
async Task Check(string name, Func<Task> run)
{
    try { await run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
async Task WithRoot(string name, Func<string, BlueprintAnalysisOptions, Task> run) => await Check(name, async () =>
{
    string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-blueprint-tests"));
    string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
    string project = Path.Combine(root, "Project"); Directory.CreateDirectory(Path.Combine(project, "Content", "Sub"));
    var options = new BlueprintAnalysisOptions(Path.Combine(project, "Content", "Sub", "BP_Fixture.uasset"),
        Path.Combine(project, "Project.uproject"), Path.Combine(root, "UnrealEditor-Cmd.exe"), Path.Combine(root, "Reports"));
    File.WriteAllBytes(options.AssetPath, [1, 2, 3]); File.WriteAllText(options.ProjectPath, "{}"); File.WriteAllBytes(options.EditorPath, []);
    try { await run(root, options); }
    finally
    {
        if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary path");
        Directory.Delete(root, true);
    }
});
static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static async Task Reject<T>(Func<Task> run) where T : Exception
{
    try { await run(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
sealed class FakeRunner(Func<EditorExportRequest, CancellationToken, Task> action) : IUnrealEditorRunner
{
    public EditorExportRequest? Last { get; private set; }
    public Task ExportAsync(EditorExportRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    { Last = request; return action(request, cancellationToken); }
}
