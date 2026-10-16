using System.Text.RegularExpressions;
using UEEtc.Core.Source;

namespace UEEtc.Core.Features.EventCounters;

public sealed record QuantityRecommendation(string Value, string Unit, string Reason)
{
    public QuantityCounterSelection CreateInputSelection(EventCounterCandidate candidate)
    {
        string suffix = Unit == "bytes" ? "InputBytes" : "InputItems";
        string stem = candidate.SuggestedName.EndsWith("_Count", StringComparison.Ordinal) ? candidate.SuggestedName[..^6] : candidate.SuggestedName;
        return new(stem + "_" + suffix, candidate.DisplayName + " " + suffix, Value, Unit, true);
    }
    internal static QuantityRecommendation? Find(IReadOnlyList<string> available, IReadOnlyList<CppToken> tokens, int open, int close, IReadOnlyDictionary<int, int> pairs)
    {
        var candidates = available.Select(name =>
        {
            if (name.EndsWith(".Num()", StringComparison.Ordinal)) return HasDirectBatch(name[..^6]) ? new QuantityRecommendation(name, "items", "この分岐の直接のループが入力配列を処理する。Engine のバッチ計測と同じ Num() で入力要素数を累積する（成功件数ではない）。") : null;
            string normalized = name.Replace("_", "", StringComparison.Ordinal);
            string? unit = Regex.IsMatch(normalized, @"^(?:In)?(?:NumBytes|ByteCount|Bytes|BytesWritten|BytesRead|WrittenBytes|ReadBytes)$", RegexOptions.IgnoreCase) ? "bytes" :
                Regex.IsMatch(normalized, @"^(?:In)?(?:NumItems|ItemCount|NumElements|ElementCount)$", RegexOptions.IgnoreCase) ? "items" : null;
            return unit is null ? null : new QuantityRecommendation(name, unit,
                "型と引数名から選んだ入力数量。分岐入口の引数値を累積する（実処理量・成功量とは区別）。");
        }).Where(c => c is not null).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;

        bool HasDirectBatch(string parameter)
        {
            for (int i = open + 1; i < close; i++)
            {
                if (tokens[i].Literal) continue;
                if (tokens[i].Text == "for" && i + 1 < close && tokens[i + 1].Text == "(")
                {
                    int end = pairs[i + 1];
                    for (int j = i + 2; j < end; j++)
                        if (tokens[j].Text == parameter && (tokens[j - 1].Text == ":" ||
                            (j + 4 < end && tokens[j + 1].Text == "." && tokens[j + 2].Text == "Num" && tokens[j + 3].Text == "(" && tokens[j + 4].Text == ")"))) return true;
                    i = end;
                }
                else if (tokens[i].Text is "{" or "(" or "[") i = pairs[i];
            }
            return false;
        }
    }
}
