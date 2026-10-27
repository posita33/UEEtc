using System.Text.Json.Serialization;

namespace UEEtc.Blueprint;

public sealed record BlueprintAnalysisOptions(string AssetPath, string ProjectPath, string EditorPath,
    string OutputRoot, bool RemoveLayout = true);

public sealed record BlueprintPin(string Id, string Name, string Direction, string Category,
    IReadOnlyDictionary<string, string> Fields, IReadOnlyList<BlueprintLink> Links);
public sealed record BlueprintLink(string NodeName, string PinId);

public sealed class BlueprintObject(string id, string name, string? parentId)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string? ParentId { get; } = parentId;
    public string ClassPath { get; internal set; } = "";
    public string ExportPath { get; internal set; } = "";
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
    public List<BlueprintPin> Pins { get; } = [];
    public List<string> OtherEntries { get; } = [];
}

public sealed record BlueprintGraph(string Id, string Name, IReadOnlyList<BlueprintObject> Nodes,
    string Role = "UE出力・参照元未分類（中間/補助グラフの可能性あり）");
public sealed record BlueprintDocument(string Name, IReadOnlyList<BlueprintObject> Objects,
    IReadOnlyList<BlueprintGraph> Graphs, [property: JsonIgnore] string Text, int RemovedLayoutProperties, IReadOnlyList<string> Warnings);
public sealed record BlueprintReport(string AssetPath, string PackagePath, string AssetSha256,
    string EngineVersion, bool LayoutRemoved, IReadOnlyList<BlueprintDocument> Documents,
    IReadOnlyList<string> Warnings);
public sealed record BlueprintAnalysisResult(string Directory, string MarkdownPath, string JsonPath, BlueprintReport Report);

public interface IBlueprintAnalysisService
{
    Task<BlueprintAnalysisResult> AnalyzeAsync(BlueprintAnalysisOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
