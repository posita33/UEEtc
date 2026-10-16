using System.Text.RegularExpressions;

namespace UEEtc.Core.Features.EventCounters;

public sealed record QuantityRecommendation(string Value, string Unit, string Reason)
{
    internal static QuantityRecommendation? Find(IReadOnlyList<string> available)
    {
        var candidates = available.Select(name =>
        {
            string normalized = name.Replace("_", "", StringComparison.Ordinal);
            string? unit = Regex.IsMatch(normalized, @"^(?:In)?(?:NumBytes|ByteCount|Bytes|BytesWritten|BytesRead|WrittenBytes|ReadBytes)$", RegexOptions.IgnoreCase) ? "bytes" :
                Regex.IsMatch(normalized, @"^(?:In)?(?:NumItems|ItemCount|NumElements|ElementCount)$", RegexOptions.IgnoreCase) ? "items" : null;
            return unit is null ? null : new QuantityRecommendation(name, unit,
                "値引数の型と名前からの推奨。実処理量・容量・要求量のどれかは未確認。ソースで意味を確認してください。");
        }).Where(c => c is not null).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
