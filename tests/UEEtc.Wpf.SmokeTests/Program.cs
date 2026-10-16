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
            Assert(!apply.IsEnabled && !export.IsEnabled, "Apply and export disabled before analysis");
            path.Text = root;
            analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!path.IsEnabled && !analyze.IsEnabled, "Inputs locked during analysis");
            var timer = Stopwatch.StartNew();
            while (!analyze.IsEnabled && timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(5);
            }
            Assert(analyze.IsEnabled && list.Items.Count == 1, "Asynchronous analysis completed");
            Assert(list.SelectedItem is FileAnalysis { AddedFunctions: 1 }, "File and function selected");
            Assert(preview.Text.Contains("TRACE_CPUPROFILER_EVENT_SCOPE_STR") && preview.Text.Contains("TRACE_COUNTER_INCREMENT"), "Preview contains both macros");
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
            path.Text += "-changed";
            Assert(!apply.IsEnabled && !export.IsEnabled && list.Items.Count == 0, "Changing path invalidates preview");
            Console.WriteLine("WPF smoke: 8 checks passed (offscreen, source unchanged).");
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
}
