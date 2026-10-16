using System.Text.RegularExpressions;

namespace UEEtc.Core.Source;

internal sealed record CppToken(string Text, int Start, int End, bool Literal = false, bool Directive = false);

// A lexical scanner, not a C++ compiler. Positions always refer to the original text.
internal static class CppLexer
{
    private static readonly string[] RawPrefixes = ["R\"", "u8R\"", "uR\"", "UR\"", "LR\""];

    public static List<CppToken> Scan(string text)
    {
        var tokens = new List<CppToken>();
        for (int i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int start = i;
            if (text.AsSpan(i).StartsWith("//"))
            {
                i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '\n' && (i == 0 || text[i - 1] != '\\') &&
                        (i < 2 || text[i - 1] != '\r' || text[i - 2] != '\\')) break;
                    i++;
                }
                continue;
            }
            if (text.AsSpan(i).StartsWith("/*"))
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("閉じていないブロックコメント。");
                i = end + 2;
                continue;
            }
            if (text[i] == '#' && text[(text.LastIndexOf('\n', i) + 1)..i].All(char.IsWhiteSpace))
            {
                do
                {
                    int end = text.IndexOf('\n', i);
                    i = end < 0 ? text.Length : end + 1;
                } while (i < text.Length && text[start..i].TrimEnd('\r', '\n').EndsWith('\\'));
                tokens.Add(new(text[start..i], start, i, Directive: true));
                continue;
            }
            var rawPrefix = RawPrefixes
                .FirstOrDefault(prefix => text.AsSpan(i).StartsWith(prefix));
            if (rawPrefix is not null)
            {
                int delimiterStart = i + rawPrefix.Length;
                int open = text.IndexOf('(', delimiterStart);
                if (open < 0 || open - delimiterStart > 16)
                    throw new InvalidDataException("不正な raw 文字列。");
                string terminator = ")" + text[delimiterStart..open] + "\"";
                int end = text.IndexOf(terminator, open + 1, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("閉じていない raw 文字列。");
                i = end + terminator.Length;
                tokens.Add(new(text[start..i], start, i, Literal: true));
                continue;
            }
            if (text[i] is '\"' or '\'')
            {
                char quote = text[i++];
                bool closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\\') { i = Math.Min(i + 2, text.Length); continue; }
                    if (text[i++] == quote) { closed = true; break; }
                }
                if (!closed) throw new InvalidDataException("閉じていない文字列／文字リテラル。");
                tokens.Add(new(text[start..i], start, i, Literal: true));
                continue;
            }
            if (char.IsDigit(text[i]))
            {
                // C++ digit separators are part of numeric literals, not character quotes.
                while (++i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '\'')) { }
            }
            else if (char.IsLetter(text[i]) || text[i] == '_')
            {
                while (++i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) { }
            }
            else if (i + 1 < text.Length && text.Substring(i, 2) is "::" or "->" or "&&") i += 2;
            else i++;
            tokens.Add(new(text[start..i], start, i));
        }
        ValidateStructure(tokens);
        return tokens;
    }

    public static string DirectiveName(CppToken token) => Regex.Match(token.Text, @"^#\s*(\w+)").Groups[1].Value;

    private static void ValidateStructure(List<CppToken> tokens)
    {
        var delimiters = new Stack<string>();
        var branches = new Stack<string[]>();
        foreach (var token in tokens)
        {
            if (token.Literal) continue;
            if (token.Directive)
            {
                var name = DirectiveName(token);
                if (name is "if" or "ifdef" or "ifndef") branches.Push(delimiters.ToArray());
                else if (name is "else" or "elif" or "endif")
                {
                    if (branches.Count == 0 || !delimiters.SequenceEqual(branches.Peek()))
                        throw new InvalidDataException("条件コンパイルの分岐が括弧構造を変更しています。手動確認が必要です。");
                    if (name == "endif") branches.Pop();
                }
                continue;
            }
            if (token.Text is "{" or "(" or "[") delimiters.Push(token.Text);
            if (token.Text is "}" or ")" or "]")
            {
                string expected = token.Text switch { "}" => "{", ")" => "(", _ => "[" };
                if (!delimiters.TryPop(out var open) || open != expected)
                    throw new InvalidDataException("括弧の対応を確定できません。手動確認が必要です。");
            }
        }
        if (delimiters.Count != 0 || branches.Count != 0)
            throw new InvalidDataException("閉じていない括弧または条件コンパイルがあります。");
    }
}
