using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UEEtc.Blueprint;
using UEEtc.Wpf;
using UEEtc.Wpf.Features.BlueprintAnalysis;
using UEEtc.Wpf.InputAssistance;

internal static class BlueprintWorkflowTests
{
    public static void Run(MainWindow home, string root, Action<bool, string> check, string? screenshots)
    {
        Click(home, "BlueprintFeatureButton");
        var launched = home.OwnedWindows.OfType<BlueprintAnalysisWindow>().Single();
        check(launched.Owner == home && launched.FindName("AssetPath") is TextBox, "Blueprint feature opens its own window from launcher");
        launched.Close();
        string project = Path.Combine(root, "BlueprintProject"); Directory.CreateDirectory(Path.Combine(project, "Content"));
        string projectFile = Path.Combine(project, "Fixture.uproject"); File.WriteAllText(projectFile, "{\"EngineAssociation\":\"5.8\"}");
        string asset = Path.Combine(project, "Content", "BP_Test.uasset"); File.WriteAllBytes(asset, [1, 2, 3]);
        var settings = new InputSettingsStore(Path.Combine(root, "blueprint-settings.json"));
        var service = new FakeService(root);
        var window = new BlueprintAnalysisWindow(settings, service) { Owner = home };
        try
        {
            window.Show();
            var input = (TextBox)window.FindName("AssetPath");
            var drop = InputAssistanceTests.Drag(input, [asset], UIElement.PreviewDropEvent);
            check(input.Text == asset && drop.Handled && drop.Effects == DragDropEffects.Copy, "Blueprint real file-drop event fills the asset path");
            check(((TextBox)window.FindName("ProjectPath")).Text == projectFile, "Blueprint drop automatically finds the owning uproject");
            check(((CheckBox)window.FindName("RemoveLayoutOption")).IsChecked == true, "Blueprint excludes layout by default");
            check(BlueprintAnalysisWindow.DropAsset(new DataObject(DataFormats.FileDrop, new[] { asset, asset })) is null &&
                BlueprintAnalysisWindow.DropAsset(new DataObject(DataFormats.FileDrop, new[] { projectFile })) is null, "Blueprint drop rejects multiple files and non-uasset input");
            ((TextBox)window.FindName("OutputPath")).Text = Path.Combine(root, "BlueprintReports");
            Click(window, "AnalyzeButton");
            check(window.IsBusy && !input.IsEnabled && ((Button)window.FindName("CancelButton")).IsEnabled, "Blueprint analysis locks inputs and enables cancel");
            Until(() => !window.IsBusy);
            check(window.Result is not null && ((TextBox)window.FindName("ReportText")).Text.Contains("Blueprint fixture report"), "Blueprint report is created and displayed");
            check(service.Last is { RemoveLayout: true } && service.Last.AssetPath == asset, "Blueprint layout and asset settings reach independent service");
            check(((Button)window.FindName("OpenOutputButton")).IsEnabled && File.ReadAllBytes(asset).SequenceEqual(new byte[] { 1, 2, 3 }), "Blueprint report enables opening output without modifying asset bytes");
            if (screenshots is not null) Program.Screenshot(window, Path.Combine(screenshots, "blueprint-analysis.png"), 1180, 860);
            ((TextBox)window.FindName("OutputPath")).Text = Path.Combine(root, "OtherReports");
            check(window.Result is null && !((Button)window.FindName("OpenOutputButton")).IsEnabled, "Blueprint changed settings invalidate the old result");
            service.Fail = true; Click(window, "AnalyzeButton"); Until(() => !window.IsBusy);
            check(window.Result is null && ((TextBox)window.FindName("DiagnosticsText")).Text.Contains("Fixture failure"), "Blueprint errors appear inline without enabling output");
            service.Fail = false; service.WaitForCancellation = true;
            Click(window, "AnalyzeButton"); Click(window, "CancelButton"); Until(() => !window.IsBusy);
            check(((TextBlock)window.FindName("StatusText")).Text.Contains("中止") && window.Result is null, "Blueprint cancellation clears busy state without a completed report");
            service.WaitForCancellation = false;
            ((CheckBox)window.FindName("RemoveLayoutOption")).IsChecked = false;
            Click(window, "AnalyzeButton"); Until(() => !window.IsBusy);
            check(service.Last is { RemoveLayout: false }, "Blueprint keep-layout choice reaches service");
        }
        finally { window.Close(); }
        var restarted = new BlueprintAnalysisWindow(new InputSettingsStore(settings.Path), service);
        try
        {
            check(((TextBox)restarted.FindName("AssetPath")).Text == asset && ((TextBox)restarted.FindName("ProjectPath")).Text == projectFile, "Blueprint input paths survive settings reload");
            check(restarted.Result is null && ((CheckBox)restarted.FindName("RemoveLayoutOption")).IsChecked == true, "Blueprint restart requires analysis and restores safe layout default");
        }
        finally { restarted.Close(); }
    }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Until(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Blueprint UI test timed out");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(10);
        }
    }
    private sealed class FakeService(string root) : IBlueprintAnalysisService
    {
        public BlueprintAnalysisOptions? Last { get; private set; }
        public bool Fail { get; set; }
        public bool WaitForCancellation { get; set; }
        public async Task<BlueprintAnalysisResult> AnalyzeAsync(BlueprintAnalysisOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Last = options;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.Delay(40, cancellationToken);
            if (Fail) throw new IOException("Fixture failure");
            string folder = Path.Combine(root, "BlueprintGenerated", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "analysis.md");
            await File.WriteAllTextAsync(path, "# Blueprint fixture report\n\nGraph / Nodes / Pins / Defaults", cancellationToken);
            return new(folder, path, Path.Combine(folder, "analysis.json"), new(options.AssetPath, "/Game/BP_Test", "Fixture", "5.8", options.RemoveLayout, [], []));
        }
    }
}
