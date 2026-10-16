using System.Text;
using System.Text.RegularExpressions;

namespace UEEtc.Core;

internal static class CounterNaming
{
    // Reserving every textual identifier is deliberately conservative, including comments and literals.
    public static IEnumerable<string> Identifiers(string text) =>
        Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]*").Select(m => m.Value);

    public static string BaseName(string namespaceName, string functionName)
    {
        var scopes = namespaceName.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.StartsWith("namespace", StringComparison.Ordinal))
            .Select(s => s.Length == "namespace".Length ? "Anonymous" : s["namespace".Length..]);
        string name = Regex.Replace(string.Join("_", scopes.Append(functionName)), @"[^A-Za-z0-9]+", "_").Trim('_');
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) name = "Function_" + name;
        return name + "_CallCount";
    }

    public static IReadOnlyList<FileAnalysis> Resolve(IReadOnlyList<FileAnalysis> files, IEnumerable<string> reservedIdentifiers,
        CancellationToken cancellationToken = default)
    {
        var reserved = reservedIdentifiers.ToHashSet(StringComparer.Ordinal);
        var candidates = files.SelectMany(f => f.Functions).Where(f => f.Counter is not null && f.CounterIdentity is not null)
            .GroupBy(f => f.CounterIdentity!, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        var assignments = new Dictionary<string, (string Name, bool Collision)>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(f => f.CounterBaseName!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool collision = group.Count() > 1 || IsReserved(group.Key);
            foreach (var candidate in group.OrderBy(f => f.CounterIdentity, StringComparer.Ordinal))
            {
                string desired = collision ? group.Key + "_" + candidate.CounterHash : group.Key;
                string allocated = desired;
                int suffix = 2;
                while (IsReserved(allocated) || !used.Add(allocated)) allocated = desired + "_" + suffix++;
                assignments.Add(candidate.CounterIdentity!, (allocated, collision));
            }
        }
        return files.Select(Rewrite).ToArray();

        bool IsReserved(string name) => reserved.Contains(name) || reserved.Contains("__GTraceCounter" + name);

        FileAnalysis Rewrite(FileAnalysis file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapping = file.Functions.Where(f => f.Counter is not null && f.CounterIdentity is not null)
                .GroupBy(f => f.Counter!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => assignments[g.First().CounterIdentity!].Name, StringComparer.Ordinal);
            if (mapping.Count == 0) return file;
            // Change generated identifier tokens only; existing comments and trace labels stay byte-for-byte intact.
            var result = new StringBuilder(file.ProposedText);
            foreach (var token in CppLexer.Scan(file.ProposedText).AsEnumerable().Reverse())
            {
                if (!token.Literal && !token.Directive && mapping.TryGetValue(token.Text, out string? replacement))
                    result.Remove(token.Start, token.End - token.Start).Insert(token.Start, replacement);
            }
            string text = result.ToString();
            return file with
            {
                ProposedText = text, ProposedBytes = SourceDocument.Read(file.OriginalBytes).Encode(text),
                Functions = file.Functions.Select(f => f.Counter is null ? f : f with
                {
                    Counter = assignments[f.CounterIdentity!].Name,
                    Reason = f.Reason + (assignments[f.CounterIdentity!].Collision ? "（カウンター名の衝突を避けるためハッシュを付加）" : "")
                }).ToArray()
            };
        }
    }
}
