using System.Text.RegularExpressions;
using UEEtc.Core.Source;

namespace UEEtc.Core.Features.EventCounters;

public sealed record QuantityCounterSelection(string CounterName, string DisplayName, string Value, string Unit, bool MeaningConfirmed = false);

internal static class QuantityCounterValues
{
    public static IReadOnlyList<string> Find(CppFunction function, IReadOnlyList<CppToken> tokens, int position)
    {
        // Only value parameters with a known unsigned <=32-bit type. No references, defaults, aliases or calls.
        const string types = @"(?:uint8|uint16|uint32|(?:std\s*::\s*)?uint(?:8|16|32)_t|unsigned\s+(?:int|short|char))";
        int open = function.Signature.IndexOf('('), close = function.Signature.IndexOf(')', open + 1);
        if (open < 0 || close < 0) return [];
        string parameters = function.Signature[(open + 1)..close];
        // Nested declarators can name callback parameters rather than variables in this function.
        if (parameters.Contains('(') || parameters.Contains('<')) return [];
        var names = parameters.Split(',').Select(p => Regex.Match(p, @"^\s*(?:const\s+)?" + types + @"\s+([A-Za-z_]\w*)\s*$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value);
        // Prior uses can include declarations that shadow a parameter; don't pretend to resolve arbitrary C++ scopes.
        return names.Where(name => !tokens.Skip(function.Open + 1).Take(function.Close - function.Open - 1)
            .Any(t => t.Start < position && !t.Literal && t.Text == name)).Distinct(StringComparer.Ordinal).ToArray();
    }
    public static bool IsAllowed(string value, EventCounterCandidate candidate) =>
        candidate.AddValues.Contains(value, StringComparer.Ordinal) ||
        (Regex.IsMatch(value, @"^(0|[1-9][0-9]*)$") && uint.TryParse(value, out _));
}
