using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using UEEtc.Blueprint;
using UEEtc.Wpf.InputAssistance;

namespace UEEtc.Wpf.Features.BlueprintAnalysis;

public partial class BlueprintAnalysisWindow : Window
{
    private readonly IBlueprintAnalysisService service;
    private CancellationTokenSource? cancellation;
    public bool IsBusy { get; private set; }
    public BlueprintAnalysisResult? Result { get; private set; }
    public BlueprintAnalysisWindow(InputSettingsStore settings, IBlueprintAnalysisService? service = null)
    {
        this.service = service ?? new BlueprintAnalysisService(); InitializeComponent();
        var inputs = new RememberedInputs(this, settings, text => StatusText.Text = text);
        EditorPath.Text = BlueprintPaths.FindEditor(); OutputPath.Text = BlueprintPaths.DefaultOutputRoot;
        foreach (var input in new[] { AssetPath, ProjectPath, EditorPath, OutputPath }) inputs.Bind(input, "Blueprint." + input.Name);
        AssetPath.TextChanged += (_, _) =>
        {
            if (IsBusy) return;
            if (BlueprintPaths.FindProject(AssetPath.Text.Trim()) is { } project)
            {
                ProjectPath.Text = project;
                string editor = BlueprintPaths.FindEditor(project);
                if (!File.Exists(EditorPath.Text) && File.Exists(editor)) EditorPath.Text = editor;
            }
            Invalidate();
        };
        foreach (var input in new[] { ProjectPath, EditorPath, OutputPath }) input.TextChanged += (_, _) => Invalidate();
        RemoveLayoutOption.Click += (_, _) => Invalidate();
        AssetPath.AllowDrop = true;
        AssetPath.PreviewDragOver += (_, e) =>
        {
            e.Effects = !IsBusy && DropAsset(e.Data) is not null && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        AssetPath.PreviewDrop += (_, e) =>
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            if (IsBusy || !e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return;
            if (DropAsset(e.Data) is not { } path) { StatusText.Text = ".uassetを1ファイルだけドロップしてください。"; return; }
            AssetPath.Text = path; e.Effects = DragDropEffects.Copy;
        };
        inputs.ReportLoadError();
    }

    private void Invalidate()
    {
        if (IsBusy) return;
        Result = null; OpenOutputButton.IsEnabled = false; ReportText.Clear(); DiagnosticsText.Clear();
        StatusText.Text = "設定を確認して解析してください。";
    }
    public static string? DropAsset(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) &&
        data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && File.Exists(paths[0]) &&
        Path.GetExtension(paths[0]).Equals(".uasset", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(paths[0]) : null;

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        Invalidate();
        var options = new BlueprintAnalysisOptions(AssetPath.Text.Trim(), ProjectPath.Text.Trim(), EditorPath.Text.Trim(), OutputPath.Text.Trim(), RemoveLayoutOption.IsChecked == true);
        using var cts = new CancellationTokenSource(); cancellation = cts; SetBusy(true);
        try
        {
            var progress = new Progress<string>(text => StatusText.Text = text);
            var result = await Task.Run(() => service.AnalyzeAsync(options, progress, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ReportText.Text = await File.ReadAllTextAsync(result.MarkdownPath, cts.Token);
            Result = result;
            DiagnosticsText.Text = string.Join(Environment.NewLine, result.Report.Warnings.Concat(result.Report.Documents.SelectMany(d => d.Warnings)));
            StatusText.Text = "資料を作成しました: " + result.Directory;
        }
        catch (OperationCanceledException) { StatusText.Text = "解析を中止しました。"; }
        catch (Exception error) { DiagnosticsText.Text = error.Message; StatusText.Text = "解析できませんでした。診断タブを確認してください。"; }
        finally { cancellation = null; SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        IsBusy = value;
        foreach (var control in new UIElement[] { AssetPath, ProjectPath, EditorPath, OutputPath, RemoveLayoutOption,
            BrowseAssetButton, BrowseProjectButton, BrowseEditorButton, BrowseOutputButton, AnalyzeButton }) control.IsEnabled = !value;
        CancelButton.IsEnabled = value; OpenOutputButton.IsEnabled = !value && Result is not null;
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BrowseAsset_Click(object sender, RoutedEventArgs e) => Pick("Blueprint .uasset|*.uasset", AssetPath);
    private void BrowseProject_Click(object sender, RoutedEventArgs e) => Pick("UE Project|*.uproject", ProjectPath);
    private void BrowseEditor_Click(object sender, RoutedEventArgs e) => Pick("UE Editor|UnrealEditor-Cmd.exe", EditorPath);
    private void Pick(string filter, System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }
    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "UEプロジェクト外の資料出力先を選択" };
        if (dialog.ShowDialog(this) == true) OutputPath.Text = dialog.FolderName;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (Result is not { } result) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { result.Directory }, UseShellExecute = false }); }
        catch (Exception error) { DiagnosticsText.Text = error.Message; }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsBusy) { cancellation?.Cancel(); e.Cancel = true; StatusText.Text = "解析の終了後に閉じてください。"; }
        base.OnClosing(e);
    }
}
