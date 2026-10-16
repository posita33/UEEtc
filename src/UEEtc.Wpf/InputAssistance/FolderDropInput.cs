using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace UEEtc.Wpf.InputAssistance;

public static class FolderDropInput
{
    public static void Attach(TextBox input, Action<string> report)
    {
        input.AllowDrop = true;
        // Preview handlers run before TextBox's built-in text/file drop handling.
        input.PreviewDragOver += (_, e) =>
        {
            e.Effects = input.IsEnabled && GetDirectory(e.Data) is not null && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        input.PreviewDrop += (_, e) =>
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            if (!input.IsEnabled || !e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return;
            if (GetDirectory(e.Data) is not { } directory) { report("フォルダーを 1 つだけドロップしてください。ファイルは使用できません。"); return; }
            input.Text = directory;
            input.CaretIndex = input.Text.Length;
            e.Effects = DragDropEffects.Copy;
        };
    }
    private static string? GetDirectory(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return null;
        if (!Directory.Exists(paths[0])) return null;
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(paths[0]));
    }
}
