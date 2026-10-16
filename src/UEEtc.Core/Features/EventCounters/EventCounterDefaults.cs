namespace UEEtc.Core.Features.EventCounters;

/// <summary>Shared automatic proposals; all generated changes remain previewable before apply.</summary>
public static class EventCounterDefaults
{
    public static EventCounterSelection Create(EventCounterCandidate candidate) => new(candidate.Id,
        candidate.SuggestedName, candidate.DisplayName, candidate.RecommendedQuantity?.CreateInputSelection(candidate),
        IncludeActiveScope: candidate.RecommendedQuantity?.IsDirectBatch == true && candidate.ScopeTrackingExclusion is null);

    public static string ActiveScopeName(string countName) =>
        (countName.EndsWith("_Count", StringComparison.Ordinal) ? countName[..^6] : countName) + "_ActiveScopes";

    public static string Explain(EventCounterCandidate candidate)
    {
        var quantity = candidate.RecommendedQuantity;
        string add = quantity is null ? "ADD: 自動追加なし（数量を安全に特定できない）。" :
            $"ADD: {quantity.Value} [{quantity.Unit}]。{quantity.Reason}";
        string scope = candidate.ScopeTrackingExclusion is { } exclusion ? "DECREMENT: 自動追加なし。" + exclusion :
            quantity?.IsDirectBatch == true ? "DECREMENT: 別の ActiveScopes を分岐退出時に減算し、分岐スコープ現在数を計測。累積回数は保持。" :
            "DECREMENT: 自動追加なし（対応する処理区間を特定できない）。累積進入回数を計測。";
        return add + " " + scope;
    }
}
