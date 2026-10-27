using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace UEEtc.Wpf.InputAssistance;

public static class FileDropInput
{
    public static void Attach(TextBox input, string extension, Action<string> report, string? fileName = null)
    {
        input.AllowDrop = true;
        // Preview handlers prevent TextBox's native drop behavior from bypassing validation.
        input.PreviewDragOver += (_, e) =>
        {
            e.Effects = input.IsEnabled && GetFile(e.Data, extension, fileName) is not null && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        input.PreviewDrop += (_, e) =>
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            if (!input.IsEnabled || !e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return;
            if (GetFile(e.Data, extension, fileName) is not { } path)
            {
                report((fileName ?? extension) + "を1ファイルだけドロップしてください。"); return;
            }
            input.Text = path; input.CaretIndex = input.Text.Length;
            e.Effects = DragDropEffects.Copy;
        };
    }

    public static string? GetFile(IDataObject data, string extension, string? fileName = null) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths &&
        File.Exists(paths[0]) && Path.GetExtension(paths[0]).Equals(extension, StringComparison.OrdinalIgnoreCase) &&
        (fileName is null || Path.GetFileName(paths[0]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            ? Path.GetFullPath(paths[0]) : null;
}
