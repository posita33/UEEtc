namespace UEEtc.Core.Source;

internal sealed record CppFunction(string Name, string Signature, int Open, int Close);

// Conservative out-of-class function discovery. Does not expand C++ macros or templates.
internal sealed class CppStructure
{
    public List<CppToken> Tokens { get; }
    public Dictionary<int, int> Pairs { get; } = [];
    public List<CppFunction> Functions { get; } = [];
    public int IncludeAnchor { get; }
    public bool HasCounterInclude { get; }
    public bool HasScopeExitInclude { get; }

    public CppStructure(string text)
    {
        var scanned = CppLexer.Scan(text);
        Tokens = scanned.Where(t => !t.Directive).ToList();
        int depth = 0;
        int firstCode = Tokens.Count > 0 ? Tokens[0].Start : text.Length;
        foreach (var t in scanned.Where(t => t.Directive))
        {
            string name = CppLexer.DirectiveName(t);
            if (name is "if" or "ifdef" or "ifndef") depth++;
            else if (name == "endif") depth--;
            else if (name == "include" && depth == 0 && t.End <= firstCode)
            {
                IncludeAnchor = t.End;
                HasCounterInclude |= System.Text.RegularExpressions.Regex.IsMatch(t.Text, "^#\\s*include\\s*[\"<]ProfilingDebugging/CountersTrace\\.h[\">]");
                HasScopeExitInclude |= System.Text.RegularExpressions.Regex.IsMatch(t.Text, "^#\\s*include\\s*[\"<]Misc/ScopeExit\\.h[\">]");
            }
        }
        var stack = new Stack<int>();
        for (int i = 0; i < Tokens.Count; i++)
        {
            if (Tokens[i].Literal) continue;
            if (Tokens[i].Text is "{" or "(" or "[") stack.Push(i);
            else if (Tokens[i].Text is "}" or ")" or "]") Pairs[stack.Pop()] = i;
        }
        Visit(0, Tokens.Count, "");

        void Visit(int start, int end, string scope)
        {
            int segment = start;
            for (int i = start; i < end; i++)
            {
                var t = Tokens[i];
                if (t.Literal) continue;
                if (t.Text == ";") { segment = i + 1; continue; }
                if (t.Text != "{") continue;
                int close = Pairs[i];
                var header = Tokens.GetRange(segment, i - segment);
                if (header.Count > 0 && header[0].Text == "namespace")
                    Visit(i + 1, close, scope + string.Concat(header.Select(x => x.Text)) + "/");
                else
                {
                    int paren = header.FindIndex(x => !x.Literal && x.Text == "(");
                    if (paren > 0 && IsIdentifier(header[paren - 1].Text))
                    {
                        int nameStart = paren - 1;
                        while (nameStart >= 2 && header[nameStart - 1].Text == "::" && IsIdentifier(header[nameStart - 2].Text)) nameStart -= 2;
                        string name = string.Concat(header.Skip(nameStart).Take(paren - nameStart).Select(x => x.Text));
                        bool special = header.Any(x => x.Text is "constexpr" or "consteval" or "template" or "operator" or "requires" or "try" or "~");
                        special |= nameStart == 0 || header.Take(nameStart).Any(x => x.Text is "=" or "[" or "]" or ":");
                        special |= header.Skip(paren).Any(x => x.Text is "->" or ":");
                        special |= name.Contains("::") && name.Split("::")[^1] == name.Split("::")[^2];
                        special |= Tokens.Skip(i + 1).Take(close - i - 1).Any(x => x.Text is "co_await" or "co_yield" or "co_return");
                        special |= Enumerable.Range(i + 1, Math.Max(0, close - i - 2)).Any(j => Tokens[j].Text == "if" && Tokens[j + 1].Text is "constexpr" or "consteval");
                        special |= scanned.Any(x => x.Directive && x.Start > t.Start && x.Start < Tokens[close].End);
                        // A declaration in a conditionally compiled function cannot be safely inferred here.
                        int conditionalDepth = 0;
                        foreach (var directive in scanned.Where(x => x.Directive && x.Start < header[0].Start))
                        {
                            string d = CppLexer.DirectiveName(directive);
                            if (d is "if" or "ifdef" or "ifndef") conditionalDepth++;
                            else if (d == "endif") conditionalDepth--;
                        }
                        if (!special && conditionalDepth == 0 && IncludeAnchor > 0 && IncludeAnchor <= header[0].Start)
                            Functions.Add(new(scope + name, scope + string.Join(" ", header.Select(x => x.Text)), i, close));
                    }
                }
                i = close;
                segment = i + 1;
            }
        }
    }

    private static bool IsIdentifier(string text) => text.Length > 0 &&
        (char.IsAsciiLetter(text[0]) || text[0] == '_') && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
