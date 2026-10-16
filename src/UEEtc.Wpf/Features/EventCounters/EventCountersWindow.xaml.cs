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
using System.Windows.Threading;

namespace UEEtc.Wpf.Features.EventCounters;

public sealed class CandidateRow(EventCounterCandidate candidate, Action changed, Action<CandidateRow>? edited = null,
    string? savedName = null, string? savedDisplay = null) : INotifyPropertyChanged
{
    public EventCounterCandidate Candidate { get; } = candidate;
    private bool selected;
    private string counterName = savedName ?? candidate.SuggestedName;
    private string displayName = savedDisplay ?? candidate.DisplayName;
    private bool quantityEnabled = candidate.RecommendedQuantity is not null, meaningConfirmed;
    private string quantityName = candidate.SuggestedName + "Quantity", quantityDisplay = candidate.DisplayName + "/Quantity",
        quantityValue = candidate.RecommendedQuantity?.Value ?? "", quantityUnit = candidate.RecommendedQuantity?.Unit ?? "";
    public string Recommendation => Candidate.RecommendedQuantity is { } q ? $"{q.Value} [{q.Unit}] — {q.Reason}" : "推奨なし（値・単位を安全に推定できない、または候補が複数）";
    public bool QuantityEnabled { get => quantityEnabled; set { quantityEnabled = value; Changed(nameof(QuantityEnabled)); } }
    public bool MeaningConfirmed { get => meaningConfirmed; set { meaningConfirmed = value; Changed(nameof(MeaningConfirmed)); } }
    public string QuantityName { get => quantityName; set { quantityName = value; Changed(nameof(QuantityName)); } }
    public string QuantityDisplay { get => quantityDisplay; set { quantityDisplay = value; Changed(nameof(QuantityDisplay)); } }
    public string QuantityValue { get => quantityValue; set { if (quantityValue == value) return; quantityValue = value; MeaningConfirmed = false; Changed(nameof(QuantityValue)); } }
    public string QuantityUnit { get => quantityUnit; set { if (quantityUnit == value) return; quantityUnit = value; MeaningConfirmed = false; Changed(nameof(QuantityUnit)); } }
    public bool Selected { get => selected; set { selected = value; Changed(nameof(Selected)); } }
    public string CounterName { get => counterName; set { counterName = value; Changed(nameof(CounterName)); } }
    public string DisplayName { get => displayName; set { displayName = value; Changed(nameof(DisplayName)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) { PropertyChanged?.Invoke(this, new(name)); changed(); if (name != nameof(Selected)) edited?.Invoke(this); }
    public EventCounterSelection Selection() => new(Candidate.Id, CounterName, DisplayName,
        QuantityEnabled ? new(QuantityName, QuantityDisplay, QuantityValue, QuantityUnit, MeaningConfirmed) : null);
}

public partial class EventCountersWindow : Window
{
    private readonly EventCounterService service = new();
    private EventCounterScan? scan;
    private AnalysisPlan? plan;
    private List<CandidateRow> rows = [];
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool previewBuilding;
    private readonly DispatcherTimer previewTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private int revision;
    public bool IsBusy => busy;
    public bool IsPreviewPending => previewTimer.IsEnabled || busy;
    private readonly RememberedInputs inputs;
    public EventCountersWindow() : this(InputSettingsStore.Default) { }
    public EventCountersWindow(InputSettingsStore settings)
    {
        InitializeComponent();
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); if (!busy && scan is not null) Preview_Click(this, new RoutedEventArgs()); };
        inputs = new(this, settings, text => StatusText.Text = text);
        inputs.Bind(SourcePath, "EventCounters.SourcePath");
        inputs.Bind(FilterText, "EventCounters.Filter");
        FolderDropInput.Attach(SourcePath, text => StatusText.Text = text);
        inputs.ReportLoadError();
    }

    private void InvalidatePlan()
    {
        revision++;
        plan = null;
        if (ExportButton is null) return;
        ExportButton.IsEnabled = ApplyButton.IsEnabled = false;
        PreviewText.Clear();
        StatusText.Text = $"選択 {rows.Count(r => r.Selected)} / 候補 {rows.Count} 件。選択・名前を確認してプレビューしてください。絞り込みで隠れた選択も対象です。";
        QueuePreview();
    }
    private void QueuePreview()
    {
        previewTimer.Stop();
        if (scan is not null) previewTimer.Start();
    }
    private void SourceChanged(object sender, TextChangedEventArgs e)
    {
        if (AnalyzeButton is null || busy) return;
        scan = null; rows = []; CandidateList.ItemsSource = null;
        ContextText.Text = OriginalText.Text = DiagnosticsText.Text = "";
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
        ContextText.Text = OriginalText.Text = DiagnosticsText.Text = "";
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
            CandidateList.SelectedItem = CandidateList.Items.Cast<CandidateRow>().FirstOrDefault();
            await RefreshPreview();
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
        if (scan is null || CandidateList.SelectedItem is not CandidateRow row) { ContextText.Text = OriginalText.Text = ""; PreviewText.Clear(); return; }
        var c = row.Candidate;
        var file = scan.Snapshot.Files.Single(f => f.RelativePath == c.RelativePath);
        OriginalText.Text = file.OriginalText;
        var lines = file.OriginalText.Split('\n');
        int start = Math.Max(0, c.Line - 5), end = Math.Min(lines.Length, c.Line + 9);
        ContextText.Text = c.RelativePath + ":" + c.Line + "  " + c.Function + Environment.NewLine + c.Reason + Environment.NewLine +
            string.Join(Environment.NewLine, Enumerable.Range(start, end - start).Select(i => $"{i + 1,5}: {lines[i]}"));
        var proposed = plan?.Files.SingleOrDefault(f => f.RelativePath == c.RelativePath) ?? file;
        PreviewText.ShowComparison(file.OriginalText, proposed.ProposedText);
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await RefreshPreview();
    }
    private async Task RefreshPreview()
    {
        previewTimer.Stop();
        CandidateList.CommitEdit(DataGridEditingUnit.Cell, true); CandidateList.CommitEdit(DataGridEditingUnit.Row, true);
        if (scan is null) return;
        var current = scan;
        bool selected = rows.Any(r => r.Selected);
        var selections = rows.Where(r => !selected || r.Selected).Select(r => r.Selection()).ToArray();
        plan = null; PreviewText.Clear(); previewBuilding = true; SetBusy(true);
        int version = revision;
        try
        {
            var token = cancellation?.Token ?? CancellationToken.None;
            var built = await Task.Run(() => service.BuildPreview(current, selections, token));
            if (version != revision) return;
            plan = built;
            var file = CandidateList.SelectedItem is CandidateRow row ? plan.Files.Single(f => f.RelativePath == row.Candidate.RelativePath) : plan.Files.FirstOrDefault(f => f.HasChanges);
            if (file is not null) PreviewText.ShowComparison(file.OriginalText, file.ProposedText);
            StatusText.Text = ReportExporter.Summary(plan) + (selected ? "。チェック済み候補の変更案。" : "。全候補の参考プレビュー。適用する候補をチェックしてください（選択0件）。") +
                (plan.RequiresQuantityConfirmation ? " 数量ADDは推奨・未確認です。意味と単位を確認するか、ADDを無効にしてください。" : "");
        }
        catch (Exception error) { StatusText.Text = "プレビューを更新できません: " + error.Message; }
        finally { previewBuilding = false; SetBusy(false); if (version != revision) QueuePreview(); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (plan is null || !rows.Any(r => r.Selected)) return;
        var picker = new OpenFolderDialog { Title = "ソース外の空の出力フォルダー" };
        if (picker.ShowDialog(this) != true) return;
        var current = plan; SetBusy(true);
        try { await Task.Run(() => ReportExporter.Export(current, picker.FolderName)); StatusText.Text = "保存しました: " + picker.FolderName; }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (plan is not { ChangedFiles: > 0, DiagnosticCount: 0, RequiresQuantityConfirmation: false } || !rows.Any(r => r.Selected)) return;
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
        AnalyzeButton.IsEnabled = BrowseButton.IsEnabled = SourcePath.IsEnabled = FilterText.IsEnabled = SelectVisibleButton.IsEnabled = ClearVisibleButton.IsEnabled = !value;
        PreviewButton.IsEnabled = !value && scan is not null;
        CandidateList.IsEnabled = QuantityEditor.IsEnabled = scan is not null && (!value || previewBuilding);
        ExportButton.IsEnabled = !value && plan is not null && rows.Any(r => r.Selected);
        ApplyButton.IsEnabled = !value && rows.Any(r => r.Selected) && plan is { ChangedFiles: > 0, DiagnosticCount: 0, RequiresQuantityConfirmation: false };
        CancelButton.IsEnabled = value && cancellation is not null;
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SelectVisible_Click(object sender, RoutedEventArgs e) { foreach (var row in CandidateList.Items.Cast<CandidateRow>()) row.Selected = true; }
    private void ClearVisible_Click(object sender, RoutedEventArgs e) { foreach (var row in CandidateList.Items.Cast<CandidateRow>()) row.Selected = false; }
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
    protected override void OnClosed(EventArgs e) { previewTimer.Stop(); scan = null; rows = []; base.OnClosed(e); }
}
