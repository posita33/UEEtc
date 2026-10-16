using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UEEtc.Wpf.InputAssistance;

public sealed class RememberedInputs
{
    private readonly InputSettingsStore store;
    private readonly Action<string> report;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public RememberedInputs(Window owner, InputSettingsStore store, Action<string> report)
    {
        this.store = store; this.report = report;
        timer.Tick += (_, _) => Flush();
        owner.Closed += (_, _) => Flush();
    }
    public void Bind(TextBox input, string key)
    {
        if (store.Get(key) is { } saved) input.Text = saved;
        input.TextChanged += (_, _) => Set(key, input.Text);
    }
    public string? Get(string key) => store.Get(key);
    public void Set(string key, string value)
    {
        store.Set(key, value);
        timer.Stop(); timer.Start();
    }
    public void ReportLoadError() { if (store.Error is { } error) report(error); }
    private void Flush() { timer.Stop(); if (!store.Flush() && store.Error is { } error) report(error); }
}
