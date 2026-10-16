using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UEEtc.Core;
using UEEtc.Wpf;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = Path.Combine(Path.GetTempPath(), "UEEtc-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string source = "#include \"Fixture.h\"\nvoid F::Work() { Run(); }\n";
        File.WriteAllText(Path.Combine(root, "Fixture.cpp"), source, new UTF8Encoding(false));
        var window = new MainWindow();
        try
        {
            var path = (TextBox)window.FindName("SourcePath");
            var analyze = (Button)window.FindName("AnalyzeButton");
            var apply = (Button)window.FindName("ApplyButton");
            var export = (Button)window.FindName("ExportButton");
            var list = (DataGrid)window.FindName("FileList");
            var preview = (TextBox)window.FindName("PreviewText");
            var cpu = (CheckBox)window.FindName("CpuOption");
            var counter = (CheckBox)window.FindName("CounterOption");
            Assert(cpu.IsChecked == true && counter.IsChecked == false, "Default is CPU only");
            Assert(!apply.IsEnabled && !export.IsEnabled, "Apply and export disabled before analysis");
            path.Text = root;
            analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!path.IsEnabled && !analyze.IsEnabled, "Inputs locked during analysis");
            WaitForAnalysis(analyze);
            Assert(analyze.IsEnabled && list.Items.Count == 1, "Asynchronous analysis completed");
            Assert(list.SelectedItem is FileAnalysis { AddedFunctions: 1 }, "File and function selected");
            Assert(preview.Text.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR") && !preview.Text.Contains("TRACE_COUNTER_INCREMENT"), "Default preview contains CPU only");
            Assert(list.SelectedItem is FileAnalysis { AddedCpuScopes: 1, AddedCallCounters: 0 }, "Default counts reflect CPU only");
            Assert(apply.IsEnabled && export.IsEnabled, "Actions enabled for valid plan");
            Assert(File.ReadAllText(Path.Combine(root, "Fixture.cpp")) == source, "Preview leaves input unchanged");

            if (args.Length > 0)
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1180, 720));
                content.Arrange(new Rect(0, 0, 1180, 720));
                content.UpdateLayout();
                var image = new RenderTargetBitmap(1180, 720, 96, 96, PixelFormats.Pbgra32);
                image.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(args[0]);
                encoder.Save(stream);
            }
            counter.IsChecked = true;
            counter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert(!apply.IsEnabled && list.Items.Count == 0, "Changing mode invalidates old plan");
            analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForAnalysis(analyze);
            Assert(list.SelectedItem is FileAnalysis { AddedCpuScopes: 1, AddedCallCounters: 1 }, "Explicit both adds both kinds");
            Assert(preview.Text.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR") && preview.Text.Contains("TRACE_COUNTER_INCREMENT(F_Work_CallCount)"), "Both preview retains readable name");
            cpu.IsChecked = false;
            cpu.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForAnalysis(analyze);
            Assert(list.SelectedItem is FileAnalysis { AddedCpuScopes: 0, AddedCallCounters: 1 }, "Counter-only selection works");
            Assert(!preview.Text.Contains("CpuProfilerTrace.h") && preview.Text.Contains("TRACE_COUNTER_INCREMENT"), "Counter-only needs no CPU header");
            Assert(File.ReadAllText(Path.Combine(root, "Fixture.cpp")) == source, "All three modes leave source unchanged");
            path.Text += "-changed";
            Assert(!apply.IsEnabled && !export.IsEnabled && list.Items.Count == 0, "Changing path invalidates preview");
            Console.WriteLine("WPF smoke: 16 checks passed (default CPU, explicit both, counter-only, invalidation, source unchanged).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            window.Close();
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc-ui-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary path");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WaitForAnalysis(Button analyze)
    {
        var timer = Stopwatch.StartNew();
        while (!analyze.IsEnabled && timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Assert(analyze.IsEnabled, "Analysis completed within timeout");
    }
}
