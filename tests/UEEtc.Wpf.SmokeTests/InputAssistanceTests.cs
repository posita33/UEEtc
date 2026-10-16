using System.IO;
using System.Windows;
using System.Windows.Controls;
using UEEtc.Wpf.Features.CpuTracing;
using UEEtc.Wpf.Features.EventCounters;
using UEEtc.Wpf.InputAssistance;

internal static class InputAssistanceTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        string settingsPath = Path.Combine(root, "assistance", "settings.json");
        string folder = Path.Combine(root, "drop target 日本語"); Directory.CreateDirectory(folder);
        var store = new InputSettingsStore(settingsPath);
        var cpu = new CpuTracingWindow(store);
        var events = new EventCountersWindow(store);
        try
        {
            var path = (TextBox)cpu.FindName("SourcePath");
            var eventPath = (TextBox)events.FindName("SourcePath");
            var filter = (TextBox)events.FindName("FilterText");
            path.Text = "CPU input with spaces 日本語";
            eventPath.Text = folder; filter.Text = "復旧";
            check(!File.Exists(settingsPath), "Saving is debounced, not per keystroke");
            cpu.Close(); events.Close();
            var restored = new InputSettingsStore(settingsPath);
            check(restored.Get("CpuTracing.SourcePath") == "CPU input with spaces 日本語", "CPU input is persisted exactly");
            check(restored.Get("EventCounters.SourcePath") == folder && restored.Get("EventCounters.Filter") == "復旧", "Event path and filter are persisted independently");
            var reopened = new CpuTracingWindow(restored);
            try
            {
                var input = (TextBox)reopened.FindName("SourcePath");
                check(input.Text == "CPU input with spaces 日本語", "CPU textbox restores on new window");
                var drag = Drag(input, [folder], UIElement.PreviewDragOverEvent);
                check(drag.Handled && drag.Effects == DragDropEffects.Copy, "Folder drag is accepted before native TextBox handling");
                var drop = Drag(input, [folder + Path.DirectorySeparatorChar], UIElement.PreviewDropEvent);
                check(drop.Handled && input.Text == Path.GetFullPath(folder), "Folder drop assigns a full normalized path");
                check(!((Button)reopened.FindName("ApplyButton")).IsEnabled, "Drop does not analyze or enable apply");
                Drag(input, [Path.Combine(root, "Fixture.cpp")], UIElement.PreviewDropEvent);
                check(input.Text == Path.GetFullPath(folder), "A dropped file leaves the input intact");
                Drag(input, [folder, root], UIElement.PreviewDropEvent);
                check(input.Text == Path.GetFullPath(folder), "Multiple folders are rejected");
                input.IsEnabled = false;
                var blocked = Drag(input, [root], UIElement.PreviewDropEvent);
                check(input.Text == Path.GetFullPath(folder) && blocked.Effects == DragDropEffects.None, "Disabled input rejects drops during operations");
                input.IsEnabled = true;
                input.Text = "";
            }
            finally { reopened.Close(); }
            check(new InputSettingsStore(settingsPath).Get("CpuTracing.SourcePath") == "", "Clearing input also persists");
            var eventRestored = new EventCountersWindow(new InputSettingsStore(settingsPath));
            try
            {
                var input = (TextBox)eventRestored.FindName("SourcePath");
                check(((TextBox)eventRestored.FindName("FilterText")).Text == "復旧", "Filter restores to the event window");
                Drag(input, [root], UIElement.PreviewDropEvent);
                check(input.Text == Path.GetFullPath(root), "Event source also accepts folder drops");
            }
            finally { eventRestored.Close(); }

            string broken = Path.Combine(root, "broken-settings.json"); File.WriteAllText(broken, "{broken");
            var bad = new InputSettingsStore(broken);
            check(bad.Get("CpuTracing.SourcePath") is null && bad.Error is not null, "Invalid JSON falls back without crashing");
            bad.Set("CpuTracing.SourcePath", folder);
            check(bad.Flush() && new InputSettingsStore(broken).Get("CpuTracing.SourcePath") == folder, "Editing after corrupt input recovers settings");
            string notFile = Path.Combine(root, "settings-is-directory"); Directory.CreateDirectory(notFile);
            var unavailable = new InputSettingsStore(notFile); unavailable.Set("Test", "value");
            check(!unavailable.Flush() && unavailable.Error is not null && unavailable.Get("Test") == "value", "Write failure preserves input in memory and reports the failure");
            check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "No temporary settings files remain");
        }
        finally { cpu.Close(); events.Close(); }
    }
    private static DragEventArgs Drag(TextBox input, string[] paths, RoutedEvent routedEvent)
    {
        var data = new DataObject(DataFormats.FileDrop, paths);
        // WPF constructs drag arguments internally; use that constructor to exercise the actual routed handlers.
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: [data, DragDropKeyStates.None, DragDropEffects.Copy, input, new Point()], culture: null)!;
        args.RoutedEvent = routedEvent;
        input.RaiseEvent(args);
        return args;
    }
}
