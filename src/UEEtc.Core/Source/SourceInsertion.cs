using System.Text;

namespace UEEtc.Core.Source;

// Positions refer to the original document. At a shared anchor, the last queued insert goes first.
internal sealed record SourceInsertion(int Position, string Text)
{
    public static string Apply(string original, IEnumerable<SourceInsertion> insertions)
    {
        var output = new StringBuilder(original);
        foreach (var group in insertions.GroupBy(e => e.Position).OrderByDescending(g => g.Key))
            output.Insert(group.Key, string.Concat(group.Reverse().Select(e => e.Text)));
        return output.ToString();
    }
}
