using UEEtc.Core.Plans;

namespace UEEtc.Wpf.Workflow;

internal sealed record PlanAvailability(bool CanExport, bool CanApply, string ApplyHint)
{
    public static PlanAvailability For(AnalysisPlan? plan, AnalysisPlan? selected, bool busy)
    {
        if (busy) return new(false, false, "処理の完了を待ってください。");
        if (plan is null) return new(false, false, "解析・プレビューを実行し、入力エラーがあれば修正してください。");
        bool export = selected is { ChangedFiles: > 0 };
        if (plan.DiagnosticCount > 0) return new(export, false, "診断の内容を解消してから再解析してください。");
        if (plan.RequiresQuantityConfirmation) return new(export, false, "詳細設定で数量ADDの意味・単位を確認するか、ADDを無効にしてください。");
        if (!export) return new(false, false, "変更候補のあるファイルを一覧でチェックしてください。");
        return new(true, true, $"選択した {selected!.ChangedFiles} ファイルに、確認後バックアップを作成して適用します。");
    }
}
