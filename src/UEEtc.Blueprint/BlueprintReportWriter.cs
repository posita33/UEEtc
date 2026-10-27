using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace UEEtc.Blueprint;

public static class BlueprintReportWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Markdown(BlueprintReport report)
    {
        var text = new StringBuilder("# Blueprint C++移植用の構造資料\n\n");
        text.AppendLine($"- アセット: {Code(report.AssetPath)}\n- パッケージ: {Code(report.PackagePath)}\n- Engine: {Code(report.EngineVersion)}\n- SHA256: {Code(report.AssetSha256)}");
        text.AppendLine($"- ノード配置情報: {(report.LayoutRemoved ? "除外" : "保持")}（{report.Documents.Sum(d => d.RemovedLayoutProperties)}プロパティ除外）\n");
        text.AppendLine("これはEngineが読み込んだ構造・接続・既定値の資料です。コンパイル可能なC++への自動変換や動作等価性の証明ではありません。\n");
        text.AppendLine("## 移植時に確認すること\n\n- 親クラス・インターフェースと呼び出し先のC++ API。\n- execピンの分岐・合流・ループと、データピンの評価順序。\n- Delay・Timeline等の潜在処理、delegate、ネットワーク複製、マクロ・関数・カスタムノードの意味。\n- コンポーネントと既定値、ワールド座標・Transform。表示用ノード配置座標とは区別する。\n- 無効ノード・未接続ピン・失われた依存先。未知ノードを省略して移植完了と判断しない。\n");
        foreach (string warning in report.Warnings.Concat(report.Documents.SelectMany(d => d.Warnings))) text.AppendLine("- 診断: " + Cell(warning));
        foreach (var document in report.Documents)
        {
            text.AppendLine($"\n## {Cell(document.Name)}\n\nObject {document.Objects.Count} / グラフ {document.Graphs.Count} / ノード {document.Graphs.Sum(g => g.Nodes.Count)}\n");
            foreach (var root in document.Objects.Where(o => o.ParentId is null))
            {
                text.AppendLine($"ルート: {Code(root.Name)} ({Code(root.ClassPath)})\n");
                foreach (var property in root.Properties)
                    text.AppendLine($"- {Code(property.Key)}: {Code(property.Value)}");
            }
            foreach (var graph in document.Graphs)
            {
                text.AppendLine($"\n### グラフ: {Cell(graph.Name)}\n\n参照元の分類: {Code(graph.Role)}\n");
                foreach (var node in graph.Nodes)
                {
                    text.AppendLine($"\n#### {Cell(node.Name)}\n\n- 型: {Code(node.ClassPath)}\n- ID: {Code(node.Id)}");
                    foreach (var property in node.Properties) text.AppendLine($"- {Code(property.Key)}: {Code(property.Value)}");
                    if (node.Pins.Count > 0)
                    {
                        text.AppendLine("\n| PinId | ピン | 方向 | 型 | 既定値 | 接続先（ノード / PinId） |\n| --- | --- | --- | --- | --- | --- |");
                        foreach (var pin in node.Pins)
                            text.AppendLine($"| {Cell(pin.Id)} | {Cell(pin.Name)} | {Cell(pin.Direction)} | {Cell(pin.Category + " / " + UnrealTextParser.Value(pin.Fields, "PinType.PinSubCategoryObject"))} | {Cell(UnrealTextParser.Value(pin.Fields, "DefaultValue", UnrealTextParser.Value(pin.Fields, "DefaultObject")))} | {Cell(string.Join("; ", pin.Links.Select(l => l.NodeName + " / " + l.PinId)))} |");
                    }
                    foreach (string entry in node.OtherEntries) text.AppendLine("\n追加情報: " + Code(entry));
                }
            }
            var graphObjects = document.Graphs.Select(g => g.Id).Concat(document.Graphs.SelectMany(g => g.Nodes).Select(n => n.Id)).ToHashSet(StringComparer.Ordinal);
            foreach (var item in document.Objects.Where(o => o.ParentId is not null && !graphObjects.Contains(o.Id)))
            {
                text.AppendLine($"\n### Object: {Cell(item.Name)}\n\n- 型: {Code(item.ClassPath)}\n- 親: {Code(item.ParentId!)}");
                foreach (var property in item.Properties) text.AppendLine($"- {Code(property.Key)}: {Code(property.Value)}");
                foreach (string entry in item.OtherEntries) text.AppendLine("- 追加情報: " + Code(entry));
            }
            text.AppendLine("\n全Objectのプロパティ、ピンの完全な型情報・既定値・コメント・参照はJSONと同名の.txtに保持しています。\n");
        }
        text.AppendLine("\n## C++移植のための情報の読み方\n\nexec出力ピンのLinkedToは次に実行されるノードの入力ピンです。データ接続は型・ContainerType・参照/const情報とともに確認してください。JSONのPropertiesはUEのシリアライズ表現を保持し、未解釈の値をC++コードへ置き換えていません。");
        return text.ToString();
    }

    private static string Cell(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "&#124;").Replace("\r", "").Replace("\n", "<br>");
    private static string Code(string value) => "<code>" + Cell(value).Replace("`", "&#96;") + "</code>";
}
