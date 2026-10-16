using UEEtc.Core.Source;
using UEEtc.Core.Plans;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UEEtc.Core.Features.FunctionTracing;

public sealed class CppInstrumenter
{
    public FileAnalysis Analyze(string relativePath, byte[] bytes, InstrumentationOptions options)
    {
        var file = AnalyzeUnresolved(relativePath, bytes, options);
        return CounterNaming.Resolve([file], CounterNaming.Identifiers(file.OriginalText))[0];
    }

    internal FileAnalysis AnalyzeUnresolved(string relativePath, byte[] bytes, InstrumentationOptions options)
    {
        var functions = new List<FunctionAnalysis>();
        var diagnostics = new List<string>();
        try
        {
            var document = SourceDocument.Read(bytes);
            var scanned = CppLexer.Scan(document.Text);
            var tokens = scanned.Where(t => !t.Directive).ToList();
            var matches = MatchBraces(tokens);
            var edits = new List<(int Position, string Text)>();
            var counters = new List<string>();
            var pendingCounters = new Dictionary<string, string>(StringComparer.Ordinal);
            bool needsCpuHeader = false;
            bool needsCounterHeader = false;
            string newline = document.Text.Contains("\r\n") ? "\r\n" : "\n";
            // Keep the own header first, and declare counters after headers that define their macros.
            int firstCode = tokens.Count > 0 ? tokens[0].Start : document.Text.Length;
            var includes = UnconditionalIncludes(scanned).Where(t => t.End <= firstCode).ToList();
            var anchor = includes.LastOrDefault();
            Visit(0, tokens.Count, "");

            if (edits.Count > 0)
            {
                string declarations = "";
                if (needsCpuHeader && !Includes(includes, "CpuProfilerTrace.h"))
                    declarations += "#include \"ProfilingDebugging/CpuProfilerTrace.h\"" + newline;
                if (needsCounterHeader && !Includes(includes, "CountersTrace.h"))
                    declarations += "#include \"ProfilingDebugging/CountersTrace.h\"" + newline;
                if (counters.Count > 0)
                    declarations += newline + "// UEEtc: cumulative function entry counters" + newline +
                        "namespace {" + newline + string.Join(newline, counters) + newline + "}" + newline;
                int position = anchor!.End;
                if (position > 0 && document.Text[position - 1] != '\n') declarations = newline + declarations;
                edits.Add((position, declarations));
            }
            var result = new StringBuilder(document.Text);
            foreach (var edit in edits.OrderByDescending(e => e.Position)) result.Insert(edit.Position, edit.Text);
            string proposed = result.ToString();
            return new(relativePath, functions, diagnostics)
            {
                OriginalBytes = bytes, ProposedBytes = document.Encode(proposed),
                OriginalText = document.Text, ProposedText = proposed
            };

            void Visit(int start, int end, string namespaceName)
            {
                int segment = start;
                for (int i = start; i < end; i++)
                {
                    if (tokens[i].Text == ";") { segment = i + 1; continue; }
                    if (tokens[i].Text != "{" || tokens[i].Literal) continue;
                    int close = matches[i];
                    var header = tokens.GetRange(segment, i - segment);
                    if (header.Count > 0 && (header[0].Text == "namespace" ||
                        (header.Count == 2 && header[0].Text == "extern" && header[1].Literal)))
                        Visit(i + 1, close, namespaceName + string.Concat(header.Select(t => t.Text)) + "/");
                    else
                        AnalyzeFunction(header, i, close, namespaceName);
                    i = close;
                    segment = i + 1;
                }
            }

            void AnalyzeFunction(List<CppToken> header, int open, int close, string namespaceName)
            {
                int paren = header.FindIndex(t => t.Text == "(" && !t.Literal);
                if (paren < 1 || !Identifier(header[paren - 1].Text)) return;
                int nameStart = paren - 1;
                while (nameStart >= 2 && header[nameStart - 1].Text == "::" && Identifier(header[nameStart - 2].Text))
                    nameStart -= 2;
                if (nameStart > 0 && header[nameStart - 1].Text == "~") nameStart--;
                string name = string.Concat(header.Skip(nameStart).Take(paren - nameStart).Select(t => t.Text));
                if (name is "if" or "for" or "while" or "switch" or "catch") return;
                int line = document.Text.AsSpan(0, header[nameStart].Start).Count('\n') + 1;
                var body = tokens.GetRange(open + 1, close - open - 1);
                string? reason = null;
                if (header.Any(t => t.Text is "constexpr" or "consteval" or "template" or "operator" or "requires" or "try"))
                    reason = "constexpr / template / operator / requires / function-try-block は手動確認";
                else if (nameStart == 0 || name.Contains('~') ||
                    (name.Contains("::") && name.Split("::")[^1] == name.Split("::")[^2]))
                    reason = "コンストラクター／デストラクターは対象外";
                else if (header.Take(nameStart).Any(t => t.Text is "=" or "[" or "]" or ":") ||
                    header.Skip(paren).Any(t => t.Text is "->" or ":"))
                    reason = "特殊な宣言構文は手動確認";
                else if (body.Count == 0) reason = "空の関数";
                else if (body.Any(t => t.Text is "co_await" or "co_yield" or "co_return"))
                    reason = "コルーチンのスコープ寿命は手動確認";
                else if (anchor is null) reason = "無条件の #include がなく挿入位置を確定できない";
                else if (anchor.End > header[0].Start) reason = "関数より前に無条件の #include がない";
                else if (!options.CpuScopes && !options.CallCounters) reason = "付与するマクロが未選択";
                if (reason is not null)
                {
                    functions.Add(new(name, line, "Skip", reason)
                    {
                        CpuDecision = options.CpuScopes ? reason : "未選択",
                        CounterDecision = options.CallCounters ? reason : "未選択"
                    });
                    return;
                }
                bool existingCpu = HasCall(t => t.StartsWith("TRACE_CPUPROFILER", StringComparison.Ordinal) ||
                    t.Contains("SCOPE_CYCLE_COUNTER", StringComparison.Ordinal) || t.StartsWith("SCOPED_NAMED_EVENT", StringComparison.Ordinal));
                bool existingCounter = HasCall(t => t.StartsWith("TRACE_COUNTER_", StringComparison.Ordinal));
                bool addCpu = options.CpuScopes && !existingCpu;
                bool addCounter = options.CallCounters && !existingCounter;
                string cpuDecision = !options.CpuScopes ? "未選択" : existingCpu ? "既存の CPU 計測を維持" : "追加：関数全体の時間";
                string counterDecision = !options.CallCounters ? "未選択" : existingCounter ? "既存のカウンター操作を維持（意味は自動推定しない）" : "追加：関数進入回数（成功・命中数ではない）";
                string decisions = "CPU: " + cpuDecision + " / Counter: " + counterDecision;
                if (!addCpu && !addCounter)
                {
                    functions.Add(new(name, line, "Skip", decisions) { CpuDecision = cpuDecision, CounterDecision = counterDecision });
                    return;
                }
                string signature = namespaceName + string.Join(" ", header.Select(t => t.Text));
                string identity = relativePath.Replace('\\', '/') + "|" + signature;
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
                string label = "UEEtc/" + relativePath.Replace('\\', '/') + "/" + namespaceName + name + " [" + hash + "]";
                // Resolve names together across the selected source tree after candidate discovery.
                string? counter = null;
                if (addCounter && !pendingCounters.TryGetValue(identity, out counter))
                {
                    counter = "UEEtcPendingCounter_" + pendingCounters.Count;
                    while (document.Text.Contains(counter, StringComparison.Ordinal)) counter += "_";
                    pendingCounters.Add(identity, counter);
                }
                string indent = Indent(document.Text, tokens[open].Start) + "\t";
                string insert = newline + indent + "// UEEtc: function entry" + newline;
                if (addCpu)
                {
                    needsCpuHeader = true;
                    insert += indent + "TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"" + Escape(label) + "\");" + newline;
                }
                if (addCounter)
                {
                    needsCounterHeader = true;
                    string declaration = "\tTRACE_DECLARE_ATOMIC_INT_COUNTER(" + counter + ", TEXT(\"" + Escape(label + "/Calls") + "\"));";
                    if (!counters.Contains(declaration)) counters.Add(declaration);
                    insert += indent + "TRACE_COUNTER_INCREMENT(" + counter + ");" + newline;
                }
                // Existing bytes after the brace are left intact, including one-line functions.
                edits.Add((tokens[open].End, insert));
                functions.Add(new(name, line, "Add", decisions, counter)
                {
                    CpuScopeAdded = addCpu, CallCounterAdded = addCounter,
                    CpuDecision = cpuDecision, CounterDecision = counterDecision,
                    CounterBaseName = CounterNaming.BaseName(namespaceName, name),
                    CounterIdentity = identity, CounterHash = hash
                });

                bool HasCall(Func<string, bool> predicate)
                {
                    for (int i = 0; i + 1 < body.Count; i++)
                        if (!body[i].Literal && body[i + 1].Text == "(" && predicate(body[i].Text)) return true;
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
        {
            diagnostics.Add(exception.Message);
            return new(relativePath, [], diagnostics) { OriginalBytes = bytes, ProposedBytes = bytes };
        }
    }

    private static Dictionary<int, int> MatchBraces(List<CppToken> tokens)
    {
        var stack = new Stack<int>();
        var pairs = new Dictionary<int, int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Literal) continue;
            if (tokens[i].Text == "{") stack.Push(i);
            if (tokens[i].Text == "}") pairs[stack.Pop()] = i;
        }
        return pairs;
    }

    private static List<CppToken> UnconditionalIncludes(List<CppToken> scanned)
    {
        int depth = 0;
        var includes = new List<CppToken>();
        foreach (var token in scanned)
        {
            if (!token.Directive) continue;
            string name = CppLexer.DirectiveName(token);
            if (name is "if" or "ifdef" or "ifndef") depth++;
            else if (name == "endif") depth--;
            else if (name == "include" && depth == 0) includes.Add(token);
        }
        return includes;
    }

    private static bool Includes(List<CppToken> includes, string file) => includes.Any(t =>
        Regex.IsMatch(t.Text, "[\"<]ProfilingDebugging/" + Regex.Escape(file) + "[\">]"));
    private static bool Identifier(string value) => Regex.IsMatch(value, @"^[A-Za-z_]\w*$");
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Indent(string text, int position)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        int end = start;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return text[start..end];
    }
}
