using System.Text;
using System.Text.RegularExpressions;
using UEEtc.Blueprint;

namespace UEEtc.Blueprint.Cpp;

internal sealed class GraphCompiler(BlueprintGraph graph, BlueprintObject entry,
    IReadOnlyDictionary<string, (string Name, string Type)> variables, Action<string, string, string> error, CancellationToken token)
{
    private readonly Dictionary<string, BlueprintObject> nodes = graph.Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
    private readonly Dictionary<string, string> inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, string Type)> outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> states = new(StringComparer.Ordinal);
    private readonly HashSet<string> expressionPath = new(StringComparer.Ordinal);
    private static readonly string[] DataProperties = ["bOrphanedPin", "bNotConnectable"];

    public (string Declaration, string Body) Compile(string name, string className)
    {
        var arguments = new List<string>(); var initialization = new StringBuilder();
        int index = 0;
        foreach (var pin in entry.Pins.Where(p => p.Category != "exec" && p.Direction == "EGPD_Output"))
        {
            if (pin.Name == "OutputDelegate" && pin.Category == "delegate" && pin.Links.Count == 0) continue;
            string? type = CppSyntax.Type(pin.Fields, "PinType.");
            if (type is null) { error(graph.Name, entry.Name, "未対応の入力引数型: " + pin.Name); continue; }
            string native = "In_" + CppSyntax.Name(pin.Name) + "_" + index++;
            inputs[pin.Id] = native; arguments.Add(type + " " + native);
        }
        foreach (var result in graph.Nodes.Where(n => n.ClassPath.EndsWith(".K2Node_FunctionResult", StringComparison.Ordinal)))
            foreach (var pin in result.Pins.Where(p => p.Category != "exec" && p.Direction == "EGPD_Input"))
            {
                string? type = CppSyntax.Type(pin.Fields, "PinType.");
                if (type is null) { error(graph.Name, result.Name, "未対応の出力引数型: " + pin.Name); continue; }
                if (outputs.TryGetValue(pin.Name, out var existing))
                {
                    if (existing.Type != type) error(graph.Name, result.Name, "戻り値の型が一致しません: " + pin.Name);
                    continue;
                }
                string native = "Out_" + CppSyntax.Name(pin.Name) + "_" + index++;
                outputs.Add(pin.Name, (native, type)); arguments.Add(type + "& " + native);
                initialization.AppendLine($"    {native} = {type}{{}};");
            }
        var queue = new Queue<BlueprintObject>(); queue.Enqueue(entry);
        var reachable = new List<BlueprintObject>();
        while (queue.TryDequeue(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (states.ContainsKey(node.Name)) continue;
            states.Add(node.Name, states.Count); reachable.Add(node);
            foreach (var pin in node.Pins.Where(p => p.Category == "exec" && p.Direction == "EGPD_Output"))
                foreach (var link in pin.Links)
                    if (nodes.TryGetValue(link.NodeName, out var target) && target.Pins.Any(p => p.Id == link.PinId && p.Category == "exec" && p.Direction == "EGPD_Input")) queue.Enqueue(target);
                    else error(graph.Name, node.Name, "実行ピンの接続先が不正です。");
        }
        var body = new StringBuilder($"void {className}::{name}({string.Join(", ", arguments)})\n{{\n{initialization}    int32 State = 0;\n    TArray<int32> Pending;\n    for (int32 Steps = 0; Steps < 100000; ++Steps)\n    {{\n        if (State < 0) {{ if (Pending.IsEmpty()) return; State = Pending.Pop(EAllowShrinking::No); }}\n        switch (State)\n        {{\n");
        foreach (var node in reachable)
        {
            token.ThrowIfCancellationRequested();
            body.AppendLine($"        case {states[node.Name]}:\n        {{");
            try { body.Append(Statement(node)); }
            catch (InvalidDataException exception)
            {
                error(graph.Name, node.Name, exception.Message);
                body.AppendLine("            UE_LOG(LogTemp, Error, " + CppSyntax.Quote("Unconverted Blueprint node: " + graph.Name + "/" + node.Name) + ");\n            return;");
            }
            body.AppendLine("        }");
        }
        body.AppendLine("        default: return;\n        }\n    }\n    UE_LOG(LogTemp, Error, TEXT(\"Blueprint conversion execution budget exceeded\"));\n}\n");
        return ("void " + name + "(" + string.Join(", ", arguments) + ")", body.ToString());
    }

    private string Statement(BlueprintObject node)
    {
        if (node.Properties.GetValueOrDefault("EnabledState") is "Disabled" or "DevelopmentOnly") throw new InvalidDataException("無効/Development限定ノードには未対応です。");
        string kind = node.ClassPath.Split('.').Last();
        if (node == entry || kind == "K2Node_Knot") return Continue(Next(node));
        if (kind == "K2Node_VariableSet")
        {
            if (UnrealTextParser.Value(UnrealTextParser.Fields(node.Properties.GetValueOrDefault("VariableReference", "()")), "bSelfContext") != "True" || node.Pins.Any(p => p.Name == "self" && p.Links.Count > 0)) throw new InvalidDataException("self以外の変数代入には未対応です。");
            string member = Member(node, "VariableReference");
            if (!variables.TryGetValue(member, out var variable)) throw new InvalidDataException("未対応の代入先: " + member);
            var pin = node.Pins.SingleOrDefault(p => p.Name == member && p.Direction == "EGPD_Input") ?? throw new InvalidDataException("変数代入の入力がありません。");
            if (CppSyntax.Type(pin.Fields, "PinType.") != variable.Type) throw new InvalidDataException("代入型が一致しません。");
            return $"            {variable.Name} = {Input(pin)};\n" + Continue(Next(node));
        }
        if (kind == "K2Node_IfThenElse")
        {
            var pin = node.Pins.Single(p => p.Name == "Condition");
            if (pin.Category != "bool") throw new InvalidDataException("分岐条件がboolではありません。");
            return $"            State = ({Input(pin)}) ? {Next(node, "then")} : {Next(node, "else")};\n            break;\n";
        }
        if (kind == "K2Node_ExecutionSequence")
        {
            var successors = node.Pins.Where(p => p.Direction == "EGPD_Output" && p.Category == "exec").Select(p => Next(node, p.Name)).Where(s => s >= 0).ToArray();
            return string.Concat(successors.Skip(1).Reverse().Select(s => $"            Pending.Add({s});\n")) + Continue(successors.FirstOrDefault(-1));
        }
        if (kind == "K2Node_FunctionResult")
        {
            var text = new StringBuilder();
            foreach (var pin in node.Pins.Where(p => p.Category != "exec" && p.Direction == "EGPD_Input"))
            {
                if (!outputs.TryGetValue(pin.Name, out var output)) throw new InvalidDataException("未対応の戻り値: " + pin.Name);
                text.AppendLine($"            {output.Name} = {Input(pin)};");
            }
            return text.AppendLine("            return;").ToString();
        }
        throw new InvalidDataException("未対応の実行ノード: " + kind + " " + Member(node, "FunctionReference"));
    }

    private string Input(BlueprintPin pin)
    {
        token.ThrowIfCancellationRequested();
        if (DataProperties.Any(p => UnrealTextParser.Value(pin.Fields, p) == "True")) throw new InvalidDataException("切り離し/接続禁止ピンには未対応です。");
        string type = CppSyntax.Type(pin.Fields, "PinType.") ?? throw new InvalidDataException("未対応のピン型: " + pin.Name);
        if (pin.Links.Count == 0) return CppSyntax.Literal(type, UnrealTextParser.Value(pin.Fields, "DefaultValue"));
        if (pin.Links.Count != 1) throw new InvalidDataException("複数入力のデータピンには未対応です。");
        var link = pin.Links[0];
        if (!nodes.TryGetValue(link.NodeName, out var node)) throw new InvalidDataException("データ接続先が見つかりません。");
        var source = node.Pins.SingleOrDefault(p => p.Id == link.PinId && p.Direction == "EGPD_Output") ?? throw new InvalidDataException("データ出力ピンが見つかりません。");
        if (CppSyntax.Type(source.Fields, "PinType.") != type) throw new InvalidDataException("暗黙の型変換には未対応です。");
        string key = node.Id + "/" + source.Id;
        if (!expressionPath.Add(key) || expressionPath.Count > 128) throw new InvalidDataException("循環または深すぎるデータ式です。");
        try
        {
            if (node.Properties.GetValueOrDefault("EnabledState") is "Disabled" or "DevelopmentOnly") throw new InvalidDataException("無効/Development限定のデータノードには未対応です。");
            string kind = node.ClassPath.Split('.').Last();
            if (node == entry && inputs.TryGetValue(source.Id, out string? parameter)) return parameter;
            if (kind == "K2Node_Knot") return Input(node.Pins.Single(p => p.Direction == "EGPD_Input"));
            if (kind is "K2Node_VariableGet" or "K2Node_VariableSet")
            {
                var reference = UnrealTextParser.Fields(node.Properties.GetValueOrDefault("VariableReference", "()"));
                if (UnrealTextParser.Value(reference, "bSelfContext") != "True" || UnrealTextParser.Value(reference, "MemberScope").Length > 0 || node.Pins.Any(p => p.Name == "self" && p.Links.Count > 0)) throw new InvalidDataException("ローカル/他オブジェクト変数には未対応です。");
                if (variables.TryGetValue(Member(node, "VariableReference"), out var variable) && variable.Type == type) return variable.Name;
            }
            if (kind is "K2Node_CallFunction" or "K2Node_PromotableOperator") return MathExpression(node, type);
            throw new InvalidDataException("未対応のデータノード: " + kind);
        }
        finally { expressionPath.Remove(key); }
    }

    private string MathExpression(BlueprintObject node, string resultType)
    {
        var reference = UnrealTextParser.Fields(node.Properties.GetValueOrDefault("FunctionReference", "()"));
        string member = UnrealTextParser.Value(reference, "MemberName");
        if (!UnrealTextParser.Value(reference, "MemberParent").Contains("/Script/Engine.KismetMathLibrary'", StringComparison.Ordinal)) throw new InvalidDataException("未対応の関数: " + member);
        var data = node.Pins.Where(p => p.Direction == "EGPD_Input" && p.Category != "exec" && p.Name != "self").OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        if (node.Pins.Any(p => p.Category == "exec")) throw new InvalidDataException("副作用を持つ式は展開しません。");
        var match = Regex.Match(member, "^(Add|Subtract|Multiply|Less|Greater|LessEqual|GreaterEqual|EqualEqual|NotEqual)_(IntInt|Int64Int64|DoubleDouble|FloatFloat|NameName|StrStr)$");
        string op = match.Groups[1].Value switch { "Add" => "+", "Subtract" => "-", "Multiply" => "*", "Less" => "<", "Greater" => ">", "LessEqual" => "<=", "GreaterEqual" => ">=", "EqualEqual" => "==", "NotEqual" => "!=", _ => "" };
        if (!match.Success || data.Length != 2 || data[0].Name != "A" || data[1].Name != "B" || data.Select(p => CppSyntax.Type(p.Fields, "PinType.")).Distinct().Take(2).Count() != 1) throw new InvalidDataException("未対応の演算関数: " + member);
        string operandType = CppSyntax.Type(data[0].Fields, "PinType.")!;
        string expectedType = match.Groups[2].Value switch { "IntInt" => "int32", "Int64Int64" => "int64", "FloatFloat" => "float", "DoubleDouble" => "double", "NameName" => "FName", _ => "FString" };
        if (operandType != expectedType || operandType is "FName" or "FString" && op is not ("==" or "!=")) throw new InvalidDataException("関数シグネチャと演算型が一致しません。");
        bool arithmetic = op is "+" or "-" or "*";
        if (arithmetic && operandType is not ("int32" or "int64" or "float" or "double") || resultType != (arithmetic ? operandType : "bool")) throw new InvalidDataException("演算型の組み合わせに未対応です。");
        string left = Input(data[0]), right = Input(data[1]);
        if (left.Length + right.Length > 8192) throw new InvalidDataException("式が大きすぎるため展開を停止しました。");
        if (arithmetic && operandType is "int32" or "int64")
        {
            string unsigned = operandType == "int32" ? "uint32" : "uint64";
            return $"static_cast<{operandType}>(static_cast<{unsigned}>({left}) {op} static_cast<{unsigned}>({right}))";
        }
        return "(" + left + " " + op + " " + right + ")";
    }

    private int Next(BlueprintObject node, string? pinName = null)
    {
        var pins = node.Pins.Where(p => p.Category == "exec" && p.Direction == "EGPD_Output" && (pinName is null || p.Name == pinName)).ToArray();
        if (pins.Length == 0) return -1;
        if (pins.Length != 1 || pins[0].Links.Count > 1) throw new InvalidDataException("複数の実行出力にはSequenceが必要です。");
        return pins[0].Links.Count == 0 ? -1 : states.GetValueOrDefault(pins[0].Links[0].NodeName, -1);
    }
    private static string Continue(int next) => $"            State = {next};\n            break;\n";
    private static string Member(BlueprintObject node, string key) => UnrealTextParser.Value(UnrealTextParser.Fields(node.Properties.GetValueOrDefault(key, "()")), "MemberName");
}
