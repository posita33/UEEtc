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
        string editorDirectory = Path.Combine(root, "Editor 日本語 Space"); Directory.CreateDirectory(editorDirectory);
        string editorFile = Path.Combine(editorDirectory, "UnrealEditor-Cmd.EXE"); File.WriteAllBytes(editorFile, []);
        string otherExe = Path.Combine(editorDirectory, "Other.exe"); File.WriteAllBytes(otherExe, []);
        string outputFolder = Path.Combine(root, "BlueprintReports"); Directory.CreateDirectory(outputFolder);
        string changedOutput = Path.Combine(root, "OtherReports"); Directory.CreateDirectory(changedOutput);
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
            var projectInput = (TextBox)window.FindName("ProjectPath");
            var editorInput = (TextBox)window.FindName("EditorPath");
            var outputInput = (TextBox)window.FindName("OutputPath");
            projectInput.Clear();
            foreach (var item in new[] { (Input: projectInput, Path: projectFile), (Input: editorInput, Path: editorFile), (Input: outputInput, Path: outputFolder) })
            {
                var over = InputAssistanceTests.Drag(item.Input, [item.Path], UIElement.PreviewDragOverEvent);
                var dropped = InputAssistanceTests.Drag(item.Input, [item.Path], UIElement.PreviewDropEvent);
                check(over.Handled && over.Effects == DragDropEffects.Copy && dropped.Effects == DragDropEffects.Copy && item.Input.Text == item.Path, "Blueprint accepts the matching dropped path for " + item.Input.Name);
            }
            check(service.Last is null, "Dropping paths never starts Blueprint analysis automatically");
            foreach (var item in new[] { (Input: projectInput, Invalid: asset), (Input: projectInput, Invalid: project),
                (Input: editorInput, Invalid: otherExe), (Input: editorInput, Invalid: editorDirectory), (Input: outputInput, Invalid: asset) })
            {
                string original = item.Input.Text;
                var rejected = InputAssistanceTests.Drag(item.Input, [item.Invalid], UIElement.PreviewDropEvent);
                check(rejected.Effects == DragDropEffects.None && item.Input.Text == original, "Blueprint rejects a mismatched dropped path for " + item.Input.Name);
            }
            foreach (var item in new[] { (Input: projectInput, Path: projectFile), (Input: editorInput, Path: editorFile), (Input: outputInput, Path: outputFolder) })
            {
                var rejected = InputAssistanceTests.Drag(item.Input, [item.Path, item.Path], UIElement.PreviewDropEvent);
                check(rejected.Effects == DragDropEffects.None && item.Input.Text == item.Path, "Blueprint rejects multiple dropped paths for " + item.Input.Name);
            }
            Click(window, "AnalyzeButton");
            check(window.IsBusy && !input.IsEnabled && ((Button)window.FindName("CancelButton")).IsEnabled, "Blueprint analysis locks inputs and enables cancel");
            check(!((Button)window.FindName("ConvertButton")).IsEnabled, "C++ conversion cannot start during analysis");
            foreach (var item in new[] { (Input: input, Path: asset), (Input: projectInput, Path: projectFile),
                (Input: editorInput, Path: editorFile), (Input: outputInput, Path: changedOutput) })
            {
                var target = item.Input;
                string original = target.Text;
                var rejected = InputAssistanceTests.Drag(target, [item.Path], UIElement.PreviewDropEvent);
                check(rejected.Effects == DragDropEffects.None && target.Text == original, "Blueprint rejects path drops during analysis for " + target.Name);
            }
            Until(() => !window.IsBusy);
            check(window.Result is not null && ((TextBox)window.FindName("ReportText")).Text.Contains("Blueprint fixture report"), "Blueprint report is created and displayed");
            check(((TextBox)window.FindName("DiagnosticsText")).Text.Contains("r.Mobile.VirtualTextures"), "Blueprint completed export retains startup warning in diagnostics");
            check(service.Last is { RemoveLayout: true } && service.Last.AssetPath == asset, "Blueprint layout and asset settings reach independent service");
            check(((Button)window.FindName("OpenOutputButton")).IsEnabled && File.ReadAllBytes(asset).SequenceEqual(new byte[] { 1, 2, 3 }), "Blueprint report enables opening output without modifying asset bytes");
            if (screenshots is not null) Program.Screenshot(window, Path.Combine(screenshots, "blueprint-analysis.png"), 1180, 860);
            InputAssistanceTests.Drag(outputInput, [changedOutput], UIElement.PreviewDropEvent);
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
            Click(window, "ConvertButton");
            check(window.IsBusy && !((Button)window.FindName("AnalyzeButton")).IsEnabled, "C++ conversion locks the shared paths and analysis button");
            Until(() => !window.IsBusy);
            check(((TextBox)window.FindName("CppText")).Text.Contains("#error") && ((TextBox)window.FindName("ReportText")).Text.Contains("未対応"), "C++ draft preview exposes unsupported fixture data");
            check(((TextBlock)window.FindName("StatusText")).Text.Contains("下書き") && ((Button)window.FindName("OpenOutputButton")).IsEnabled, "C++ draft is distinguished from a complete conversion and can be opened");
            input.Text += "x";
            check(((TextBox)window.FindName("CppText")).Text.Length == 0, "Changing a conversion path invalidates C++ preview");
            input.Text = asset;
            service.WaitForCancellation = true;
            Click(window, "ConvertButton"); Click(window, "CancelButton"); Until(() => !window.IsBusy);
            check(window.Result is null && ((TextBox)window.FindName("CppText")).Text.Length == 0 && ((TextBlock)window.FindName("StatusText")).Text.Contains("中止"), "C++ conversion cancellation leaves no completed preview");
            service.WaitForCancellation = false;
        }
        finally { window.Close(); }
        var restarted = new BlueprintAnalysisWindow(new InputSettingsStore(settings.Path), service);
        try
        {
            check(((TextBox)restarted.FindName("AssetPath")).Text == asset && ((TextBox)restarted.FindName("ProjectPath")).Text == projectFile, "Blueprint input paths survive settings reload");
            check(((TextBox)restarted.FindName("EditorPath")).Text == editorFile && ((TextBox)restarted.FindName("OutputPath")).Text == changedOutput, "Blueprint dropped Editor and output paths survive settings reload");
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
            return new(folder, path, Path.Combine(folder, "analysis.json"), new(options.AssetPath, "/Game/BP_Test", "Fixture", "5.8", options.RemoveLayout, [], ["r.Mobile.VirtualTextures startup ensure; export completed"]));
        }
    }
}
