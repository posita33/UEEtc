using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Plans;
using UEEtc.Core.Reporting;
using UEEtc.Wpf.InputAssistance;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using UEEtc.Wpf.Preview;
using UEEtc.Wpf.Workflow;

namespace UEEtc.Wpf.Features.EventCounters;

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
    private readonly PlanFileActions fileActions;
    public EventCountersWindow() : this(InputSettingsStore.Default) { }
    public EventCountersWindow(InputSettingsStore settings)
    {
        InitializeComponent();
        fileActions = new(this, value => SetBusy(value), text => StatusText.Text = text, ShowError);
        SelectionActions.Attach(FileList);
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); if (!busy && scan is not null) Preview_Click(this, new RoutedEventArgs()); };
        inputs = new(this, settings, text => StatusText.Text = text);
        BackupLocation.Initialize(inputs, "EventCounters");
        BackupLocation.Changed += (_, _) => SetBusy(busy);
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
    private async void Export_Click(object sender, RoutedEventArgs e) => await fileActions.ExportAsync(SelectedPlan);
    private async void Apply_Click(object sender, RoutedEventArgs e) =>
        await fileActions.ApplyAsync(plan, SelectedPlan, BackupLocation.BackupRoot, "イベントカウンター", applied =>
        {
            plan = null;
            if (!applied) return;
            scan = null; rows = []; files = []; FileList.ItemsSource = CandidateList.ItemsSource = null;
        });
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void SetBusy(bool value)
    {
        busy = value;
        BackupLocation.IsEnabled = !value;
        AnalyzeButton.IsEnabled = BrowseButton.IsEnabled = SourcePath.IsEnabled = FilterText.IsEnabled = SelectVisibleButton.IsEnabled = ClearVisibleButton.IsEnabled = !value;
        SelectAllButton.IsEnabled = ClearAllButton.IsEnabled = SelectRowsButton.IsEnabled = ClearRowsButton.IsEnabled = !value;
        PreviewButton.IsEnabled = !value && scan is not null;
        FileList.IsEnabled = CandidateList.IsEnabled = QuantityEditor.IsEnabled = scan is not null && (!value || previewBuilding);
        var availability = PlanAvailability.For(plan, SelectedPlan, value).WithBackupRoot(SelectedPlan, BackupLocation.BackupRoot);
        ExportButton.IsEnabled = availability.CanExport;
        ApplyButton.IsEnabled = availability.CanApply; ApplyButton.ToolTip = availability.ApplyHint;
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
