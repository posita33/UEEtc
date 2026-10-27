using System.Text;
using System.Text.RegularExpressions;

namespace UEEtc.Blueprint;

// Parses the Engine's object-text export; it does not guess meaning from binary strings.
public static class UnrealTextParser
{
    private static readonly Regex HeaderField = new("(?<key>\\w+)=(?:\"(?<quoted>(?:\\\\.|[^\"\\\\])*)\"|(?<raw>\\S+))", RegexOptions.Compiled);
    private static readonly HashSet<string> NodeLayout = new(StringComparer.Ordinal) { "NodePosX", "NodePosY", "NodeWidth", "NodeHeight" };

    public static BlueprintDocument Parse(string name, string text, bool removeLayout = true, CancellationToken cancellationToken = default)
    {
        if (text.Length > 128 * 1024 * 1024) throw new InvalidDataException("Blueprintテキストが128Mi文字を超えています。");
        var objects = new Dictionary<string, BlueprintObject>(StringComparer.Ordinal);
        var stack = new Stack<BlueprintObject>();
        var lines = new List<(string Text, BlueprintObject? Owner, string? Key)>();
        var warnings = new List<string>();
        int lineNumber = 0;
        foreach (string sourceLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            string line = sourceLine.Trim();
            BlueprintObject? owner = stack.TryPeek(out var top) ? top : null;
            string? key = null;
            if (line.StartsWith("Begin Object", StringComparison.Ordinal))
            {
                var fields = HeaderField.Matches(line).ToDictionary(m => m.Groups["key"].Value,
                    m => m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["raw"].Value, StringComparer.Ordinal);
                if (!fields.TryGetValue("Name", out string? objectName)) throw new InvalidDataException($"L{lineNumber}: Object名がありません。");
                string id = owner is null ? objectName : owner.Id + "/" + objectName;
                if (!objects.TryGetValue(id, out var item)) objects[id] = item = new(id, objectName, owner?.Id);
                if (fields.TryGetValue("Class", out string? type)) item.ClassPath = type;
                if (fields.TryGetValue("ExportPath", out string? exportPath)) item.ExportPath = exportPath;
                stack.Push(item); owner = item;
            }
            else if (line == "End Object")
            {
                if (stack.Count == 0) throw new InvalidDataException($"L{lineNumber}: 対応しないEnd Objectです。");
                owner = stack.Pop();
            }
            else if (line.StartsWith("CustomProperties Pin ", StringComparison.Ordinal) && owner is not null)
            {
                var fields = Fields(line["CustomProperties Pin ".Length..]);
                if (!fields.TryGetValue("PinId", out string? id)) throw new InvalidDataException($"L{lineNumber}: PinIdがありません。");
                var links = new List<BlueprintLink>();
                if (fields.TryGetValue("LinkedTo", out string? linked))
                    foreach (string entry in Split(Unwrap(linked)))
                    {
                        var tokens = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length == 2) links.Add(new(tokens[0], tokens[1]));
                        else if (tokens.Length != 0) warnings.Add($"L{lineNumber}: 接続を解釈できません: {entry}");
                    }
                var pin = new BlueprintPin(id, Value(fields, "PinName"), Value(fields, "Direction", "EGPD_Input"),
                    Value(fields, "PinType.PinCategory"), fields, links);
                int existing = owner.Pins.FindIndex(p => p.Id == id);
                if (existing >= 0) owner.Pins[existing] = pin; else owner.Pins.Add(pin);
            }
            else if (owner is not null && line.Length > 0)
            {
                int equals = line.IndexOf('=');
                if (!line.StartsWith("CustomProperties ", StringComparison.Ordinal) && equals > 0)
                {
                    key = line[..equals];
                    owner.Properties[key] = line[(equals + 1)..];
                }
                else owner.OtherEntries.Add(line);
            }
            else if (line.Length > 0) warnings.Add($"L{lineNumber}: Object外の行をテキストに保持しました。");
            lines.Add((sourceLine, owner, key));
        }
        if (stack.Count != 0 || objects.Count == 0) throw new InvalidDataException("不完全なUE Objectテキストです。");
        var graphs = objects.Values.Where(o => o.ClassPath.EndsWith(".EdGraph", StringComparison.Ordinal) ||
            o.Properties.ContainsKey("Schema")).Select(graph =>
        {
            var members = graph.Properties.Where(p => p.Key.StartsWith("Nodes(", StringComparison.Ordinal))
                .Select(p => ReferenceName(p.Value)).ToHashSet(StringComparer.Ordinal);
            string? role = graph.ParentId is { } parent && objects.TryGetValue(parent, out var container)
                ? container.Properties.Where(p => new[] { "UbergraphPages(", "FunctionGraphs(", "MacroGraphs(", "DelegateSignatureGraphs(" }
                    .Any(prefix => p.Key.StartsWith(prefix, StringComparison.Ordinal)) && ReferenceName(p.Value) == graph.Name)
                    .Select(p => p.Key[..p.Key.IndexOf('(')]).FirstOrDefault() : null;
            return new BlueprintGraph(graph.Id, graph.Name, objects.Values.Where(child => child.ParentId == graph.Id &&
                (members.Contains(child.Name) || child.Pins.Count > 0 || child.ClassPath.Contains("K2Node", StringComparison.Ordinal) ||
                 child.ClassPath.Contains("EdGraphNode", StringComparison.Ordinal))).ToArray(), role ?? "UE出力・参照元未分類（中間/補助グラフの可能性あり）");
        }).ToArray();
        var nodes = graphs.SelectMany(g => g.Nodes).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var graph in graphs)
        {
            var endpoints = graph.Nodes.SelectMany(n => n.Pins.Select(p => (n.Name, p.Id))).ToHashSet();
            foreach (var node in graph.Nodes)
                foreach (var link in node.Pins.SelectMany(p => p.Links))
                    if (!endpoints.Contains((link.NodeName, link.PinId)))
                        warnings.Add($"{graph.Name}/{node.Name}: 接続先を同じグラフで解決できません: {link.NodeName} / {link.PinId}。元の接続情報は保持しています。");
        }
        int removed = 0;
        var normalized = new StringBuilder();
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool layout = line.Owner is { } item && line.Key is { } property &&
                ((nodes.Contains(item.Id) && NodeLayout.Contains(property)) ||
                 (item.ParentId is null && item.ClassPath.EndsWith("Blueprint", StringComparison.Ordinal) && property.StartsWith("LastEditedDocuments(", StringComparison.Ordinal)));
            if (removeLayout && layout)
            {
                line.Owner!.Properties.Remove(line.Key!); removed++; continue;
            }
            normalized.AppendLine(line.Text);
        }
        return new(name, objects.Values.ToArray(), graphs, normalized.ToString(), removed, warnings);
    }

    public static IReadOnlyDictionary<string, string> Fields(string tuple)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in Split(Unwrap(tuple)))
        {
            int equals = entry.IndexOf('=');
            if (equals > 0) result[entry[..equals].Trim()] = entry[(equals + 1)..].Trim();
        }
        return result;
    }
    public static string Value(IReadOnlyDictionary<string, string> fields, string key, string fallback = "") =>
        fields.TryGetValue(key, out var value) ? Unquote(value) : fallback;

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
        ? value[1..^1] : value;
    private static string Unwrap(string value) => value.StartsWith('(') && value.EndsWith(')') ? value[1..^1] : value;
    private static string ReferenceName(string value)
    {
        value = Unquote(value).TrimEnd('\'');
        int quote = value.LastIndexOf('\'');
        return quote >= 0 ? value[(quote + 1)..] : value;
    }
    private static IEnumerable<string> Split(string value)
    {
        int start = 0, depth = 0; bool quoted = false, escaped = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') quoted = true;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { yield return value[start..i].Trim(); start = i + 1; }
            if (depth < 0) throw new InvalidDataException("ピン・構造体の括弧が不正です。");
        }
        if (quoted || depth != 0) throw new InvalidDataException("ピン・構造体の文字列または括弧が閉じていません。");
        if (start < value.Length) yield return value[start..].Trim();
    }
}
