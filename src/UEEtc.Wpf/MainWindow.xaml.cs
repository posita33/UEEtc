using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UEEtc.Core;

namespace UEEtc.Wpf;

public partial class MainWindow : Window
{
    private readonly InstrumentationService service = new();
    private AnalysisPlan? plan;
    private CancellationTokenSource? cancellation;
    private bool busy;

    public MainWindow() => InitializeComponent();

    private void InputsChanged(object sender, RoutedEventArgs e)
    {
        if (AnalyzeButton is null || busy) return;
        plan = null;
        ExportButton.IsEnabled = ApplyButton.IsEnabled = false;
        FileList.ItemsSource = null;
        PreviewText.Text = OriginalText.Text = DiagnosticsText.Text = "";
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
        FileList.ItemsSource = null;
        PreviewText.Text = OriginalText.Text = DiagnosticsText.Text = "";
        FunctionList.ItemsSource = null;
        string root = SourcePath.Text.Trim();
        var options = new InstrumentationOptions(CpuOption.IsChecked == true, CounterOption.IsChecked == true);
        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource;
        SetBusy(true, canCancel: true);
        try
        {
            var progress = new Progress<string>(path => StatusText.Text = "解析中: " + path);
            plan = await Task.Run(() => service.Analyze(root, options, progress, tokenSource.Token));
            FileList.ItemsSource = plan.Files;
            DiagnosticsText.Text = string.Join(Environment.NewLine, plan.Diagnostics.Concat(
                plan.Files.SelectMany(f => f.Diagnostics.Select(d => f.RelativePath + ": " + d))));
            if (plan.DiagnosticCount == 0) DiagnosticsText.Text = "診断なし。C++ のコンパイル確認は別途必要です。";
            FileList.SelectedItem = plan.Files.FirstOrDefault(f => f.HasChanges) ?? plan.Files.FirstOrDefault();
            StatusText.Text = ReportExporter.Summary(plan) + "（元ファイルの変更なし）";
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
        var current = plan;
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
        if (plan is null || plan.ChangedFiles == 0 || plan.DiagnosticCount > 0) return;
        var current = plan;
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
        if (FileList.SelectedItem is not FileAnalysis file) return;
        PreviewText.Text = file.ProposedText;
        OriginalText.Text = file.OriginalText;
        FunctionList.ItemsSource = file.Functions;
    }

    private void SetBusy(bool value, bool canCancel = false)
    {
        busy = value;
        SourcePath.IsEnabled = BrowseButton.IsEnabled = CpuOption.IsEnabled = CounterOption.IsEnabled = AnalyzeButton.IsEnabled = !value;
        CancelButton.IsEnabled = value && canCancel;
        ExportButton.IsEnabled = !value && plan is not null;
        ApplyButton.IsEnabled = !value && plan is { ChangedFiles: > 0, DiagnosticCount: 0 };
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

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
