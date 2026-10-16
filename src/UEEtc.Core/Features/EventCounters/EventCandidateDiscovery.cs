using System.Text;
using System.Text.RegularExpressions;
using UEEtc.Core.Plans;
using UEEtc.Core.Reporting;
using UEEtc.Core.Source;

namespace UEEtc.Core.Features.EventCounters;

internal sealed record EventSourceFile(SourceDocument Document, CppStructure Syntax);

internal sealed class EventCandidateDiscovery(List<EventCounterCandidate> candidates,
    Dictionary<string, EventSourceFile> sources, CancellationToken cancellationToken)
{
    public FileAnalysis Read(string relativePath, byte[] bytes)
    {
        try
        {
            var document = SourceDocument.Read(bytes);
            var syntax = new CppStructure(document.Text);
            sources.Add(relativePath, new(document, syntax));
            foreach (var function in syntax.Functions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? scopeTrackingExclusion = HasGoto(function)
                    ? "gotoを含む関数はスコープ内へのジャンプを否定できないため、退出ガードを自動挿入しません。" : null;
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
                        string id = EventCounterIdentity.Hash(identity);
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
                            RecommendedQuantity = QuantityRecommendation.Find(addValues, tokens, open, close, syntax.Pairs),
                            ScopeTrackingExclusion = scopeTrackingExclusion });
                    }
                }
            }
            return new(relativePath, [], []) { OriginalBytes = bytes, ProposedBytes = bytes, OriginalText = document.Text, ProposedText = document.Text };

            bool HasGoto(CppFunction function) => syntax.Tokens.Skip(function.Open + 1).Take(function.Close - function.Open - 1)
                .Any(t => !t.Literal && t.Text == "goto");
        }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
        {
            return new(relativePath, [], [error.Message]) { OriginalBytes = bytes, ProposedBytes = bytes };
        }
    }
}
