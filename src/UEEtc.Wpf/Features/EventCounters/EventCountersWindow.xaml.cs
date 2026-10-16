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
using UEEtc.Wpf.Preview;

namespace UEEtc.Wpf.Features.EventCounters;

public sealed class CandidateRow(EventCounterCandidate candidate, Action changed, Action<CandidateRow>? edited = null,
    string? savedName = null, string? savedDisplay = null) : INotifyPropertyChanged
{
    public EventCounterCandidate Candidate { get; } = candidate;
    private bool selected = true;
    private bool trackActiveScopes;
    private bool includeActiveScope = EventCounterDefaults.Create(candidate).IncludeActiveScope;
    public bool IncludeActiveScope { get => includeActiveScope; set { includeActiveScope = value; if (value) TrackActiveScopes = false; Changed(nameof(IncludeActiveScope)); } }
    public string AutomaticDecision => EventCounterDefaults.Explain(Candidate);
    public string PlannedMacros => "INCREMENT" + (IncludeActiveScope || TrackActiveScopes ? " + DECREMENT（スコープ）" : "") + (QuantityEnabled ? " + ADD " + QuantityValue + " [" + QuantityUnit + "]" : "");
    private bool changingMeasurement;
    public bool TrackActiveScopes
    {
        get => trackActiveScopes;
        set
        {
            if (trackActiveScopes == value) return;
            if (value) IncludeActiveScope = false;
            changingMeasurement = true;
            string activeName = Candidate.SuggestedName.EndsWith("_Count", StringComparison.Ordinal) ? Candidate.SuggestedName[..^6] + "_ActiveScopes" : Candidate.SuggestedName + "_ActiveScopes";
            if (CounterName == (value ? Candidate.SuggestedName : activeName)) CounterName = value ? activeName : Candidate.SuggestedName;
            if (DisplayName == (value ? Candidate.DisplayName : Candidate.DisplayName + " ActiveScopes")) DisplayName = Candidate.DisplayName + (value ? " ActiveScopes" : "");
            trackActiveScopes = value; changingMeasurement = false; Changed(nameof(TrackActiveScopes));
        }
    }
    private string counterName = savedName ?? candidate.SuggestedName;
    private string displayName = savedDisplay ?? candidate.DisplayName;
    private bool quantityEnabled = candidate.RecommendedQuantity is not null, meaningConfirmed = candidate.RecommendedQuantity is not null;
    private string quantityName = candidate.RecommendedQuantity?.CreateInputSelection(candidate).CounterName ?? candidate.SuggestedName + "Quantity",
        quantityDisplay = candidate.RecommendedQuantity?.CreateInputSelection(candidate).DisplayName ?? candidate.DisplayName + " Quantity",
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
    private void Changed(string name) { PropertyChanged?.Invoke(this, new(name)); PropertyChanged?.Invoke(this, new(nameof(PlannedMacros))); changed(); if (!changingMeasurement && name is nameof(CounterName) or nameof(DisplayName)) edited?.Invoke(this); }
    public EventCounterSelection Selection() => new(Candidate.Id, CounterName, DisplayName,
        QuantityEnabled ? new(QuantityName, QuantityDisplay, QuantityValue, QuantityUnit, MeaningConfirmed) : null,
        TrackActiveScopes ? CounterMeasurement.ActiveScope : CounterMeasurement.Cumulative, IncludeActiveScope);
}

