using System.Text;
using System.Text.RegularExpressions;
using UEEtc.Blueprint;

namespace UEEtc.Blueprint.Cpp;

/// <summary>A deliberately limited compiler over Engine object-text graphs. No asset-specific templates.</summary>
public static class BlueprintCppEmitter
{
    public static CppConversionOutput Generate(BlueprintReport report, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<ConversionDiagnostic>();
        void Error(string graph, string node, string message) => diagnostics.Add(new("Error", graph, node, message));
        var blueprint = report.Documents.FirstOrDefault(d => d.Name == "Blueprint");
        var root = blueprint?.Objects.FirstOrDefault(o => o.ParentId is null);
        string parent = root?.Properties.GetValueOrDefault("ParentClass") ?? "";
        var baseClass = parent.Contains("'/Script/Engine.Pawn'", StringComparison.Ordinal) ? ("APawn", "GameFramework/Pawn.h") : ("AActor", "GameFramework/Actor.h");
        if (!parent.Contains("'/Script/Engine.Actor'", StringComparison.Ordinal) && baseClass.Item1 != "APawn") Error("", "", "親クラスに対応していません。仮のAActorを使用します。");
        if (root is null) Error("", "", "Blueprintのルート情報がありません。");
        if (report.Warnings.Count > 0) Error("", "", "抽出時の診断があります。analysis.md/editor.logの確認が必要です。");
        foreach (var document in report.Documents.Where(d => d.Warnings.Count > 0)) Error(document.Name, "", "構造解析の診断があるため完全変換と判定しません。");
        string className = "AConverted_" + CppSyntax.Name(root?.Name ?? "Blueprint");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Unique(string prefix, string name)
        {
            string candidate = prefix + CppSyntax.Name(name), result = candidate; int suffix = 2;
            while (!names.Add(result)) result = candidate + "_" + suffix++;
            return result;
        }
        var variables = new Dictionary<string, (string Name, string Type)>(StringComparer.Ordinal);
        var properties = new StringBuilder();
        var defaults = report.Documents.FirstOrDefault(d => d.Name == "Defaults")?.Objects.FirstOrDefault(o => o.ParentId is null)?.Properties;
        foreach (var property in root?.Properties.Where(p => p.Key.StartsWith("NewVariables(", StringComparison.Ordinal)) ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = UnrealTextParser.Fields(property.Value);
            string name = UnrealTextParser.Value(fields, "VarName");
            string? type = CppSyntax.Type(UnrealTextParser.Fields(fields.GetValueOrDefault("VarType", "()")));
            if (type is null || name.Length == 0) { Error("Variables", name, "未対応の変数型です（基本型以外・コンテナー等）。"); continue; }
            string native = Unique("Var_", name);
            variables.Add(name, (native, type));
            try
            {
                properties.AppendLine($"    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category=\"Converted\")\n    {type} {native} = {CppSyntax.Literal(type, defaults is not null ? UnrealTextParser.Value(defaults, name, UnrealTextParser.Value(fields, "DefaultValue")) : UnrealTextParser.Value(fields, "DefaultValue"))};");
            }
            catch (InvalidDataException error) { Error("Variables", name, error.Message); properties.AppendLine($"    {type} {native}{{}};"); }
            if (fields.TryGetValue("PropertyFlags", out var flags) && ulong.TryParse(flags, out ulong bits) && (bits & 0x20) != 0) Error("Variables", name, "複製プロパティの生成には未対応です。");
        }
        var declarations = new StringBuilder(); var bodies = new StringBuilder();
        foreach (var reference in root?.Properties.Where(p => p.Key.StartsWith("FunctionGraphs(", StringComparison.Ordinal) || p.Key.StartsWith("UbergraphPages(", StringComparison.Ordinal)) ?? [])
            if (!(blueprint?.Graphs.Any(g => reference.Value.Contains("EdGraph'" + g.Name + "'", StringComparison.Ordinal)) ?? false)) Error("Graphs", reference.Key, "参照されたユーザーグラフが見つかりません。");
        int count = 0; string? beginPlay = null;
        foreach (var graph in blueprint?.Graphs ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool userGraph = graph.Role is "FunctionGraphs" or "UbergraphPages";
            bool fromInterface = root?.Properties.Where(p => p.Key.StartsWith("ImplementedInterfaces(", StringComparison.Ordinal))
                .Any(p => p.Value.Contains("EdGraph'" + graph.Name + "'", StringComparison.Ordinal)) == true;
            if (!userGraph && !fromInterface) continue; // Compiler intermediates are not additional user functions.
            if (fromInterface) Error(graph.Name, "", "Blueprint専用インターフェースの実装には手動アダプターが必要です。");
            foreach (var node in graph.Nodes.Where(n => n.ClassPath.EndsWith(".K2Node_FunctionEntry", StringComparison.Ordinal) || n.ClassPath.EndsWith(".K2Node_Event", StringComparison.Ordinal) || n.ClassPath.EndsWith(".K2Node_CustomEvent", StringComparison.Ordinal)))
            {
                string member = UnrealTextParser.Value(UnrealTextParser.Fields(node.Properties.GetValueOrDefault("EventReference", "()")), "MemberName");
                bool isEvent = !node.ClassPath.EndsWith(".K2Node_FunctionEntry", StringComparison.Ordinal);
                string nativeName = Unique("Fn_", isEvent ? (member.Length > 0 ? member : UnrealTextParser.Value(node.Properties, "CustomFunctionName", node.Name)) : graph.Name);
                if (isEvent && member == "ReceiveBeginPlay" && node.Properties.GetValueOrDefault("EventReference", "").Contains("/Script/Engine.Actor'", StringComparison.Ordinal)) beginPlay = nativeName;
                else if (isEvent) Error(graph.Name, node.Name, "イベントは呼び出せる関数へ変換しますが、通知元との自動接続には未対応です。");
                if (graph.Name == "UserConstructionScript") Error(graph.Name, node.Name, "Construction Scriptのライフサイクル接続には未対応です。");
                var compiler = new GraphCompiler(graph, node, variables, Error, cancellationToken);
                var function = compiler.Compile(nativeName, className);
                declarations.AppendLine("    UFUNCTION(BlueprintCallable, Category=\"Converted\")\n    " + function.Declaration + ";");
                bodies.AppendLine(function.Body); count++;
            }
        }
        if (root?.Properties.Keys.Any(k => k is "SimpleConstructionScript") == true) Error("Components", "", "コンポーネント構成・アセット参照は自動再構築しません。Class.txtとDefaults.txtから手動で移してください。");
        if (root?.Properties.Keys.Any(k => k.StartsWith("MacroGraphs(", StringComparison.Ordinal)) == true) Error("Macros", "", "ローカルマクログラフには未対応です。");
        diagnostics.Add(new("Warning", "Defaults", "", "既定値は対応するNewVariablesのみを移します。親クラスの設定・複製・外部アセットは手動確認が必要です。"));
        string guard = diagnostics.Any(d => d.Severity == "Error") ? "#if !defined(UEETC_ALLOW_INCOMPLETE_BLUEPRINT)\n#error \"Incomplete Blueprint conversion. Read conversion.md before opting in.\"\n#endif\n" : "";
        string hash = Regex.IsMatch(report.AssetSha256, "^[A-Fa-f0-9]{64}$") ? report.AssetSha256 : "unknown";
        string header = $"// Experimental generated code. Source SHA256: {hash}\n#pragma once\n#include \"CoreMinimal.h\"\n#include \"{baseClass.Item2}\"\n#include \"ConvertedBlueprint.generated.h\"\n{guard}\nUCLASS(Blueprintable)\nclass BLUEPRINTCONVERSION_API {className} : public {baseClass.Item1}\n{{\n    GENERATED_BODY()\npublic:\n{properties}{declarations}" +
            (beginPlay is null ? "" : "protected:\n    virtual void BeginPlay() override;\n") + "};\n";
        string implementation = "#include \"ConvertedBlueprint.h\"\n\n" + bodies +
            (beginPlay is null ? "" : $"void {className}::BeginPlay()\n{{\n    Super::BeginPlay();\n    {beginPlay}();\n}}\n");
        return new(className, header, implementation, count, diagnostics.Distinct().ToArray());
    }
}
