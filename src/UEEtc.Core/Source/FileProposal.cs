using UEEtc.Core.Plans;

namespace UEEtc.Core.Source;

// A feature proposes insertions; materialization happens once at the public plan boundary.
internal sealed record FileProposal(FileAnalysis Source, SourceDocument? Document, IReadOnlyList<SourceInsertion> Insertions)
{
    public FileAnalysis Render()
    {
        if (Insertions.Count == 0) return Source;
        var document = Document ?? throw new InvalidOperationException("挿入に必要な元ソースがありません。");
        string text = SourceInsertion.Apply(document.Text, Insertions);
        return Source with { ProposedText = text, ProposedBytes = document.Encode(text) };
    }

    public static FileProposal Merge(FileProposal first, FileProposal second)
    {
        if (first.Source.RelativePath != second.Source.RelativePath ||
            !first.Source.OriginalBytes.AsSpan().SequenceEqual(second.Source.OriginalBytes))
            throw new InvalidOperationException("異なる元ソースの変更案は統合できません。");
        var source = second.Source with
        {
            Functions = first.Source.Functions.Concat(second.Source.Functions).ToArray(),
            Diagnostics = first.Source.Diagnostics.Concat(second.Source.Diagnostics).Distinct(StringComparer.Ordinal).ToArray()
        };
        // SourceInsertion gives later inserts precedence at the same anchor (CPU includes first).
        return new(source, first.Document ?? second.Document, second.Insertions.Concat(first.Insertions).ToArray());
    }
}
