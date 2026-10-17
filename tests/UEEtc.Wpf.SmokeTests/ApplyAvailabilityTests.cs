using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using UEEtc.Core.Plans;
using UEEtc.Core.Source;
using UEEtc.Wpf.Features.CombinedTracing;
using UEEtc.Wpf.Features.CpuTracing;
using UEEtc.Wpf.Features.EventCounters;
using UEEtc.Wpf.InputAssistance;
using UEEtc.Wpf.Workflow;

internal static class ApplyAvailabilityTests
{
    public static void Run(string root, Action<bool, string> check, string? screenshots)
    {
        foreach (string feature in new[] { "CPU", "Events", "Combined" })
        {
            string source = Path.Combine(root, "apply-diagnostics-" + feature);
            Directory.CreateDirectory(source);
            const string good = "#include \"Fixture.h\"\nvoid F::Work() { if (Ready) { for (int i=0;i<4;++i) Use(); } }";
            File.WriteAllText(Path.Combine(source, "Good.cpp"), good);
            byte[] unsupported = [0xff, 0xfe, 0, 0, 65, 0, 0, 0];
            File.WriteAllBytes(Path.Combine(source, "Unsupported.cpp"), unsupported);
            var settings = new InputSettingsStore(Path.Combine(source, "settings.json"));
            Window window = Create(settings);
            string customBackup = Path.Combine(root, "Backups", feature);
            try
            {
                window.Show();
                ((TextBox)window.FindName("SourcePath")).Text = source;
                ((Button)window.FindName("AnalyzeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => !Busy());
                check(((TextBox)window.FindName("DiagnosticsText")).Text.Contains("UTF-32"), feature + " retains diagnostics for excluded file");
                check(Selected() is { ChangedFiles: 1, DiagnosticCount: 0 }, feature + " selected plan excludes unsupported file");
                check(((Button)window.FindName("ApplyButton")).IsEnabled, feature + " checked valid file remains applicable despite excluded-file diagnosis");
                var hint = (TextBlock)window.FindName("ApplyStatusText");
                hint.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                check(hint.Text.Contains("適用可能") && hint.Text.Contains("対象外ファイル"), feature + " always displays applicable state and excluded diagnostics");
                var backupLocation = (BackupLocationPicker)window.FindName("BackupLocation");
                var backupInput = (TextBox)backupLocation.FindName("PathInput");
                check(backupLocation.BackupRoot == BackupStorage.DefaultRoot, feature + " default backup folder is outside source");
                if (feature != "Events")
                {
                    var editor = (CpuTraceSettingsEditor)window.FindName("CpuTraceSettings");
                    check(window.FindName("CpuMacroOption") is null && editor.Settings.MacroName == "TRACE_CPUPROFILER_EVENT_SCOPE_STR", feature + " offers STR settings without an auto/plain selector");
                    ((TextBox)editor.FindName("MacroNameInput")).Text = "PROJECT_TRACE_CPU_SCOPE_STR";
                    ((TextBox)editor.FindName("HeaderInput")).Text = "Tracing/ProjectCpuTrace.h";
                    check(Selected() is null && !((Button)window.FindName("ApplyButton")).IsEnabled, feature + " editing macro settings invalidates the old preview");
                    ((Button)window.FindName("AnalyzeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
                    check(Selected()!.Files.Single().ProposedText.Contains("PROJECT_TRACE_CPU_SCOPE_STR(\"F Work\")") && Selected()!.Files.Single().ProposedText.Contains("#include \"Tracing/ProjectCpuTrace.h\""), feature + " custom macro and header reach preview together");
                }
                string proposal = Selected()!.Files.Single().ProposedText;
                foreach (string invalid in new[] { source, Path.Combine(source, "Backup"), "", "relative" })
                {
                    backupInput.Text = invalid;
                    check(!((Button)window.FindName("ApplyButton")).IsEnabled && ((Button)window.FindName("ExportButton")).IsEnabled, feature + " invalid backup path blocks apply while allowing export");
                }
                backupInput.Text = customBackup;
                check(((Button)window.FindName("ApplyButton")).IsEnabled && Selected()!.Files.Single().ProposedText == proposal, feature + " changing backup path retains preview and enables apply without reanalysis");
                hint.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                check(hint.Text.Contains(customBackup), feature + " apply status shows configured backup location");
                if (screenshots is not null) Program.Screenshot(window, Path.Combine(screenshots, "backup-location-" + feature + ".png"), 1360, 900);
                var list = (DataGrid)window.FindName("FileList");
                var item = list.Items.Cast<object>().Single(row => (string)row.GetType().GetProperty("RelativePath")!.GetValue(row)! == "Good.cpp");
                list.ScrollIntoView(item); window.UpdateLayout();
                var checkbox = Descendants((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(item)).OfType<CheckBox>().First();
                checkbox.IsChecked = false; Until(() => !Busy());
                check(Selected()?.ChangedFiles == 0 && !((Button)window.FindName("ApplyButton")).IsEnabled, feature + " actual checkbox uncheck disables apply");
                checkbox.IsChecked = true; Until(() => !Busy());
                check(Selected()?.ChangedFiles == 1 && ((Button)window.FindName("ApplyButton")).IsEnabled, feature + " actual checkbox recheck enables apply");
                string backup = new SourceWorkspace().Apply(Selected()!, backupLocation.BackupRoot);
                check(Path.GetDirectoryName(backup) == customBackup && !Directory.Exists(Path.Combine(source, ".ueetc-backups")), feature + " uses configured external backup folder only");
                check(Directory.GetFiles(backup, "*.cpp", SearchOption.AllDirectories).Length == 1, feature + " backs up only selected good file");
                check(File.ReadAllText(Path.Combine(source, "Good.cpp")).Contains("TRACE_"), feature + " applies valid selected file");
                check(File.ReadAllBytes(Path.Combine(source, "Unsupported.cpp")).SequenceEqual(unsupported), feature + " preserves excluded unsupported bytes");
                File.WriteAllText(Path.Combine(source, "Good.cpp"), good);
                string lockedPath = Path.Combine(source, "Locked.cpp");
                File.WriteAllText(lockedPath, good);
                using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                ((Button)window.FindName("AnalyzeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
                check(Selected() is { ChangedFiles: 1, DiagnosticCount: > 0 } && !((Button)window.FindName("ApplyButton")).IsEnabled, feature + " workspace-wide read failure still blocks selected changes");
                hint.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                check(hint.Text.Contains("Locked.cpp") && hint.Text.Contains("適用不可"), feature + " blocked reason identifies the workspace error file");
                bool rejected = false;
                try { new SourceWorkspace().Apply(Selected()!); } catch (InvalidOperationException) { rejected = true; }
                check(rejected && File.ReadAllText(Path.Combine(source, "Good.cpp")) == good, feature + " backend also rejects workspace diagnostics without writes");
            }
            finally { window.Close(); }
            var restarted = Create(new InputSettingsStore(settings.Path));
            try
            {
                check(((BackupLocationPicker)restarted.FindName("BackupLocation")).BackupRoot == customBackup, feature + " backup location survives reopening with settings reloaded from disk");
                if (feature != "Events")
                {
                    var saved = ((CpuTraceSettingsEditor)restarted.FindName("CpuTraceSettings")).Settings;
                    check(saved.MacroName == "PROJECT_TRACE_CPU_SCOPE_STR" && saved.Header == "Tracing/ProjectCpuTrace.h", feature + " CPU macro and header survive reopening with settings reloaded from disk");
                }
            }
            finally { restarted.Close(); }

            Window Create(InputSettingsStore store) => feature switch { "CPU" => new CpuTracingWindow(store), "Events" => new EventCountersWindow(store), _ => new CombinedTracingWindow(store) };
            bool Busy() => window switch { CpuTracingWindow w => w.IsBusy, EventCountersWindow w => w.IsPreviewPending, CombinedTracingWindow w => w.IsBusy, _ => false };
            AnalysisPlan? Selected() => window switch { CpuTracingWindow w => w.SelectedPlan, EventCountersWindow w => w.SelectedPlan, CombinedTracingWindow w => w.SelectedPlan, _ => null };
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Until(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Apply availability test timed out");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
