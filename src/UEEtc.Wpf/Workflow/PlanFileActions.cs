using System.Windows;
using Microsoft.Win32;
using UEEtc.Core.Plans;
using UEEtc.Core.Reporting;
using UEEtc.Core.Source;

namespace UEEtc.Wpf.Workflow;

// UI owns dialogs and operation state; Core owns export, stale checks, backup and rollback.
internal sealed class PlanFileActions(Window owner, Action<bool> setBusy, Action<string> status, Action<Exception> error)
{
    public async Task ExportAsync(AnalysisPlan? selected)
    {
        if (selected is not { ChangedFiles: > 0 }) return;
        var picker = new OpenFolderDialog { Title = "ソース外の空の出力フォルダーを選択" };
        if (picker.ShowDialog(owner) != true) return;
        setBusy(true);
        try
        {
            await Task.Run(() => ReportExporter.Export(selected, picker.FolderName));
            status("レポート・差分・加工済みコピーを保存: " + picker.FolderName);
        }
        catch (Exception exception) { error(exception); }
        finally { setBusy(false); }
    }

    public async Task ApplyAsync(AnalysisPlan? plan, AnalysisPlan? selected, string backupRoot, string description, Action<bool> invalidate)
    {
        if (!PlanAvailability.For(plan, selected, false).WithBackupRoot(selected, backupRoot).CanApply) return;
        if (MessageBox.Show(owner,
            $"{selected!.RootPath}\n\n{selected.ChangedFiles} ファイルに{description}を適用します。\nバックアップ先: {backupRoot}\n（適用ごとに日時とIDのサブフォルダーを作成）\n\nプレビューした変更を適用しますか？",
            "マクロ付与の適用", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool applied = false;
        setBusy(true);
        try
        {
            string backup = await Task.Run(() => new SourceWorkspace().Apply(selected, backupRoot));
            applied = true;
            status("適用しました。再解析してください。バックアップ: " + backup);
        }
        catch (Exception exception) { error(exception); }
        finally { invalidate(applied); setBusy(false); }
    }
}
