using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UEEtc.Core.Plans;
using UEEtc.Core.Source;

namespace UEEtc.Core.Features.EventCounters;

public sealed record EventCounterCandidate(string Id, string RelativePath, string Function, int Line,
    string Branch, string Condition, string Hint, string Reason, string SuggestedName, string DisplayName)
{
    public IReadOnlyList<string> AddValues { get; init; } = [];
    internal int Position { get; init; }
    internal string Indent { get; init; } = "";
}

public sealed record EventCounterSelection(string Id, string CounterName, string DisplayName, QuantityCounterSelection? Quantity = null);

public sealed class EventCounterScan
{
    public AnalysisPlan Snapshot { get; }
    public IReadOnlyList<EventCounterCandidate> Candidates { get; }
    internal HashSet<string> ReservedNames { get; }
    internal EventCounterScan(AnalysisPlan snapshot, List<EventCounterCandidate> candidates, HashSet<string> reserved)
    {
        Snapshot = snapshot;
        Candidates = candidates.AsReadOnly();
        ReservedNames = reserved;
    }
}

public sealed record EventCounterSelectionFile(string RootPath, string Fingerprint, IReadOnlyList<EventCounterSelection> Selections);

public sealed class EventCounterService
{
    public EventCounterScan Analyze(string rootPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var candidates = new List<EventCounterCandidate>();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = new SourceWorkspace().Analyze(rootPath, "EventCounters", Read,
            text => reserved.UnionWith(Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]*").Select(m => m.Value)), progress, cancellationToken);
        var duplicateLabels = candidates.GroupBy(c => c.DisplayName, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return new(snapshot, candidates.Select(c => duplicateLabels.Contains(c.DisplayName) ? c with { DisplayName = c.DisplayName + " [" + c.Id + "]" } : c).ToList(), reserved);

        FileAnalysis Read(string relativePath, byte[] bytes)
        {
            try
            {
                var document = SourceDocument.Read(bytes);
                var syntax = new CppStructure(document.Text);
                foreach (var function in syntax.Functions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int ordinal = 0;
                    Visit(function.Open + 1, function.Close);

                    void Visit(int start, int end)
                    {
                        var tokens = syntax.Tokens;
                        for (int i = start; i < end; i++)
                        {
                            if (tokens[i].Literal) continue;
                            if (tokens[i].Text is "for" or "while" or "switch" or "catch" or "do")
                            {
                                i = StatementEnd(i, end);
                                continue;
                            }
                            if (tokens[i].Text == "if")
                            {
                                int paren = i + 1;
                                if (paren < end && tokens[paren].Text == "constexpr")
                                {
                                    // Runtime counters would invalidate constexpr branches. Skip the whole statement.
                                    i = SkipStatement(paren + 1, end);
                                    continue;
                                }
                                if (paren >= end || tokens[paren].Text != "(") continue;
                                int closeParen = syntax.Pairs[paren];
                                string condition = document.Text[tokens[paren].End..tokens[closeParen].Start].Trim();
                                int open = closeParen + 1;
                                if (open < end && tokens[open].Text == "{")
                                {
                                    int close = syntax.Pairs[open];
                                    Add(open, close, "If", condition);
                                    Visit(open + 1, close);
                                    i = close;
                                    if (i + 2 < end && tokens[i + 1].Text == "else" && tokens[i + 2].Text == "{")
                                    {
                                        open = i + 2; close = syntax.Pairs[open];
                                        Add(open, close, "Else", condition);
                                        Visit(open + 1, close);
                                        i = close;
                                    }
                                }
                                else i = closeParen; // Unbraced statements are never rewritten.
                            }
                            else if (tokens[i].Text is "{" or "(" or "[")
                            {
                                // Excludes lambdas, loops, switch/catch and local classes, including conditions in lambda bodies.
                                i = syntax.Pairs[i];
                            }
                        }

                        int SkipStatement(int paren, int limit)
                        {
                            if (paren >= limit || tokens[paren].Text != "(") return paren;
                            int next = syntax.Pairs[paren] + 1;
                            if (next < limit && tokens[next].Text == "{")
                            {
                                next = syntax.Pairs[next];
                                if (next + 2 < limit && tokens[next + 1].Text == "else" && tokens[next + 2].Text == "{") next = syntax.Pairs[next + 2];
                                return next;
                            }
                            return next;
                        }

                        int StatementEnd(int first, int limit)
                        {
                            if (first >= limit) return limit - 1;
                            if (tokens[first].Text == "{") return syntax.Pairs[first];
                            if (tokens[first].Text == "do")
                            {
                                int last = StatementEnd(first + 1, limit);
                                if (last + 2 < limit && tokens[last + 1].Text == "while" && tokens[last + 2].Text == "(")
                                    return Math.Min(limit - 1, syntax.Pairs[last + 2] + 1);
                                return last;
                            }
                            if (tokens[first].Text is "if" or "for" or "while" or "switch" or "catch")
                            {
                                int paren = first + 1;
                                if (paren < limit && tokens[paren].Text == "constexpr") paren++;
                                if (paren < limit && tokens[paren].Text == "(")
                                {
                                    int last = StatementEnd(syntax.Pairs[paren] + 1, limit);
                                    if (tokens[first].Text == "if" && last + 1 < limit && tokens[last + 1].Text == "else")
                                        last = StatementEnd(last + 2, limit);
                                    return last;
                                }
                            }
                            for (int j = first; j < limit; j++)
                            {
                                if (tokens[j].Literal) continue;
                                if (tokens[j].Text == ";") return j;
                                if (tokens[j].Text is "{" or "(" or "[") j = syntax.Pairs[j];
                            }
                            return limit - 1;
                        }

                        void Add(int open, int close, string branch, string condition)
                        {
                            int number = ++ordinal;
                            if (open + 1 == close) return;
                            for (int j = open + 1; j < close; j++)
                            {
                                if (!tokens[j].Literal && j + 1 < close && tokens[j + 1].Text == "(" && tokens[j].Text.StartsWith("TRACE_COUNTER_", StringComparison.Ordinal)) return;
                                if (!tokens[j].Literal && tokens[j].Text is "{" or "(" or "[") j = syntax.Pairs[j];
                            }
                            int line = document.Text.AsSpan(0, tokens[open].Start).Count('\n') + 1;
                            string identity = relativePath + "|" + function.Signature + "|" + number + "|" + branch + "|" + condition;
                            string id = Hash(identity);
                            string name = Regex.Replace(function.Name, @"[^A-Za-z0-9]+", "_").Trim('_');
                            if (name.Length == 0 || !char.IsAsciiLetter(name[0])) name = "Event_" + name;
                            name += "_" + branch + number + "_Count";
                            string hint = Regex.IsMatch(condition, "cache|hit|miss", RegexOptions.IgnoreCase) ? "キャッシュ候補" :
                                Regex.IsMatch(condition, "fail|error|success|result|cancel|valid|null|blocked|missing", RegexOptions.IgnoreCase) ? "結果・ガード候補" :
                                Regex.IsMatch(condition, "retry|recover|stale", RegexOptions.IgnoreCase) ? "再試行・復旧候補" :
                                Regex.IsMatch(condition, "clear|remain|health|lives|spawn|dead|destroy", RegexOptions.IgnoreCase) ? "状態遷移候補" : "条件分岐";
                            int lineStart = document.Text.LastIndexOf('\n', Math.Max(0, tokens[open].Start - 1)) + 1;
                            string indent = new(document.Text[lineStart..tokens[open].Start].TakeWhile(c => c is ' ' or '\t').ToArray());
                            candidates.Add(new(id, relativePath, function.Name, line, branch, condition, hint,
                                (branch == "If" ? "条件が真の分岐" : "対応条件が偽の分岐") + "への到達を数える。成功・失敗の意味は要確認。",
                                name, "UEEtc/Events/" + relativePath + "/" + function.Name + "/" + branch + number)
                            { Position = tokens[open].End, Indent = indent + "\t", AddValues = QuantityCounterValues.Find(function, tokens, tokens[open].End) });
                        }
                    }
                }
                return new(relativePath, [], []) { OriginalBytes = bytes, ProposedBytes = bytes, OriginalText = document.Text, ProposedText = document.Text };
            }
            catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
            {
                return new(relativePath, [], [error.Message]) { OriginalBytes = bytes, ProposedBytes = bytes };
            }
        }
    }

    public AnalysisPlan BuildPlan(EventCounterScan scan, IEnumerable<EventCounterSelection> selections,
        CancellationToken cancellationToken = default)
    {
        var requested = selections.ToArray();
        if (requested.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.CounterName) || string.IsNullOrWhiteSpace(s.DisplayName)))
            throw new ArgumentException("選択には Id / CounterName / DisplayName が必要です。");
        var available = scan.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        if (requested.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != requested.Length)
            throw new ArgumentException("同じ候補が複数回選択されています。");
        var used = new HashSet<string>(scan.ReservedNames, StringComparer.Ordinal);
        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
        var metrics = requested.SelectMany(s => new[] { s.CounterName }.Concat(s.Quantity is { } q ? new[] { q.CounterName } : []));
        if (metrics.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("数量カウンターの名前がありません。");
        var groups = metrics.GroupBy(name => name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var displays = requested.SelectMany(s => new[] { s.DisplayName }.Concat(s.Quantity is { } q ? new[] { q.DisplayName } : []));
        var duplicateDisplays = displays.GroupBy(name => name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var s in requested.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!available.ContainsKey(s.Id)) throw new ArgumentException("候補が存在しません。再解析してください: " + s.Id);
            Allocate(s.Id, s.CounterName, s.DisplayName);
            if (s.Quantity is { } q)
            {
                if (!q.MeaningConfirmed || string.IsNullOrWhiteSpace(q.Unit) || q.Unit.Any(char.IsControl))
                    throw new ArgumentException("ADD の数量・単位・この位置で数える意味を確認してください。");
                if (string.IsNullOrWhiteSpace(q.Value) || !QuantityCounterValues.IsAllowed(q.Value, available[s.Id]))
                    throw new ArgumentException("ADD 値は候補の AddValues または 0～4294967295 の十進整数だけを指定できます。関数呼び出し・任意式・未確認の変数は使用できません。");
                Allocate(s.Id + "_Quantity", q.CounterName, q.DisplayName);
            }
        }

        void Allocate(string id, string requestedName, string display)
        {
            if (!Regex.IsMatch(requestedName, @"^[A-Za-z][A-Za-z0-9_]*$") || requestedName.Contains("__", StringComparison.Ordinal))
                throw new ArgumentException("カウンター名は英字で始まる識別子にしてください（二重アンダースコアは不可）。");
            if (string.IsNullOrWhiteSpace(display) || display.Any(char.IsControl))
                throw new ArgumentException("表示名には空白だけの名前や制御文字を使用できません。");
            string name = requestedName;
            if (groups[name] > 1 || used.Contains(name) || used.Contains("__GTraceCounter" + name)) name += "_" + id;
            string stem = name;
            for (int suffix = 2; used.Contains(name) || used.Contains("__GTraceCounter" + name); suffix++) name = stem + "_" + suffix;
            used.Add(name); used.Add("__GTraceCounter" + name);
            assigned.Add(id, name);
        }
        var files = scan.Snapshot.Files.Select(file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = requested.Where(s => available[s.Id].RelativePath == file.RelativePath).ToArray();
            if (selected.Length == 0) return file;
            var doc = SourceDocument.Read(file.OriginalBytes);
            var syntax = new CppStructure(doc.Text);
            string newline = doc.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var edits = new List<(int Position, string Text)>();
            var rows = new List<FunctionAnalysis>();
            var declarations = new List<string>();
            foreach (var s in selected)
            {
                var c = available[s.Id]; string name = assigned[s.Id];
                string insert = newline + c.Indent + "// UEEtc: event " + c.Id + newline + c.Indent + "TRACE_COUNTER_INCREMENT(" + name + ");" + newline;
                string display = duplicateDisplays.Contains(s.DisplayName) ? s.DisplayName + " [" + s.Id + "]" : s.DisplayName;
                declarations.Add("\tTRACE_DECLARE_ATOMIC_INT_COUNTER(" + name + ", TEXT(\"" + Escape(display) + "\"));");
                if (s.Quantity is { } q)
                {
                    string quantityName = assigned[s.Id + "_Quantity"];
                    string quantityDisplay = q.DisplayName + (duplicateDisplays.Contains(q.DisplayName) ? " [" + c.Id + " Quantity]" : "") + " [" + q.Unit + "]";
                    declarations.Add("\tTRACE_DECLARE_ATOMIC_INT_COUNTER(" + quantityName + ", TEXT(\"" + Escape(quantityDisplay) + "\"));");
                    insert += c.Indent + "TRACE_COUNTER_ADD(" + quantityName + ", " + q.Value + ");" + newline;
                }
                edits.Add((c.Position, insert));
                rows.Add(new(c.Function, c.Line, "Add", c.Reason, name) { EventCounterAdded = true, CounterDecision = c.Branch + ": " + c.Condition,
                    QuantityCounter = s.Quantity is null ? null : assigned[s.Id + "_Quantity"], QuantityValue = s.Quantity?.Value, QuantityUnit = s.Quantity?.Unit });
            }
            string header = syntax.HasCounterInclude ? "" : "#include \"ProfilingDebugging/CountersTrace.h\"" + newline;
            if (syntax.IncludeAnchor > 0 && doc.Text[syntax.IncludeAnchor - 1] != '\n') header = newline + header;
            edits.Add((syntax.IncludeAnchor, header + newline + "// UEEtc: selected branch entry counters" + newline + "namespace {" + newline + string.Join(newline, declarations) + newline + "}" + newline));
            var output = new StringBuilder(doc.Text);
            foreach (var edit in edits.OrderByDescending(e => e.Position)) output.Insert(edit.Position, edit.Text);
            string text = output.ToString();
            return file with { Functions = rows, ProposedText = text, ProposedBytes = doc.Encode(text) };
        }).ToArray();
        return scan.Snapshot with { Files = files };
    }

    public EventCounterSelectionFile CreateSelection(EventCounterScan scan) => new(scan.Snapshot.RootPath, Fingerprint(scan), []);
    public AnalysisPlan BuildPlan(EventCounterScan scan, EventCounterSelectionFile selection, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(selection.RootPath, scan.Snapshot.RootPath, StringComparison.OrdinalIgnoreCase) || selection.Fingerprint != Fingerprint(scan))
            throw new InvalidOperationException("選択ファイルのソースが解析時点と異なります。候補を再解析してください。");
        return BuildPlan(scan, selection.Selections ?? throw new ArgumentException("Selections がありません。"), cancellationToken);
    }

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Fingerprint(EventCounterScan scan) => Hash(string.Join("\n", scan.Snapshot.Files.Select(f => f.RelativePath + ":" + Convert.ToHexString(SHA256.HashData(f.OriginalBytes)))) + "\n" + string.Join("\n", scan.ReservedNames.Order(StringComparer.Ordinal)));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
