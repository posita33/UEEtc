using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UEEtc.Core.Plans;
using UEEtc.Wpf;
using UEEtc.Wpf.Features.CpuTracing;
using UEEtc.Wpf.Features.EventCounters;
using UEEtc.Wpf.InputAssistance;
using UEEtc.Wpf.Preview;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-ui-tests")) + Path.DirectorySeparatorChar;
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        const string source = "#include \"Fixture.h\"\nvoid F::Work() { Super::Work(); if (bCacheHit) { Hit(); } else { Miss(); } }\n";
        File.WriteAllText(Path.Combine(root, "Fixture.cpp"), source, new UTF8Encoding(false));
        var settings = new InputSettingsStore(Path.Combine(root, "input-settings.json"));
        var home = new MainWindow(settings);
        try
        {
            home.Show();
            FindButton(home, "CpuFeatureButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var cpu = home.OwnedWindows.OfType<CpuTracingWindow>().Single();
            Assert(cpu.Owner == home, "CPU opens from launcher");
            Assert(cpu.FindName("CounterOption") is null, "CPU screen contains no entry-counter switch");
            Assert(!FindButton(cpu, "ApplyButton").IsEnabled, "CPU apply disabled before analysis");
            var cpuPath = (TextBox)cpu.FindName("SourcePath"); var cpuAnalyze = FindButton(cpu, "AnalyzeButton");
            cpuPath.Text = root; cpuAnalyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!cpuPath.IsEnabled, "CPU locks inputs"); Wait(cpuAnalyze);
            var cpuFile = ((SelectableFile)((DataGrid)cpu.FindName("FileList")).SelectedItem).File;
            Assert(cpuFile.AddedCpuScopes == 0, "Focused CPU skips this tiny branch wrapper by default");
            Assert(!FindButton(cpu, "ApplyButton").IsEnabled, "No CPU changes keeps apply disabled");
            var allFunctions = (CheckBox)cpu.FindName("AllFunctionsOption");
            allFunctions.IsChecked = true; allFunctions.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            cpuAnalyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(cpuAnalyze);
            var cpuRow = (SelectableFile)((DataGrid)cpu.FindName("FileList")).SelectedItem;
            cpuFile = cpuRow.File;
            Assert(cpuFile.AddedCpuScopes == 1 && cpuFile.AddedCallCounters == 0, "CPU adds scopes only");
            Assert(!cpuFile.ProposedText.Contains("TRACE_COUNTER_INCREMENT"), "CPU never inserts event counts");
            Assert(cpuFile.ProposedText.Contains("TRACE_CPUPROFILER_EVENT_SCOPE(F Work)"), "Auto CPU label is class and function as plain text");
            Assert(cpuFile.ProposedText.IndexOf("Super::Work();", StringComparison.Ordinal) < cpuFile.ProposedText.IndexOf("TRACE_CPUPROFILER", StringComparison.Ordinal), "CPU starts after Super");
            Assert(cpuRow.Selected && FindButton(cpu, "ApplyButton").IsEnabled, "CPU changes are selected and applicable immediately after analysis");
            cpuRow.Selected = false;
            Assert(!FindButton(cpu, "ApplyButton").IsEnabled, "Clearing CPU selection disables apply");
            cpuRow.Selected = true;
            Assert(FindButton(cpu, "ApplyButton").IsEnabled, "Selected CPU file can be applied");
            ((ComboBox)cpu.FindName("CpuMacroOption")).SelectedIndex = 2;
            Assert(!FindButton(cpu, "ApplyButton").IsEnabled, "CPU macro choice invalidates plan");
            cpuAnalyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(cpuAnalyze);
            Assert(((CodeChangePreview)cpu.FindName("PreviewText")).Text.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR(\"F Work\")"), "Explicit STR preserves the same fixed label");
            cpuPath.Text += "-changed";
            Assert(!FindButton(cpu, "ApplyButton").IsEnabled && !FindButton(cpu, "ExportButton").IsEnabled, "CPU path invalidates preview");
            FindButton(home, "EventFeatureButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var events = home.OwnedWindows.OfType<EventCountersWindow>().Single();
            Assert(events.Owner == home && events != (Window)cpu, "Events open in independent window");
            var eventPath = (TextBox)events.FindName("SourcePath"); var analyze = FindButton(events, "AnalyzeButton");
            eventPath.Text = root;
            var oldCandidate = new UEEtc.Core.Features.EventCounters.EventCounterService().Analyze(root).Candidates[0];
            string oldScope = "EventCounters.Edits." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))) + "." + oldCandidate.Id + ".Display";
            settings.Set(oldScope, "UEEtc/Events/" + oldCandidate.RelativePath + "/" + oldCandidate.Function + "/If1");
            analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!eventPath.IsEnabled, "Events lock inputs during analysis"); Wait(analyze);
            var list = (DataGrid)events.FindName("CandidateList");
            var eventFiles = (DataGrid)events.FindName("FileList");
            var eventFile = ((IEnumerable<EventFileRow>)eventFiles.ItemsSource).Single();
            var rows = ((IEnumerable<CandidateRow>)list.ItemsSource).ToArray();
            Assert(rows[0].DisplayName == "F Work if1", "Saved generated path label migrates to the short default");
            Assert(rows.Length == 2 && rows.All(r => r.Selected) && eventFile.Selected, "Two included branches in an automatically selected file");
            Assert(ReferenceEquals(((FrameworkElement)events.FindName("QuantityEditor")).DataContext, rows[0]), "Quantity editor binds the highlighted candidate");
            Assert(FindButton(events, "ApplyButton").IsEnabled, "Valid event preview can be applied immediately after analysis");
            Assert(((TextBox)events.FindName("ContextText")).Text.Contains("bCacheHit"), "Candidate context includes condition");
            eventFile.Selected = true; rows[1].Selected = false; rows[0].CounterName = "VerifiedCacheHitCount"; rows[0].DisplayName = "Cache/VerifiedHits";
            var previewButton = FindButton(events, "PreviewButton"); previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(previewButton);
            string preview = ((CodeChangePreview)events.FindName("PreviewText")).Text;
            Assert(preview.Contains("TRACE_COUNTER_INCREMENT(VerifiedCacheHitCount)"), "Selected name reaches preview");
            Assert(preview.Split("TRACE_COUNTER_INCREMENT").Length == 2, "Only selected branch added");
            Assert(!preview.Contains("CpuProfilerTrace.h"), "Events do not add CPU tracing");
            Assert(FindButton(events, "ApplyButton").IsEnabled && FindButton(events, "ExportButton").IsEnabled, "Reviewed plan enables apply/export");
            int inputExceptions = 0;
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe = (_, e) => { if (e.Exception is ArgumentException && e.Exception.StackTrace?.Contains("EventCounterService", StringComparison.Ordinal) == true) inputExceptions++; };
            AppDomain.CurrentDomain.FirstChanceException += observe;
            try
            {
                rows[0].QuantityEnabled = true;
                previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(previewButton);
                Assert(!FindButton(events, "ApplyButton").IsEnabled && ((TextBlock)events.FindName("StatusText")).Text.Contains("単位"), "Enabling blank ADD requests missing input inline");
                Assert(inputExceptions == 0, "Blank ADD never throws an ArgumentException, even before catch");
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
            rows[0].QuantityName = "TransferredItems";
            rows[0].QuantityDisplay = "Cache/Items"; rows[0].QuantityValue = "4";
            rows[0].QuantityUnit = "items"; rows[0].MeaningConfirmed = true;
            Assert(((ComboBox)events.FindName("QuantityValueInput")).Text == "4", "Quantity value binding reflects edited model");
            Assert(!FindButton(events, "ApplyButton").IsEnabled, "Quantity edit invalidates plan");
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(previewButton);
            Assert(((CodeChangePreview)events.FindName("PreviewText")).Text.Contains("TRACE_COUNTER_ADD(TransferredItems, 4)"), "Quantity reaches generated preview");
            ((TextBox)events.FindName("FilterText")).Text = "does-not-match";
            Assert(eventFiles.Items.Count == 0 && eventFile.Selected, "Filtering files does not change selection");
            ((TextBox)events.FindName("FilterText")).Text = "";
            Assert(eventFiles.Items.Count == 1, "Clearing filter restores file row");
            eventFiles.SelectedItem = eventFile;
            if (args.Length > 0)
            {
                Directory.CreateDirectory(args[0]);
                Screenshot(home, Path.Combine(args[0], "home.png"), 800, 450);
                Screenshot(cpu, Path.Combine(args[0], "cpu.png"), 1200, 750);
                Screenshot(events, Path.Combine(args[0], "events.png"), 1240, 830);
                ((TabItem)events.FindName("QuantityTab")).IsSelected = true;
                Screenshot(events, Path.Combine(args[0], "quantity.png"), 1240, 830);
            }
            rows[0].CounterName = "ChangedName";
            Assert(!FindButton(events, "ApplyButton").IsEnabled && !FindButton(events, "ExportButton").IsEnabled, "Name edit invalidates preview");
            settings.Flush();
            var restarted = new EventCountersWindow(new InputSettingsStore(settings.Path));
            try
            {
                Assert(((TextBox)restarted.FindName("SourcePath")).Text == root, "Event source survives application restart");
                Assert(!FindButton(restarted, "ApplyButton").IsEnabled, "Restart still requires fresh analysis before apply");
                var restartAnalyze = FindButton(restarted, "AnalyzeButton");
                restartAnalyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(restartAnalyze);
                var restartedRows = ((IEnumerable<CandidateRow>)((DataGrid)restarted.FindName("CandidateList")).ItemsSource).ToArray();
                Assert(restartedRows[0].CounterName == "ChangedName" && restartedRows[0].DisplayName == "Cache/VerifiedHits", "Edited candidate names are restored");
                Assert(((IEnumerable<EventFileRow>)((DataGrid)restarted.FindName("FileList")).ItemsSource).All(f => f.Selected) && FindButton(restarted, "ApplyButton").IsEnabled, "Fresh analysis selects current changes after restart");
                Assert(restartedRows.All(r => !r.QuantityEnabled && !r.MeaningConfirmed), "Restart requires fresh quantity confirmation");
            }
            finally { restarted.Close(); }
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(previewButton);
            rows[0].Selected = false;
            Assert(!FindButton(events, "ApplyButton").IsEnabled, "Selection edit invalidates preview");
            eventPath.Text += "-changed";
            Assert(list.ItemsSource is null && !previewButton.IsEnabled, "Source edit clears event candidates");
            Assert(File.ReadAllText(Path.Combine(root, "Fixture.cpp")) == source, "All previews preserve original source");
            InputAssistanceTests.Run(root, Assert);
            ApplyAvailabilityTests.Run(root, Assert, args.FirstOrDefault());
            WorkflowTests.Run(root, Assert, args.FirstOrDefault());
            CombinedWorkflowTests.Run(home, root, Assert, args.FirstOrDefault());
            Console.WriteLine($"WPF smoke: {checks} checks passed (launcher, separate windows, selection, naming, filtering, invalidation, source unchanged).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (Window child in home.OwnedWindows.Cast<Window>().ToArray()) child.Close(); home.Close();
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary path");
            Directory.Delete(root, true);
        }
    }
    private static Button FindButton(Window window, string name) => (Button)window.FindName(name);
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Wait(Button action)
    {
        var timer = Stopwatch.StartNew();
        while (!action.IsEnabled && timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
        Assert(action.IsEnabled, "Operation completed within timeout");
    }
    internal static void Screenshot(Window window, string path, int width, int height, Action? beforeRender = null)
    {
        var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        beforeRender?.Invoke(); content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
