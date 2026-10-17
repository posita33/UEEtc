using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using Microsoft.Win32;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Plans;
using UEEtc.Core.Reporting;
using UEEtc.Wpf.InputAssistance;
using UEEtc.Wpf.Preview;

namespace UEEtc.Wpf.Workflow;

internal delegate AnalysisPlan AnalyzePlan(string root, bool focusedCpu, CpuTraceSettings cpuTrace, IProgress<string> progress, CancellationToken token);

// Shared single-plan workflow. Feature rules stay in Core; event editing keeps its own workflow.
internal sealed class PlanWindowWorkflow
{
    private readonly Window owner;
    private readonly PlanWindowView view;
    private readonly AnalyzePlan analyze;
    private readonly PlanFileActions actions;
    private AnalysisPlan? plan;
    private List<SelectableFile> files = [];
    private CancellationTokenSource? cancellation;
    public bool IsBusy { get; private set; }
    public AnalysisPlan? SelectedPlan => plan is null ? null : SelectableFile.Select(plan, files);
    private static readonly string[] SelectionButtons = ["SelectAllButton", "ClearAllButton", "SelectRowsButton", "ClearRowsButton", "SelectVisibleButton", "ClearVisibleButton"];

    public PlanWindowWorkflow(Window owner, InputSettingsStore settings, string key, string description, AnalyzePlan analyze)
    {
        this.owner = owner; this.analyze = analyze; view = new(owner);
        actions = new(owner, value => SetBusy(value), text => view.Status.Text = text, ShowError);
        SelectionActions.Attach(view.Files);
        view.SourcePath.TextChanged += InputsChanged;
        view.AllFunctions.Click += InputsChanged;
        view.Filter.TextChanged += (_, _) => FilterFiles();
        view.Files.SelectionChanged += (_, _) => ShowFile();
        view.Button("AnalyzeButton").Click += async (_, _) => await AnalyzeAsync();
        view.Button("CancelButton").Click += (_, _) => cancellation?.Cancel();
        view.Button("BrowseButton").Click += (_, _) => Browse();
        view.Button("ExportButton").Click += async (_, _) => await actions.ExportAsync(SelectedPlan);
        view.Button("ApplyButton").Click += async (_, _) => await actions.ApplyAsync(plan, SelectedPlan, view.BackupLocation.BackupRoot, description, _ => plan = null);
        BindSelection("SelectAllButton", () => files, true);
        BindSelection("ClearAllButton", () => files, false);
        BindSelection("SelectVisibleButton", () => view.Files.Items.OfType<ISelectionRow>(), true);
        BindSelection("ClearVisibleButton", () => view.Files.Items.OfType<ISelectionRow>(), false);
        BindSelection("SelectRowsButton", () => view.Files.SelectedItems.OfType<ISelectionRow>(), true);
        BindSelection("ClearRowsButton", () => view.Files.SelectedItems.OfType<ISelectionRow>(), false);
        owner.Closing += (_, e) =>
        {
            if (!IsBusy) return;
            cancellation?.Cancel(); e.Cancel = true; view.Status.Text = "処理終了後に閉じてください。";
        };
        var inputs = new RememberedInputs(owner, settings, text => view.Status.Text = text);
        view.CpuTrace.Initialize(inputs, key);
        view.CpuTrace.Changed += (_, _) => InputsChanged(owner, new RoutedEventArgs());
        view.BackupLocation.Initialize(inputs, key);
        view.BackupLocation.Changed += (_, _) => SetBusy(IsBusy);
        inputs.Bind(view.SourcePath, key + ".SourcePath"); inputs.Bind(view.Filter, key + ".Filter");
        FolderDropInput.Attach(view.SourcePath, text => view.Status.Text = text); inputs.ReportLoadError();
    }

    private void BindSelection(string name, Func<IEnumerable<ISelectionRow>> rows, bool selected) =>
        view.Button(name).Click += (_, _) => SelectionActions.Set(rows(), selected);

