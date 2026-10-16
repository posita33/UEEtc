using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Plans;
using UEEtc.Core.Reporting;
using UEEtc.Core.Source;
using UEEtc.Wpf.InputAssistance;
using System.Security.Cryptography;
using System.Text;

namespace UEEtc.Wpf.Features.EventCounters;

public sealed class CandidateRow(EventCounterCandidate candidate, Action changed, Action<CandidateRow>? edited = null,
    string? savedName = null, string? savedDisplay = null) : INotifyPropertyChanged
{
    public EventCounterCandidate Candidate { get; } = candidate;
    private bool selected;
    private string counterName = savedName ?? candidate.SuggestedName;
    private string displayName = savedDisplay ?? candidate.DisplayName;
    public bool Selected { get => selected; set { selected = value; Changed(nameof(Selected)); } }
    public string CounterName { get => counterName; set { counterName = value; Changed(nameof(CounterName)); } }
    public string DisplayName { get => displayName; set { displayName = value; Changed(nameof(DisplayName)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) { PropertyChanged?.Invoke(this, new(name)); changed(); if (name != nameof(Selected)) edited?.Invoke(this); }
    public EventCounterSelection Selection() => new(Candidate.Id, CounterName, DisplayName);
}

public partial class EventCountersWindow : Window
{
    private readonly EventCounterService service = new();
    private EventCounterScan? scan;
    private AnalysisPlan? plan;
    private List<CandidateRow> rows = [];
    private CancellationTokenSource? cancellation;
    private bool busy;
    public bool IsBusy => busy;
    private readonly RememberedInputs inputs;
    public EventCountersWindow() : this(InputSettingsStore.Default) { }
    public EventCountersWindow(InputSettingsStore settings)
    {
        InitializeComponent();
        inputs = new(this, settings, text => StatusText.Text = text);
        inputs.Bind(SourcePath, "EventCounters.SourcePath");
        inputs.Bind(FilterText, "EventCounters.Filter");
        FolderDropInput.Attach(SourcePath, text => StatusText.Text = text);
        inputs.ReportLoadError();
    }

    private void InvalidatePlan()
    {
        plan = null;
        if (ExportButton is null) return;
        ExportButton.IsEnabled = ApplyButton.IsEnabled = false;
        PreviewText.Text = "";
        StatusText.Text = $"選択 {rows.Count(r => r.Selected)} / 候補 {rows.Count} 件。選択・名前を確認してプレビューしてください。絞り込みで隠れた選択も対象です。";
    }
    private void SourceChanged(object sender, TextChangedEventArgs e)
    {
        if (AnalyzeButton is null || busy) return;
        scan = null; rows = []; CandidateList.ItemsSource = null;
        ContextText.Text = DiagnosticsText.Text = "";
        PreviewButton.IsEnabled = CandidateList.IsEnabled = false;
        InvalidatePlan();
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "イベントカウンターを検討するソースフォルダー" };
        if (picker.ShowDialog(this) == true) SourcePath.Text = picker.FolderName;
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        scan = null; rows = []; CandidateList.ItemsSource = null; InvalidatePlan();
        ContextText.Text = DiagnosticsText.Text = "";
        string root = SourcePath.Text.Trim();
        using var cts = new CancellationTokenSource(); cancellation = cts;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(path => StatusText.Text = "候補解析中: " + path);
            scan = await Task.Run(() => service.Analyze(root, progress, cts.Token));
            string scope = "EventCounters.Edits." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scan.Snapshot.RootPath.ToUpperInvariant()))) + ".";
            rows = scan.Candidates.OrderBy(c => c.Hint == "条件分岐").Select(c => new CandidateRow(c, InvalidatePlan,
                row => { inputs.Set(scope + c.Id + ".Name", row.CounterName); inputs.Set(scope + c.Id + ".Display", row.DisplayName); },
                inputs.Get(scope + c.Id + ".Name"), inputs.Get(scope + c.Id + ".Display"))).ToList();
            CandidateList.ItemsSource = rows;
            FilterChanged(this, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
            DiagnosticsText.Text = string.Join(Environment.NewLine, scan.Snapshot.Diagnostics.Concat(scan.Snapshot.Files.SelectMany(f => f.Diagnostics.Select(d => f.RelativePath + ": " + d))));
            StatusText.Text = $"{scan.Snapshot.Files.Count} ファイル / 候補 {rows.Count} 件 / 診断 {scan.Snapshot.DiagnosticCount} 件。元ソースの変更なし。選択は 0 件です。";
            CandidateList.SelectedItem = rows.FirstOrDefault();
        }
        catch (OperationCanceledException) { StatusText.Text = "解析を中止しました。"; }
        catch (Exception error) { ShowError(error); }
        finally { cancellation = null; SetBusy(false); }
    }
    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (CandidateList?.ItemsSource is null) return;
        string query = FilterText.Text.Trim();
        CollectionViewSource.GetDefaultView(CandidateList.ItemsSource).Filter = value => value is CandidateRow row &&
            (query.Length == 0 || (row.Candidate.RelativePath + " " + row.Candidate.Function + " " + row.Candidate.Condition + " " + row.Candidate.Hint).Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void CandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (scan is null || CandidateList.SelectedItem is not CandidateRow row) return;
        var c = row.Candidate;
        var file = scan.Snapshot.Files.Single(f => f.RelativePath == c.RelativePath);
        var lines = file.OriginalText.Split('\n');
        int start = Math.Max(0, c.Line - 5), end = Math.Min(lines.Length, c.Line + 9);
        ContextText.Text = c.RelativePath + ":" + c.Line + "  " + c.Function + Environment.NewLine + c.Reason + Environment.NewLine +
            string.Join(Environment.NewLine, Enumerable.Range(start, end - start).Select(i => $"{i + 1,5}: {lines[i]}"));
        if (plan is not null) PreviewText.Text = plan.Files.Single(f => f.RelativePath == c.RelativePath).ProposedText;
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        CandidateList.CommitEdit(DataGridEditingUnit.Cell, true); CandidateList.CommitEdit(DataGridEditingUnit.Row, true);
        if (scan is null) return;
        var current = scan;
        var selections = rows.Where(r => r.Selected).Select(r => r.Selection()).ToArray();
        if (selections.Length == 0) { StatusText.Text = "追加する候補を選択してください。"; return; }
        InvalidatePlan(); SetBusy(true);
        try
        {
            plan = await Task.Run(() => service.BuildPlan(current, selections));
            PreviewText.Text = plan.Files.FirstOrDefault(f => f.HasChanges)?.ProposedText ?? "";
            StatusText.Text = ReportExporter.Summary(plan) + "（元ソースの変更なし）。実際のカウンター名はプレビューで確認できます。";
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (plan is null) return;
        var picker = new OpenFolderDialog { Title = "ソース外の空の出力フォルダー" };
        if (picker.ShowDialog(this) != true) return;
        var current = plan; SetBusy(true);
        try { await Task.Run(() => ReportExporter.Export(current, picker.FolderName)); StatusText.Text = "保存しました: " + picker.FolderName; }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (plan is not { ChangedFiles: > 0, DiagnosticCount: 0 }) return;
        var current = plan;
        if (MessageBox.Show(this, $"{current.RootPath}\n\n{current.ChangedFiles} ファイルに選択済みイベントカウンターを追加します。\nバックアップを作成して適用しますか？", "イベントカウンターの適用", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            string backup = await Task.Run(() => new SourceWorkspace().Apply(current));
            scan = null; rows = []; CandidateList.ItemsSource = null;
            StatusText.Text = "適用しました。再解析してください。バックアップ: " + backup;
        }
        catch (Exception error) { ShowError(error); }
        finally { plan = null; SetBusy(false); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void SetBusy(bool value)
    {
        busy = value;
        AnalyzeButton.IsEnabled = BrowseButton.IsEnabled = SourcePath.IsEnabled = FilterText.IsEnabled = !value;
        CandidateList.IsEnabled = PreviewButton.IsEnabled = !value && scan is not null;
        ExportButton.IsEnabled = !value && plan is not null;
        ApplyButton.IsEnabled = !value && plan is { ChangedFiles: > 0, DiagnosticCount: 0 };
        CancelButton.IsEnabled = value && cancellation is not null;
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowError(Exception error)
    {
        StatusText.Text = "処理に失敗しました: " + error.Message;
        MessageBox.Show(this, error.Message, "UEEtc", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy) { cancellation?.Cancel(); e.Cancel = true; StatusText.Text = "処理終了後に閉じてください。"; }
        base.OnClosing(e);
    }
}
