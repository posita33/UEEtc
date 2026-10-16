using UEEtc.Core.Plans;

namespace UEEtc.Wpf.Workflow;

internal sealed record PlanAvailability(bool CanExport, bool CanApply, string ApplyHint)
{
    public static PlanAvailability For(AnalysisPlan? plan, AnalysisPlan? selected, bool busy)
    {
        if (busy) return new(false, false, "処理の完了を待ってください。");
        if (plan is null) return new(false, false, "解析・プレビューを実行し、入力エラーがあれば修正してください。");
        bool export = selected is { ChangedFiles: > 0 };
        // Apply receives the selected plan. Diagnostics belonging only to excluded files
        // must not block it; workspace-wide diagnostics remain on the selected plan.
        if (selected is { DiagnosticCount: > 0 })
        {
            string detail = selected.Diagnostics.FirstOrDefault() ?? selected.Files
                .Where(f => f.Diagnostics.Count > 0).Select(f => f.RelativePath + ": " + f.Diagnostics[0]).First();
            return new(export, false, "適用不可: 選択対象またはフォルダー全体に診断があります。診断タブで確認し、対象を外すか解消後に再解析してください。 " + detail);
        }
        if (plan.RequiresQuantityConfirmation) return new(export, false, "詳細設定で数量ADDの意味・単位を確認するか、ADDを無効にしてください。");
        if (!export) return new(false, false, "変更候補のあるファイルを一覧でチェックしてください。");
        int excludedDiagnostics = plan.DiagnosticCount - selected!.DiagnosticCount;
        string note = excludedDiagnostics > 0 ? $" 対象外ファイルの診断 {excludedDiagnostics} 件は診断タブに表示しています。" : "";
        return new(true, true, $"適用可能: 選択した {selected.ChangedFiles} ファイルに、確認後バックアップを作成して適用します。" + note);
    }
}
