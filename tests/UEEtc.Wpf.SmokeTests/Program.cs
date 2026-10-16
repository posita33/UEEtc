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

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-ui-tests")) + Path.DirectorySeparatorChar;
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        const string source = "#include \"Fixture.h\"\nvoid F::Work() { if (bCacheHit) { Hit(); } else { Miss(); } }\n";
        File.WriteAllText(Path.Combine(root, "Fixture.cpp"), source, new UTF8Encoding(false));
        var home = new MainWindow();
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
            var cpuFile = (FileAnalysis)((DataGrid)cpu.FindName("FileList")).SelectedItem;
            Assert(cpuFile.AddedCpuScopes == 1 && cpuFile.AddedCallCounters == 0, "CPU adds scopes only");
            Assert(!cpuFile.ProposedText.Contains("TRACE_COUNTER_INCREMENT"), "CPU never inserts event counts");
            Assert(FindButton(cpu, "ApplyButton").IsEnabled, "CPU valid preview can be applied");
            cpuPath.Text += "-changed";
            Assert(!FindButton(cpu, "ApplyButton").IsEnabled && !FindButton(cpu, "ExportButton").IsEnabled, "CPU path invalidates preview");
            FindButton(home, "EventFeatureButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var events = home.OwnedWindows.OfType<EventCountersWindow>().Single();
            Assert(events.Owner == home && events != (Window)cpu, "Events open in independent window");
            var eventPath = (TextBox)events.FindName("SourcePath"); var analyze = FindButton(events, "AnalyzeButton");
            eventPath.Text = root; analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!eventPath.IsEnabled, "Events lock inputs during analysis"); Wait(analyze);
            var list = (DataGrid)events.FindName("CandidateList");
            var rows = ((IEnumerable<CandidateRow>)list.ItemsSource).ToArray();
            Assert(rows.Length == 2 && rows.All(r => !r.Selected), "Two branch candidates, none selected");
            Assert(!FindButton(events, "ApplyButton").IsEnabled, "Discovery cannot be applied");
            Assert(((TextBox)events.FindName("ContextText")).Text.Contains("bCacheHit"), "Candidate context includes condition");
            rows[0].Selected = true; rows[0].CounterName = "VerifiedCacheHitCount"; rows[0].DisplayName = "Cache/VerifiedHits";
            var previewButton = FindButton(events, "PreviewButton"); previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(previewButton);
            string preview = ((TextBox)events.FindName("PreviewText")).Text;
            Assert(preview.Contains("TRACE_COUNTER_INCREMENT(VerifiedCacheHitCount)"), "Selected name reaches preview");
            Assert(preview.Split("TRACE_COUNTER_INCREMENT").Length == 2, "Only selected branch added");
            Assert(!preview.Contains("CpuProfilerTrace.h"), "Events do not add CPU tracing");
            Assert(FindButton(events, "ApplyButton").IsEnabled && FindButton(events, "ExportButton").IsEnabled, "Reviewed plan enables apply/export");
            ((TextBox)events.FindName("FilterText")).Text = "does-not-match";
            Assert(list.Items.Count == 0 && rows[0].Selected, "Filtering does not change selection");
            ((TextBox)events.FindName("FilterText")).Text = "";
            Assert(list.Items.Count == 2, "Clearing filter restores candidates");
            if (args.Length > 0)
            {
                Directory.CreateDirectory(args[0]);
                Screenshot(home, Path.Combine(args[0], "home.png"), 800, 450);
                Screenshot(cpu, Path.Combine(args[0], "cpu.png"), 1200, 750);
                Screenshot(events, Path.Combine(args[0], "events.png"), 1240, 830);
            }
            rows[0].CounterName = "ChangedName";
            Assert(!FindButton(events, "ApplyButton").IsEnabled && !FindButton(events, "ExportButton").IsEnabled, "Name edit invalidates preview");
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(previewButton);
            rows[0].Selected = false;
            Assert(!FindButton(events, "ApplyButton").IsEnabled, "Selection edit invalidates preview");
            eventPath.Text += "-changed";
            Assert(list.ItemsSource is null && !previewButton.IsEnabled, "Source edit clears event candidates");
            Assert(File.ReadAllText(Path.Combine(root, "Fixture.cpp")) == source, "All previews preserve original source");
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
    private static void Screenshot(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
