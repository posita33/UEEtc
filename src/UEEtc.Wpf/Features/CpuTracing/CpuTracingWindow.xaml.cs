using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Plans;
using UEEtc.Core.Reporting;
using UEEtc.Wpf.InputAssistance;
using UEEtc.Wpf.Preview;
using System.Windows.Data;

namespace UEEtc.Wpf.Features.CpuTracing;

public partial class CpuTracingWindow : Window
{
    private readonly InstrumentationService service = new();
    private AnalysisPlan? plan;
    private List<SelectableFile> files = [];
    public AnalysisPlan? SelectedPlan => plan is null ? null : SelectableFile.Select(plan, files);
    private CancellationTokenSource? cancellation;
    private bool busy;
    public bool IsBusy => busy;

    private readonly RememberedInputs inputs;
    public CpuTracingWindow() : this(InputSettingsStore.Default) { }
    public CpuTracingWindow(InputSettingsStore settings)
    {
        InitializeComponent();
        inputs = new(this, settings, text => StatusText.Text = text);
        inputs.Bind(SourcePath, "CpuTracing.SourcePath");
        inputs.Bind(FilterText, "CpuTracing.Filter");
        FolderDropInput.Attach(SourcePath, text => StatusText.Text = text);
        inputs.ReportLoadError();
    }

    private void InputsChanged(object sender, RoutedEventArgs e)
    {
        if (AnalyzeButton is null || busy) return;
        plan = null;
        files = [];
        ExportButton.IsEnabled = ApplyButton.IsEnabled = false;
        FileList.ItemsSource = null;
        PreviewText.Clear(); OriginalText.Text = DiagnosticsText.Text = "";
        FunctionList.ItemsSource = null;
        StatusText.Text = "設定が変更されました。解析を実行してください。";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "解析するソースフォルダーを選択" };
        if (picker.ShowDialog(this) == true) SourcePath.Text = picker.FolderName;
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        plan = null;
        files = [];
        FileList.ItemsSource = null;
        PreviewText.Clear(); OriginalText.Text = DiagnosticsText.Text = "";
        FunctionList.ItemsSource = null;
        string root = SourcePath.Text.Trim();
        var options = new InstrumentationOptions(true, false, FocusedCpu: AllFunctionsOption.IsChecked != true);
        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource;
        SetBusy(true, canCancel: true);
        try
        {
            var progress = new Progress<string>(path => StatusText.Text = "解析中: " + path);
            plan = await Task.Run(() => service.Analyze(root, options, progress, tokenSource.Token));
            files = plan.Files.Select(f => new SelectableFile(f, SelectionChanged)).ToList();
            FileList.ItemsSource = files;
            FilterChanged(this, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
            DiagnosticsText.Text = string.Join(Environment.NewLine, plan.Diagnostics.Concat(
                plan.Files.SelectMany(f => f.Diagnostics.Select(d => f.RelativePath + ": " + d))));
            if (plan.DiagnosticCount == 0) DiagnosticsText.Text = "診断なし。C++ のコンパイル確認は別途必要です。";
            FileList.SelectedItem = FileList.Items.Cast<SelectableFile>().FirstOrDefault(f => f.File.HasChanges) ?? FileList.Items.Cast<SelectableFile>().FirstOrDefault();
            StatusText.Text = ReportExporter.Summary(plan) + "。適用するファイルをチェックしてください。初期選択0件。";
        }
        catch (OperationCanceledException) { StatusText.Text = "解析を中止しました。"; }
        catch (Exception error) { ShowError(error); }
        finally { cancellation = null; SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (plan is null) return;
        var picker = new OpenFolderDialog { Title = "ソース外の空の出力フォルダーを選択" };
        if (picker.ShowDialog(this) != true) return;
        var current = SelectedPlan!;
        if (current.ChangedFiles == 0) return;
        SetBusy(true);
        try
        {
            await Task.Run(() => ReportExporter.Export(current, picker.FolderName));
            StatusText.Text = "レポート・差分・加工済みコピーを保存: " + picker.FolderName;
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (plan is null || plan.DiagnosticCount > 0 || SelectedPlan is not { ChangedFiles: > 0 } current) return;
        if (MessageBox.Show(this,
            $"{current.RootPath}\n\n{current.ChangedFiles} ファイルにマクロを付与します。\n元ファイルは .ueetc-backups に保存されます。\n\nプレビューした変更を適用しますか？",
            "マクロ付与の適用", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            string backup = await Task.Run(() => service.Apply(current));
            plan = null;
            StatusText.Text = "適用しました。バックアップ: " + backup + "（続ける場合は再解析してください）";
        }
        catch (Exception error)
        {
            plan = null;
            ShowError(error);
        }
        finally { SetBusy(false); }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not SelectableFile row) { PreviewText.Clear(); OriginalText.Text = ""; FunctionList.ItemsSource = null; return; }
        var file = row.File;
        PreviewText.ShowComparison(file.OriginalText, file.ProposedText);
        OriginalText.Text = file.OriginalText;
        FunctionList.ItemsSource = file.Functions;
    }

    private void SetBusy(bool value, bool canCancel = false)
    {
        busy = value;
        SourcePath.IsEnabled = BrowseButton.IsEnabled = AnalyzeButton.IsEnabled = AllFunctionsOption.IsEnabled = !value;
        FileList.IsEnabled = FilterText.IsEnabled = SelectVisibleButton.IsEnabled = ClearVisibleButton.IsEnabled = !value;
        CancelButton.IsEnabled = value && canCancel;
        ExportButton.IsEnabled = !value && SelectedPlan is { ChangedFiles: > 0 };
        ApplyButton.IsEnabled = !value && plan is { DiagnosticCount: 0 } && SelectedPlan is { ChangedFiles: > 0 };
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectionChanged()
    {
        SetBusy(busy);
        StatusText.Text = $"適用選択 {files.Count(f => f.Selected && f.File.HasChanges)} / 変更候補 {files.Count(f => f.File.HasChanges)} ファイル。検索で隠れた選択も対象です。";
    }
    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (FileList?.ItemsSource is null) return;
        string query = FilterText.Text.Trim();
        CollectionViewSource.GetDefaultView(FileList.ItemsSource).Filter = value => value is SelectableFile row &&
            (query.Length == 0 || (row.RelativePath + " " + string.Join(" ", row.File.Functions.Select(f => f.Name + " " + f.CpuDecision))).Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void SelectVisible_Click(object sender, RoutedEventArgs e) { foreach (var file in FileList.Items.Cast<SelectableFile>()) if (file.File.HasChanges) file.Selected = true; }
    private void ClearVisible_Click(object sender, RoutedEventArgs e) { foreach (var file in FileList.Items.Cast<SelectableFile>()) file.Selected = false; }

    private void ShowError(Exception error)
    {
        StatusText.Text = "処理に失敗しました: " + error.Message;
        MessageBox.Show(this, error.Message, "UEEtc", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (busy)
        {
            cancellation?.Cancel();
            e.Cancel = true;
            StatusText.Text = "処理が終了してから閉じてください。";
        }
        base.OnClosing(e);
    }
}
