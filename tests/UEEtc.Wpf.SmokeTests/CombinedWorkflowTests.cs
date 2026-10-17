using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UEEtc.Core.Source;
using UEEtc.Wpf;
using UEEtc.Wpf.Features.CombinedTracing;
using UEEtc.Wpf.Preview;

internal static class CombinedWorkflowTests
{
    public static void Run(MainWindow home, string root, Action<bool, string> check, string? screenshots)
    {
        string source = Path.Combine(root, "combined"); Directory.CreateDirectory(source);
        const string batch = "#include \"Fixture.h\"\nvoid F::Batch(const TArray<int>& Items) { Super::Batch(); if (Ready) { for (auto Item : Items) Use(Item); } }";
        const string eventOnly = "#include \"Fixture.h\"\nvoid F::EventOnly(uint32 NumBytes) { if (Ready) { Use(); } }";
        File.WriteAllText(Path.Combine(source, "Batch.cpp"), batch);
        File.WriteAllText(Path.Combine(source, "CpuOnly.cpp"), "#include \"Fixture.h\"\nvoid F::CpuOnly() { for (int i=0;i<4;++i) Use(); }");
        File.WriteAllText(Path.Combine(source, "EventOnly.cpp"), eventOnly);
        File.WriteAllText(Path.Combine(source, "Empty.cpp"), "#include \"Fixture.h\"\nvoid F::Empty() {}\n");
        Click(home, "CombinedFeatureButton");
        var window = home.OwnedWindows.OfType<CombinedTracingWindow>().Single();
        try
        {
            check(window.Owner == home && home.OwnedWindows.Count >= 3, "Combined tracing opens as a separate launcher window");
            check(!((Button)window.FindName("ApplyButton")).IsEnabled, "Combined apply requires fresh analysis");
            ((TextBox)window.FindName("SourcePath")).Text = source; Click(window, "AnalyzeButton"); Until(() => !window.IsBusy);
            var files = ((IEnumerable<SelectableFile>)((DataGrid)window.FindName("FileList")).ItemsSource).ToArray();
            check(files.Count(f => f.Selected) == 3 && files.Single(f => f.RelativePath == "Empty.cpp").Selected == false, "Combined analysis selects all changed files only");
            var plan = window.SelectedPlan!;
            check(plan.AddedCpuScopes == 2 && plan.AddedEventCounters == 2 && plan.AddedQuantityCounters == 2 && plan.AddedDecrements == 1, "One combined analysis includes both policies and automatic quantity/scope metrics");
            var preview = (CodeChangePreview)window.FindName("PreviewText");
            check(preview.Text.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR(") && preview.Text.Contains("TRACE_COUNTER_ADD(") && preview.Text.Contains("TRACE_COUNTER_DECREMENT("), "The same preview contains CPU and event changes");
            check(files.Single(f => f.RelativePath == "Batch.cpp") is { CpuCount: 1, EventCount: 1, AddCount: 1, DecrementCount: 1 }, "Combined file row exposes each feature count");
            check(((Button)window.FindName("ApplyButton")).IsEnabled, "Combined preview is directly applicable");
            var filter = (TextBox)window.FindName("FilterText"); filter.Text = "CpuOnly";
            check(((DataGrid)window.FindName("FileList")).Items.Count == 1 && window.SelectedPlan?.ChangedFiles == 3, "Combined filtering retains explicit hidden selections");
            filter.Text = ""; Click(window, "ClearAllButton");
            check(window.SelectedPlan?.ChangedFiles == 0 && !((Button)window.FindName("ApplyButton")).IsEnabled, "Combined clear-all disables apply");
            Click(window, "SelectAllButton");
            var editor = (UEEtc.Wpf.Workflow.CpuTraceSettingsEditor)window.FindName("CpuTraceSettings");
            ((TextBox)editor.FindName("MacroNameInput")).Text = "PROJECT_TRACE_CPU_SCOPE_STR";
            ((TextBox)editor.FindName("HeaderInput")).Text = "Tracing/ProjectCpuTrace.h";
            check(window.SelectedPlan is null && !((Button)window.FindName("ApplyButton")).IsEnabled, "Combined CPU settings changes invalidate both-feature plan");
            Click(window, "AnalyzeButton"); Until(() => !window.IsBusy);
            check(window.SelectedPlan is { AddedCpuScopes: 2, AddedEventCounters: 2 } && preview.Text.Contains("PROJECT_TRACE_CPU_SCOPE_STR("), "Combined custom STR wrapper retains event generation");
            files = ((IEnumerable<SelectableFile>)((DataGrid)window.FindName("FileList")).ItemsSource).ToArray();
            files.Single(f => f.RelativePath == "EventOnly.cpp").Selected = false;
            check(window.SelectedPlan?.ChangedFiles == 2, "Combined apply selection can exclude individual files");
            check(File.ReadAllText(Path.Combine(source, "Batch.cpp")) == batch, "Combined analysis and preview leave original source unchanged");
            if (screenshots is not null) Program.Screenshot(window, Path.Combine(screenshots, "combined.png"), 1360, 900);
            plan = window.SelectedPlan!; string backup = new SourceWorkspace().Apply(plan, Path.Combine(root, "Backups"));
            check(File.ReadAllText(Path.Combine(source, "Batch.cpp")).Contains("PROJECT_TRACE_CPU_SCOPE_STR") && File.ReadAllText(Path.Combine(source, "Batch.cpp")).Contains("TRACE_COUNTER_ADD"), "Combined apply writes both features together");
            check(File.ReadAllText(Path.Combine(source, "EventOnly.cpp")) == eventOnly && File.ReadAllText(Path.Combine(backup, "Batch.cpp")) == batch, "Combined backup preserves original and excluded sources are unchanged");
            ((TextBox)window.FindName("SourcePath")).Text += "-changed";
            check(window.SelectedPlan is null && !((Button)window.FindName("ApplyButton")).IsEnabled, "Combined source change clears stale plan");
        }
        finally { window.Close(); }
    }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Until(Func<bool> condition)
    {
        var time = Stopwatch.StartNew();
        while (!condition() && time.Elapsed < TimeSpan.FromSeconds(30))
        {
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
        if (!condition()) throw new Exception("Combined workflow timed out");
    }
}