    private void InputsChanged(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        Clear(); SetBusy(false); view.Status.Text = "設定が変更されました。解析を実行してください。";
    }
    private void Clear()
    {
        plan = null; files = []; view.Files.ItemsSource = view.Functions.ItemsSource = null;
        view.Preview.Clear(); view.Original.Text = view.Diagnostics.Text = "";
    }
    private void Browse()
    {
        var picker = new OpenFolderDialog { Title = "解析するソースフォルダーを選択" };
        if (picker.ShowDialog(owner) == true) view.SourcePath.Text = picker.FolderName;
    }

    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;
        Clear();
        string root = view.SourcePath.Text.Trim(); bool focused = view.AllFunctions.IsChecked != true;
        var cpuTrace = view.CpuTrace.Settings;
        using var cts = new CancellationTokenSource(); cancellation = cts; SetBusy(true, canCancel: true);
        try
        {
            var progress = new Progress<string>(path => view.Status.Text = "解析中: " + path);
            var analyzed = await Task.Run(() => analyze(root, focused, cpuTrace, progress, cts.Token));
            cts.Token.ThrowIfCancellationRequested();
            plan = analyzed;
            files = plan.Files.Select(f => new SelectableFile(f, SelectionChanged, initiallySelected: true)).ToList();
            view.Files.ItemsSource = files; FilterFiles();
            view.Diagnostics.Text = plan.DiagnosticCount == 0 ? "診断なし。C++ のコンパイル確認は別途必要です。" :
                string.Join(Environment.NewLine, plan.Diagnostics.Concat(plan.Files.SelectMany(f => f.Diagnostics.Select(d => f.RelativePath + ": " + d))));
            view.Files.SelectedItem = view.Files.Items.Cast<SelectableFile>().FirstOrDefault(f => f.CanSelect) ?? view.Files.Items.Cast<SelectableFile>().FirstOrDefault();
            view.Status.Text = ReportExporter.Summary(plan) + $"。変更候補のある {files.Count(f => f.Selected)} ファイルを適用対象に選択しました。一覧で対象を外せます。";
        }
        catch (OperationCanceledException) { view.Status.Text = "解析を中止しました。"; }
        catch (Exception exception) { ShowError(exception); }
        finally { cancellation = null; SetBusy(false); }
    }

    private void ShowFile()
    {
        if (view.Files.SelectedItem is not SelectableFile row) { view.Preview.Clear(); view.Original.Text = ""; view.Functions.ItemsSource = null; return; }
        view.Preview.ShowComparison(row.File.OriginalText, row.File.ProposedText);
        view.Original.Text = row.File.OriginalText; view.Functions.ItemsSource = row.File.Functions;
    }
    private void SetBusy(bool value, bool canCancel = false)
    {
        IsBusy = value;
        view.BackupLocation.IsEnabled = !value;
        view.SourcePath.IsEnabled = view.Filter.IsEnabled = view.Files.IsEnabled = view.CpuTrace.IsEnabled = view.AllFunctions.IsEnabled = !value;
        foreach (string name in SelectionButtons.Concat(["BrowseButton", "AnalyzeButton"])) view.Button(name).IsEnabled = !value;
        view.Button("CancelButton").IsEnabled = value && canCancel;
        var availability = PlanAvailability.For(plan, SelectedPlan, value).WithBackupRoot(SelectedPlan, view.BackupLocation.BackupRoot);
        view.Button("ExportButton").IsEnabled = availability.CanExport;
        view.Button("ApplyButton").IsEnabled = availability.CanApply; view.Button("ApplyButton").ToolTip = availability.ApplyHint;
        view.Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SelectionChanged()
    {
        SetBusy(IsBusy);
        view.Status.Text = $"適用選択 {files.Count(f => f.Selected && f.CanSelect)} / 変更候補 {files.Count(f => f.CanSelect)} ファイル。検索で隠れた選択も対象です。";
    }
    private void FilterFiles()
    {
        if (view.Files.ItemsSource is null) return;
        string query = view.Filter.Text.Trim();
        CollectionViewSource.GetDefaultView(view.Files.ItemsSource).Filter = value => value is SelectableFile row &&
            (query.Length == 0 || (row.RelativePath + " " + string.Join(" ", row.File.Functions.Select(f => f.Name + " " + f.CpuDecision + " " + f.CounterDecision))).Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void ShowError(Exception exception)
    {
        view.Status.Text = "処理に失敗しました: " + exception.Message;
        MessageBox.Show(owner, exception.Message, "UEEtc", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
