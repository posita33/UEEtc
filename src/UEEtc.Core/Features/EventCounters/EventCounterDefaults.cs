namespace UEEtc.Core.Features.EventCounters;

/// <summary>Shared automatic proposals; all generated changes remain previewable before apply.</summary>
public static class EventCounterDefaults
{
    public static EventCounterSelection Create(EventCounterCandidate candidate) => new(candidate.Id,
        candidate.SuggestedName, candidate.DisplayName, candidate.RecommendedQuantity?.CreateInputSelection(candidate),
        IncludeActiveScope: candidate.RecommendedQuantity?.IsDirectBatch == true);

    public static string ActiveScopeName(string countName) =>
        (countName.EndsWith("_Count", StringComparison.Ordinal) ? countName[..^6] : countName) + "_ActiveScopes";

    public static string Explain(EventCounterCandidate candidate) =>
        candidate.RecommendedQuantity is { } q
            ? "ADD: " + q.Value + " [" + q.Unit + "]。" + q.Reason +
              (q.IsDirectBatch ? " DECREMENT: 別の ActiveScopes を分岐退出時に減算し、分岐スコープ現在数を計測。累積回数は保持。" : " DECREMENT: 自動追加なし（対応する処理区間を特定できない）。")
            : "ADD / DECREMENT: 自動追加なし（数量・対応区間を安全に特定できない）。累積進入回数を計測。";
}
