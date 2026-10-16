using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UEEtc.Core.Plans;
using UEEtc.Core.Source;
using UEEtc.Core.Reporting;

namespace UEEtc.Core.Features.EventCounters;

public sealed record EventCounterCandidate(string Id, string RelativePath, string Function, int Line,
    string Branch, string Condition, string Hint, string Reason, string SuggestedName, string DisplayName)
{
    public IReadOnlyList<string> AddValues { get; init; } = [];
    public QuantityRecommendation? RecommendedQuantity { get; init; }
    internal int Position { get; init; }
    internal string Indent { get; init; } = "";
}

public sealed record EventCounterSelection(string Id, string CounterName, string DisplayName, QuantityCounterSelection? Quantity = null,
    CounterMeasurement Measurement = CounterMeasurement.Cumulative, bool IncludeActiveScope = false);
public sealed record EventCounterPreviewResult(AnalysisPlan? Plan, IReadOnlyList<string> Errors);

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
                            var addValues = QuantityCounterValues.Find(function, tokens, tokens[open].End);
                            candidates.Add(new(id, relativePath, function.Name, line, branch, condition, hint,
                                (branch == "If" ? "条件が真の分岐" : "対応条件が偽の分岐") + "への到達を数える。成功・失敗の意味は要確認。",
                                name, TraceDisplayNames.Event(function.Name, branch, number))
                            { Position = tokens[open].End, Indent = SourceIndentation.Block(document.Text, tokens, open, close), AddValues = addValues,
                                RecommendedQuantity = QuantityRecommendation.Find(addValues, tokens, open, close, syntax.Pairs) });
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
        => Build(scan, selections, false, cancellationToken);

    public AnalysisPlan BuildPreview(EventCounterScan scan, IEnumerable<EventCounterSelection> selections,
        CancellationToken cancellationToken = default)
        => Build(scan, selections, true, cancellationToken);

    public EventCounterPreviewResult TryBuildPreview(EventCounterScan scan, IEnumerable<EventCounterSelection> selections, CancellationToken cancellationToken = default)
    {
        var requested = selections.ToArray();
        var errors = Validate(scan, requested, true);
        return errors.Count > 0 ? new(null, errors) : new(Build(scan, requested, true, cancellationToken), []);
    }

    private static IReadOnlyList<string> Validate(EventCounterScan scan, EventCounterSelection[] requested, bool allowUnconfirmed)
    {
        var errors = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var available = scan.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var s in requested)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Id) || !available.TryGetValue(s.Id, out var candidate)) { errors.Add("候補が存在しません。再解析してください。"); continue; }
            if (!seen.Add(s.Id)) errors.Add("同じ候補が複数回選択されています。");
            Metric(s.CounterName, s.DisplayName);
            if (!Enum.IsDefined(s.Measurement)) errors.Add(candidate.DisplayName + ": 未知のカウンター計測モードです。");
            if (s.IncludeActiveScope && s.Measurement == CounterMeasurement.ActiveScope) errors.Add(candidate.DisplayName + ": スコープ現在数の二重設定を解除してください。");
            if (s.Quantity is not { } q) continue;
            Metric(q.CounterName, q.DisplayName);
            if (string.IsNullOrWhiteSpace(q.Unit) || q.Unit.Any(char.IsControl)) errors.Add(candidate.DisplayName + ": ADD の単位を入力してください。");
            if (string.IsNullOrWhiteSpace(q.Value) || !QuantityCounterValues.IsAllowed(q.Value, candidate)) errors.Add(candidate.DisplayName + ": ADD の値を候補一覧または十進整数で入力してください。");
            if (!allowUnconfirmed && !q.MeaningConfirmed) errors.Add(candidate.DisplayName + ": ADD の数量と単位の意味を確認してください。");
        }
        return errors;
        void Metric(string name, string display)
        {
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]*$") || name.Contains("__", StringComparison.Ordinal)) errors.Add("カウンター名は英字で始まる識別子にしてください（二重アンダースコアは不可）。");
            if (string.IsNullOrWhiteSpace(display) || display.Any(char.IsControl)) errors.Add("表示名には空白だけの名前や制御文字を使用できません。");
        }
    }

    private AnalysisPlan Build(EventCounterScan scan, IEnumerable<EventCounterSelection> selections,
        bool allowUnconfirmed, CancellationToken cancellationToken)
    {
        var requested = selections.ToArray();
        var validation = Validate(scan, requested, allowUnconfirmed);
        if (validation.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, validation));
        var available = scan.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var used = new HashSet<string>(scan.ReservedNames, StringComparer.Ordinal);
        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
        var metrics = requested.SelectMany(s => new[] { s.CounterName }.Concat(s.Quantity is { } q ? new[] { q.CounterName } : []).Concat(s.IncludeActiveScope ? new[] { EventCounterDefaults.ActiveScopeName(s.CounterName) } : []));
        var groups = metrics.GroupBy(name => name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var displays = requested.SelectMany(s => new[] { s.DisplayName }.Concat(s.Quantity is { } q ? new[] { q.DisplayName } : []).Concat(s.IncludeActiveScope ? new[] { s.DisplayName + " ActiveScopes" } : []));
        var duplicateDisplays = displays.GroupBy(name => name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var s in requested.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Allocate(s.Id, s.CounterName);
            if (s.IncludeActiveScope) Allocate(s.Id + "_Scope", EventCounterDefaults.ActiveScopeName(s.CounterName));
            if (s.Quantity is { } q)
            {
                Allocate(s.Id + "_Quantity", q.CounterName);
            }
        }

        void Allocate(string id, string requestedName)
        {
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
            var edits = new List<SourceInsertion>();
            var rows = new List<FunctionAnalysis>();
            var declarations = new List<string>();
            string unit = SourceIndentation.Unit(doc.Text);
            foreach (var s in selected)
            {
                var c = available[s.Id]; string name = assigned[s.Id];
                string insert = newline + c.Indent + "TRACE_COUNTER_INCREMENT(" + name + ");" + newline;
                if (s.Measurement == CounterMeasurement.ActiveScope)
                    insert += c.Indent + "ON_SCOPE_EXIT" + newline + c.Indent + "{" + newline + c.Indent + unit + "TRACE_COUNTER_DECREMENT(" + name + ");" + newline + c.Indent + "};" + newline;
                string display = duplicateDisplays.Contains(s.DisplayName) ? s.DisplayName + " [" + s.Id + "]" : s.DisplayName;
                declarations.Add(unit + "TRACE_DECLARE_ATOMIC_INT_COUNTER(" + name + ", TEXT(\"" + Escape(display) + "\"));");
                if (s.IncludeActiveScope)
                {
                    string scopeName = assigned[s.Id + "_Scope"];
                    string scopeDisplay = s.DisplayName + " ActiveScopes";
                    if (duplicateDisplays.Contains(scopeDisplay)) scopeDisplay += " [" + s.Id + " Scope]";
                    declarations.Add(unit + "TRACE_DECLARE_ATOMIC_INT_COUNTER(" + scopeName + ", TEXT(\"" + Escape(scopeDisplay) + "\"));");
                    insert += c.Indent + "TRACE_COUNTER_INCREMENT(" + scopeName + ");" + newline +
                        c.Indent + "ON_SCOPE_EXIT" + newline + c.Indent + "{" + newline + c.Indent + unit +
                        "TRACE_COUNTER_DECREMENT(" + scopeName + ");" + newline + c.Indent + "};" + newline;
                }
                if (s.Quantity is { } q)
                {
                    string quantityName = assigned[s.Id + "_Quantity"];
                    string quantityDisplay = q.DisplayName + (duplicateDisplays.Contains(q.DisplayName) ? " [" + c.Id + " Quantity]" : "") + " [" + q.Unit + "]";
                    declarations.Add(unit + "TRACE_DECLARE_ATOMIC_INT_COUNTER(" + quantityName + ", TEXT(\"" + Escape(quantityDisplay) + "\"));");
                    insert += c.Indent + "TRACE_COUNTER_ADD(" + quantityName + ", " + q.Value + ");" + newline;
                }
                edits.Add(new(c.Position, insert));
                rows.Add(new(c.Function, c.Line, "Add", c.Reason, name) { EventCounterAdded = true, DecrementAdded = s.Measurement == CounterMeasurement.ActiveScope || s.IncludeActiveScope,
                    ActiveScopeCounter = s.IncludeActiveScope ? assigned[s.Id + "_Scope"] : null,
                    CounterMeasurement = s.Measurement.ToString(), CounterDecision = c.Branch + ": " + c.Condition + (s.Measurement == CounterMeasurement.ActiveScope ? " / 分岐スコープ現在数（退出時に減少）" : " / 累積進入回数") + (s.IncludeActiveScope ? " + 別の分岐スコープ現在数（退出時に減少）" : ""),
                    QuantityCounter = s.Quantity is null ? null : assigned[s.Id + "_Quantity"], QuantityValue = s.Quantity?.Value, QuantityUnit = s.Quantity?.Unit });
            }
            string header = syntax.HasCounterInclude ? "" : "#include \"ProfilingDebugging/CountersTrace.h\"" + newline;
            if (selected.Any(s => s.Measurement == CounterMeasurement.ActiveScope || s.IncludeActiveScope) && !syntax.HasScopeExitInclude) header += "#include \"Misc/ScopeExit.h\"" + newline;
            if (syntax.IncludeAnchor > 0 && doc.Text[syntax.IncludeAnchor - 1] != '\n') header = newline + header;
            edits.Add(new(syntax.IncludeAnchor, header + newline + "namespace {" + newline + string.Join(newline, declarations) + newline + "}" + newline));
            string text = SourceInsertion.Apply(doc.Text, edits);
            return file with { Functions = rows, ProposedText = text, ProposedBytes = doc.Encode(text), Insertions = edits };
        }).ToArray();
        return scan.Snapshot with { Files = files, RequiresQuantityConfirmation = requested.Any(s => s.Quantity is { MeaningConfirmed: false }) };
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
