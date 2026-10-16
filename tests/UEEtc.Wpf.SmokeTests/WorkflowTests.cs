using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Input;
using UEEtc.Core.Source;
using UEEtc.Wpf.Features.CpuTracing;
using UEEtc.Wpf.Features.EventCounters;
using UEEtc.Wpf.InputAssistance;
using UEEtc.Wpf.Preview;

internal static class WorkflowTests
{
    public static void Run(string root, Action<bool, string> check, string? screenshots)
    {
        string source = Path.Combine(root, "workflow"); Directory.CreateDirectory(source);
        const string a = "#include \"Fixture.h\"\nvoid F::Alpha()\n{\n    for (int i=0;i<3;++i) Run();\n}\n";
        const string b = "#include \"Fixture.h\"\nvoid F::Beta(unsigned int NumBytes)\n{\n\tif (bSuccess)\n\t{\n\t\tFinish();\n\t}\n}\n";
        File.WriteAllText(Path.Combine(source, "Alpha.cpp"), a); File.WriteAllText(Path.Combine(source, "Beta.cpp"), b);
        var settings = new InputSettingsStore(Path.Combine(root, "workflow-settings.json"));
        var cpu = new CpuTracingWindow(settings); var events = new EventCountersWindow(settings);
        try
        {
            cpu.Show(); events.Show();
            ((CheckBox)cpu.FindName("AllFunctionsOption")).IsChecked = true;
            ((TextBox)cpu.FindName("SourcePath")).Text = source; Click(cpu, "AnalyzeButton"); Until(() => !cpu.IsBusy);
            var list = (DataGrid)cpu.FindName("FileList");
            var files = ((IEnumerable<SelectableFile>)list.ItemsSource).ToArray();
            check(files.Length == 2 && files.All(f => !f.Selected), "CPU file choices start empty");
            var filter = (TextBox)cpu.FindName("FilterText"); filter.Text = "Alpha";
            check(list.Items.Count == 1, "CPU filters by function and path"); Click(cpu, "SelectVisibleButton");
            check(cpu.SelectedPlan?.Files.Single().RelativePath == "Alpha.cpp", "Visible selection selects only Alpha");
            filter.Text = "Beta";
            check(cpu.SelectedPlan?.Files.Single().RelativePath == "Alpha.cpp", "Hidden selection remains explicit apply target");
            Click(cpu, "ClearVisibleButton"); check(files.Single(f => f.RelativePath == "Alpha.cpp").Selected, "Clear visible does not clear hidden file");
            filter.Text = ""; list.SelectedItem = files[0];
            Click(cpu, "SelectAllButton"); check(files.All(f => f.Selected), "CPU global select includes all rows");
            filter.Text = "Beta"; Click(cpu, "ClearAllButton"); check(files.All(f => !f.Selected), "Global clear includes hidden rows");
            filter.Text = ""; list.SelectedItems.Clear(); list.SelectedItems.Add(files[0]); list.SelectedItems.Add(files[1]);
            Click(cpu, "SelectRowsButton"); check(files.All(f => f.Selected), "Select highlighted CPU rows");
            Space(cpu, list); check(files.All(f => !f.Selected), "Space toggles highlighted CPU rows");
            Click(cpu, "SelectRowsButton"); Click(cpu, "ClearRowsButton"); check(files.All(f => !f.Selected), "Clear highlighted CPU rows");
            files[0].Selected = true; list.SelectedItem = files[0];
            var preview = (CodeChangePreview)cpu.FindName("PreviewText");
            check(preview.ChangedLines.Count > 0 && preview.ChangeCount >= 2, "CPU preview distinguishes include and body changes");
            ((Button)preview.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(preview.CurrentChangeIndex == 0, "Next change navigates to first block");
            preview.MoveChange(-1); check(preview.CurrentChangeIndex == preview.ChangeCount - 1, "Previous change wraps");
            check(preview.ChangedLines.All(line => ((Paragraph)((RichTextBox)preview.FindName("Editor")).Document.Blocks.ElementAt(line - 1)).Background is not null), "Changed lines have a background");
            if (screenshots is not null) Program.Screenshot(cpu, Path.Combine(screenshots, "workflow-cpu.png"), 1240, 800);
            string longSource = string.Join('\n', Enumerable.Range(0, 250).Select(i => "Line " + i));
            preview.ShowComparison(longSource, longSource.Replace("Line 1\n", "Added\nLine 1\n", StringComparison.Ordinal).Replace("Line 240\n", "Added late\nLine 240\n", StringComparison.Ordinal));
            cpu.UpdateLayout(); preview.MoveChange(1); preview.MoveChange(1); Pump();
            check(((RichTextBox)preview.FindName("Editor")).VerticalOffset > 0, "Change navigation scrolls the actual document");
            preview.ShowComparison(files[0].File.OriginalText, files[0].File.ProposedText);
            var selected = cpu.SelectedPlan!; string backup = new SourceWorkspace().Apply(selected);
            check(File.ReadAllText(Path.Combine(source, "Beta.cpp")) == b, "Apply leaves unselected file byte content unchanged");
            check(File.ReadAllText(Path.Combine(source, "Alpha.cpp")).Contains("TRACE_CPUPROFILER"), "Apply changes selected file");
            check(Directory.GetFiles(backup, "*.cpp", SearchOption.AllDirectories).Length == 1, "Backup contains selected file only");

            ((TextBox)events.FindName("SourcePath")).Text = source; Click(events, "AnalyzeButton"); Until(() => !events.IsBusy);
            var grid = (DataGrid)events.FindName("CandidateList");
            var row = ((IEnumerable<CandidateRow>)grid.ItemsSource).Single();
            var eventPreview = (CodeChangePreview)events.FindName("PreviewText");
            check(eventPreview.Text.Contains("TRACE_COUNTER_ADD("), "Analyze automatically previews recommended ADD");
            check(row.QuantityEnabled && row.MeaningConfirmed && row.QuantityValue == "NumBytes" && row.QuantityUnit == "bytes", "Recommendation automatically counts input quantity");
            var eventList = (DataGrid)events.FindName("FileList");
            var eventFile = ((IEnumerable<EventFileRow>)eventList.ItemsSource).Single(f => f.CanSelect);
            eventFile.Selected = true; Until(() => !events.IsPreviewPending);
            check(((Button)events.FindName("ApplyButton")).IsEnabled, "Automatic input quantity is ready with file selection");
            row.MeaningConfirmed = false; Until(() => !events.IsPreviewPending);
            check(!((Button)events.FindName("ApplyButton")).IsEnabled, "Unconfirmed recommendation cannot be applied");
            row.MeaningConfirmed = true; Until(() => !events.IsPreviewPending);
            check(((Button)events.FindName("ApplyButton")).IsEnabled, "Confirmed selected recommendation enables apply");
            check(eventPreview.Text.Contains("\t\tTRACE_COUNTER_ADD"), "Recommended ADD follows tab indentation");
            if (screenshots is not null) Program.Screenshot(events, Path.Combine(screenshots, "workflow-events.png"), 1280, 900);
            row.QuantityValue = "Unsafe()"; Until(() => !events.IsPreviewPending);
            check(!((Button)events.FindName("ApplyButton")).IsEnabled && ((TextBlock)events.FindName("StatusText")).Text.Contains("更新できません"), "Invalid editing reports inline and cannot apply");
            row.QuantityValue = "NumBytes"; Until(() => !events.IsPreviewPending);
            check(!row.MeaningConfirmed && !((Button)events.FindName("ApplyButton")).IsEnabled, "Editing quantity requires renewed confirmation");
            check(File.ReadAllText(Path.Combine(source, "Beta.cpp")) == b, "Event preview preserves source");
            Click(events, "ClearVisibleButton"); Until(() => !events.IsPreviewPending);
            check(!eventFile.Selected && !((Button)events.FindName("ApplyButton")).IsEnabled, "Clear visible disables file apply while retaining reference preview");
            row.QuantityEnabled = false; Until(() => !events.IsPreviewPending);
            Click(events, "SelectAllButton"); Until(() => !events.IsPreviewPending);
            check(eventFile.Selected && events.SelectedPlan?.ChangedFiles == 1, "Global file selection skips files without candidates");
            eventList.SelectedItems.Clear(); eventList.SelectedItems.Add(eventFile); Click(events, "ClearRowsButton"); Until(() => !events.IsPreviewPending);
            check(!eventFile.Selected, "Clear highlighted event file");
            Space(events, eventList); Until(() => !events.IsPreviewPending); check(eventFile.Selected, "Space selects highlighted event file");
            Click(events, "ClearAllButton"); Until(() => !events.IsPreviewPending); check(!eventFile.Selected, "Global event clear");
            eventFile.Selected = true; Until(() => !events.IsPreviewPending);
            string alphaBefore = File.ReadAllText(Path.Combine(source, "Alpha.cpp"));
            string eventBackup = new SourceWorkspace().Apply(events.SelectedPlan!);
            check(File.ReadAllText(Path.Combine(source, "Alpha.cpp")) == alphaBefore && File.ReadAllText(Path.Combine(source, "Beta.cpp")).Contains("TRACE_COUNTER_INCREMENT"), "Event apply modifies only the checked file");
            check(Directory.GetFiles(eventBackup, "*.cpp", SearchOption.AllDirectories).Length == 1, "Event backup contains one selected file");
        }
        finally { cpu.Close(); events.Close(); }
    }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Space(Window window, DataGrid grid) => grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static void Until(Func<bool> condition)
    {
        var time = Stopwatch.StartNew();
        while (!condition() && time.Elapsed < TimeSpan.FromSeconds(30)) { Pump(); Thread.Sleep(5); }
        if (!condition()) throw new Exception("Workflow timed out");
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
}