public partial class EventCountersWindow : Window
{
    private readonly EventCounterService service = new();
    private EventCounterScan? scan;
    private AnalysisPlan? plan;
    private List<CandidateRow> rows = [];
    private List<EventFileRow> files = [];
    private bool HasFileSelection => files.Any(f => f.Selected && f.CanSelect);
    public AnalysisPlan? SelectedPlan => plan is null ? null : plan with { Files = plan.Files.Where(f => files.Any(row => row.Selected && row.RelativePath == f.RelativePath)).ToArray() };
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
        SelectionActions.Attach(FileList);
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
        ApplyButton.ToolTip = scan is null ? "解析・プレビューを実行してください。" : "変更した設定のプレビュー更新を待ってください。";
        PreviewText.Clear();
        PreviewSummary.Text = "";
        foreach (var file in files) file.RefreshCount();
        StatusText.Text = $"適用選択 {files.Count(f => f.Selected)} ファイル / 候補 {rows.Count} 件。変更案を更新します。検索で隠れた選択も対象です。";
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
        scan = null; rows = []; files = []; FileList.ItemsSource = CandidateList.ItemsSource = null;
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
        scan = null; rows = []; files = []; FileList.ItemsSource = CandidateList.ItemsSource = null; InvalidatePlan();
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
                inputs.Get(scope + c.Id + ".Name"), RestoredDisplay(c, inputs.Get(scope + c.Id + ".Display")))).ToList();
            files = scan.Snapshot.Files.Select(f => new EventFileRow(f, rows.Where(r => r.Candidate.RelativePath == f.RelativePath).ToArray(), InvalidatePlan, initiallySelected: true)).ToList();
            FileList.ItemsSource = files;
            FilterChanged(this, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
            DiagnosticsText.Text = string.Join(Environment.NewLine, scan.Snapshot.Diagnostics.Concat(scan.Snapshot.Files.SelectMany(f => f.Diagnostics.Select(d => f.RelativePath + ": " + d))));
            FileList.SelectedItem = FileList.Items.Cast<EventFileRow>().FirstOrDefault(f => f.CanSelect) ?? FileList.Items.Cast<EventFileRow>().FirstOrDefault();
            await RefreshPreview();
        }
        catch (OperationCanceledException) { StatusText.Text = "解析を中止しました。"; }
        catch (Exception error) { ShowError(error); }
        finally { cancellation = null; SetBusy(false); }
    }
    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (FileList?.ItemsSource is null) return;
        string query = FilterText.Text.Trim();
        CollectionViewSource.GetDefaultView(FileList.ItemsSource).Filter = value => value is EventFileRow row &&
            (query.Length == 0 || (row.RelativePath + " " + string.Join(" ", row.Candidates.Select(c => c.Candidate.Function + " " + c.Candidate.Condition + " " + c.Candidate.Hint))).Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private static string? RestoredDisplay(EventCounterCandidate c, string? saved) => saved?.StartsWith("UEEtc/Events/" + c.RelativePath + "/" + c.Function + "/", StringComparison.Ordinal) == true ? null : saved;
    private void FileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not EventFileRow file) { CandidateList.ItemsSource = null; OriginalText.Text = ContextText.Text = ""; PreviewText.Clear(); return; }
        CandidateList.ItemsSource = file.Candidates;
        CandidateList.SelectedItem = file.Candidates.FirstOrDefault();
        ShowFile(file.RelativePath);
    }
    private void ShowFile(string relativePath)
    {
        var file = plan?.Files.SingleOrDefault(f => f.RelativePath == relativePath) ?? scan?.Snapshot.Files.SingleOrDefault(f => f.RelativePath == relativePath);
        if (file is null) return;
        OriginalText.Text = file.OriginalText;
        PreviewText.ShowComparison(file.OriginalText, file.ProposedText);
        PreviewSummary.Text = $"このファイル: イベント {file.AddedEventCounters} 箇所、DECREMENT {file.AddedDecrements} 箇所、ADD {file.AddedQuantityCounters} 箇所。\n" +
            string.Join("\n", rows.Where(r => r.Candidate.RelativePath == relativePath && r.Selected && file.Functions.Any(f => f.Line == r.Candidate.Line && f.Name == r.Candidate.Function))
                .Select(r => $"L{r.Candidate.Line} {r.Candidate.Function}: {r.PlannedMacros}\n  解析時の判断: {r.AutomaticDecision}"));
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
        bool selected = HasFileSelection;
        var selectedPaths = files.Where(f => f.Selected).Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        var selections = rows.Where(r => r.Selected && (!selected || selectedPaths.Contains(r.Candidate.RelativePath))).Select(r => r.Selection()).ToArray();
        plan = null; PreviewText.Clear(); previewBuilding = true; SetBusy(true);
        int version = revision;
        try
        {
            var token = cancellation?.Token ?? CancellationToken.None;
            var result = await Task.Run(() => service.TryBuildPreview(current, selections, token));
            if (version != revision) return;
            if (result.Plan is null) { StatusText.Text = "プレビューを更新できません: " + string.Join(" / ", result.Errors.Distinct().Take(5)); return; }
            plan = result.Plan;
            if (FileList.SelectedItem is EventFileRow file) ShowFile(file.RelativePath);
            StatusText.Text = ReportExporter.Summary(plan) + (selected ? $"。適用選択 {files.Count(f => f.Selected)} ファイルの変更案。" : "。全候補の参考プレビュー。適用するファイルをチェックしてください（選択0件）。") +
                (plan.RequiresQuantityConfirmation ? " 数量ADDは推奨・未確認です。意味と単位を確認するか、ADDを無効にしてください。" : "");
        }
        catch (Exception error) { StatusText.Text = "プレビューを更新できません: " + error.Message; }
        finally { previewBuilding = false; SetBusy(false); if (version != revision) QueuePreview(); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlan is not { ChangedFiles: > 0 } current) return;
        var picker = new OpenFolderDialog { Title = "ソース外の空の出力フォルダー" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try { await Task.Run(() => ReportExporter.Export(current, picker.FolderName)); StatusText.Text = "保存しました: " + picker.FolderName; }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (plan is not { DiagnosticCount: 0, RequiresQuantityConfirmation: false } || SelectedPlan is not { ChangedFiles: > 0 } current) return;
        if (MessageBox.Show(this, $"{current.RootPath}\n\n{current.ChangedFiles} ファイルに選択済みイベントカウンターを追加します。\nバックアップを作成して適用しますか？", "イベントカウンターの適用", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            string backup = await Task.Run(() => new SourceWorkspace().Apply(current));
            scan = null; rows = []; files = []; FileList.ItemsSource = CandidateList.ItemsSource = null;
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
        SelectAllButton.IsEnabled = ClearAllButton.IsEnabled = SelectRowsButton.IsEnabled = ClearRowsButton.IsEnabled = !value;
        PreviewButton.IsEnabled = !value && scan is not null;
        FileList.IsEnabled = CandidateList.IsEnabled = QuantityEditor.IsEnabled = scan is not null && (!value || previewBuilding);
        ExportButton.IsEnabled = !value && SelectedPlan is { ChangedFiles: > 0 };
        ApplyButton.IsEnabled = !value && SelectedPlan is { ChangedFiles: > 0 } && plan is { DiagnosticCount: 0, RequiresQuantityConfirmation: false };
        ApplyButton.ToolTip = value ? "解析・プレビューの完了を待ってください。" : plan is null ? "解析・プレビューを実行し、入力エラーがあれば修正してください。" :
            plan.DiagnosticCount > 0 ? "診断の内容を解消してから再解析してください。" :
            plan.RequiresQuantityConfirmation ? "詳細設定で数量ADDの意味・単位を確認するか、ADDを無効にしてください。" :
            SelectedPlan is not { ChangedFiles: > 0 } ? "変更候補のあるファイルを一覧でチェックしてください。" :
            $"選択した {SelectedPlan.ChangedFiles} ファイルに、確認後バックアップを作成して適用します。";
        CancelButton.IsEnabled = value && cancellation is not null;
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SelectVisible_Click(object sender, RoutedEventArgs e) => SelectionActions.Set(FileList.Items.OfType<ISelectionRow>(), true);
    private void ClearVisible_Click(object sender, RoutedEventArgs e) => SelectionActions.Set(FileList.Items.OfType<ISelectionRow>(), false);
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectionActions.Set(files, true);
    private void ClearAll_Click(object sender, RoutedEventArgs e) => SelectionActions.Set(files, false);
    private void SelectRows_Click(object sender, RoutedEventArgs e) => SelectionActions.Set(FileList.SelectedItems.OfType<ISelectionRow>(), true);
    private void ClearRows_Click(object sender, RoutedEventArgs e) => SelectionActions.Set(FileList.SelectedItems.OfType<ISelectionRow>(), false);
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
