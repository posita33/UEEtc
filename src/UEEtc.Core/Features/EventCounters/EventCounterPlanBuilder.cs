using UEEtc.Core.Plans;
using UEEtc.Core.Source;

namespace UEEtc.Core.Features.EventCounters;

internal static class EventCounterPlanBuilder
{
    public static IReadOnlyList<FileProposal> Propose(EventCounterScan scan, EventCounterSelection[] requested,
        CancellationToken cancellationToken)
    {
        var available = scan.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var selectedByPath = requested.ToLookup(s => available[s.Id].RelativePath, StringComparer.Ordinal);
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
            var selected = selectedByPath[file.RelativePath].ToArray();
            if (selected.Length == 0) return new FileProposal(file, scan.Sources.GetValueOrDefault(file.RelativePath)?.Document, []);
            var doc = scan.Sources[file.RelativePath].Document;
            var syntax = scan.Sources[file.RelativePath].Syntax;
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
                    insert += ExitGuard(name, c.Indent, unit, newline);
                string display = duplicateDisplays.Contains(s.DisplayName) ? s.DisplayName + " [" + s.Id + "]" : s.DisplayName;
                declarations.Add(unit + "TRACE_DECLARE_ATOMIC_INT_COUNTER(" + name + ", TEXT(\"" + Escape(display) + "\"));");
                if (s.IncludeActiveScope)
                {
                    string scopeName = assigned[s.Id + "_Scope"];
                    string scopeDisplay = s.DisplayName + " ActiveScopes";
                    if (duplicateDisplays.Contains(scopeDisplay)) scopeDisplay += " [" + s.Id + " Scope]";
                    declarations.Add(unit + "TRACE_DECLARE_ATOMIC_INT_COUNTER(" + scopeName + ", TEXT(\"" + Escape(scopeDisplay) + "\"));");
                    insert += c.Indent + "TRACE_COUNTER_INCREMENT(" + scopeName + ");" + newline + ExitGuard(scopeName, c.Indent, unit, newline);
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
                    CounterMeasurement = s.Measurement.ToString(), CounterDecision = c.Branch + ": " + c.Condition + (s.Measurement == CounterMeasurement.ActiveScope ? " / 分岐スコープ現在数（退出時に減少）" : " / 累積進入回数") + (s.IncludeActiveScope ? " + 別の分岐スコープ現在数（退出時に減少）" : "") +
                        (c.ScopeTrackingExclusion is { } exclusion ? " / " + exclusion : ""),
                    QuantityCounter = s.Quantity is null ? null : assigned[s.Id + "_Quantity"], QuantityValue = s.Quantity?.Value, QuantityUnit = s.Quantity?.Unit });
            }
            string header = syntax.HasCounterInclude ? "" : "#include \"ProfilingDebugging/CountersTrace.h\"" + newline;
            if (selected.Any(s => s.Measurement == CounterMeasurement.ActiveScope || s.IncludeActiveScope) && !syntax.HasScopeExitInclude) header += "#include \"Misc/ScopeExit.h\"" + newline;
            if (syntax.IncludeAnchor > 0 && doc.Text[syntax.IncludeAnchor - 1] != '\n') header = newline + header;
            edits.Add(new(syntax.IncludeAnchor, header + newline + "namespace {" + newline + string.Join(newline, declarations) + newline + "}" + newline));
            return new FileProposal(file with { Functions = rows }, doc, edits);
        }).ToArray();
        return files;
    }
    private static string ExitGuard(string name, string indent, string unit, string newline) =>
        indent + "ON_SCOPE_EXIT" + newline + indent + "{" + newline + indent + unit +
        "TRACE_COUNTER_DECREMENT(" + name + ");" + newline + indent + "};" + newline;

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
