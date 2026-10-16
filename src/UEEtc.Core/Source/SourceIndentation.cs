namespace UEEtc.Core.Source;

// Preserve local statement indentation; use the file's prevailing unit only for new blocks.
internal static class SourceIndentation
{
    public static string Unit(string text)
    {
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToArray();
        var blockUnits = new List<string>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (!lines[i - 1].TrimEnd().EndsWith('{')) continue;
            string outer = new(lines[i - 1].TakeWhile(c => c is ' ' or '\t').ToArray());
            string inner = new(lines[i].TakeWhile(c => c is ' ' or '\t').ToArray());
            if (inner.Length > outer.Length && inner.StartsWith(outer, StringComparison.Ordinal)) blockUnits.Add(inner[outer.Length..]);
        }
        if (blockUnits.Count > 0) return blockUnits.GroupBy(u => u).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Length).First().Key;
        var prefixes = lines
            .Select(l => new string(l.TakeWhile(c => c is ' ' or '\t').ToArray())).Where(p => p.Length > 0).ToArray();
        int tabs = prefixes.Count(p => p.Contains('\t'));
        if (tabs > 0 && tabs >= prefixes.Length - tabs) return "\t";
        int width = prefixes.Where(p => !p.Contains('\t')).Select(p => p.Length).DefaultIfEmpty(0).Aggregate(Gcd);
        return width > 0 ? new string(' ', width) : "\t";
    }

    public static string Block(string text, IReadOnlyList<CppToken> tokens, int open, int close)
    {
        string parent = Prefix(text, tokens[open].Start);
        int depth = 0;
        for (int i = open + 1; i < close; i++)
        {
            var token = tokens[i];
            if (token.Literal) continue;
            string indent = Prefix(text, token.Start);
            int lineStart = text.LastIndexOf('\n', Math.Max(0, token.Start - 1)) + 1;
            if (depth == 0 && token.Start == lineStart + indent.Length && indent.StartsWith(parent, StringComparison.Ordinal) && indent.Length > parent.Length)
                return indent;
            if (token.Text is "{" or "(" or "[") depth++;
            else if (token.Text is "}" or ")" or "]") depth--;
        }
        return parent + Unit(text);
    }

    private static string Prefix(string text, int position)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1, end = start;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return text[start..end];
    }
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
