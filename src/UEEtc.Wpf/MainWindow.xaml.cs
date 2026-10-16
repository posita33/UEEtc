using System.Windows;
using UEEtc.Wpf.Features.CpuTracing;
using UEEtc.Wpf.Features.EventCounters;
using UEEtc.Wpf.InputAssistance;

namespace UEEtc.Wpf;

public partial class MainWindow : Window
{
    private readonly InputSettingsStore settings;
    public MainWindow() : this(InputSettingsStore.Default) { }
    public MainWindow(InputSettingsStore settings) { this.settings = settings; InitializeComponent(); }
    private void OpenCpu_Click(object sender, RoutedEventArgs e) => new CpuTracingWindow(settings) { Owner = this }.Show();
    private void OpenEvents_Click(object sender, RoutedEventArgs e) => new EventCountersWindow(settings) { Owner = this }.Show();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (OwnedWindows.Cast<Window>().Any(w => w is CpuTracingWindow { IsBusy: true } or EventCountersWindow { IsBusy: true }))
        {
            e.Cancel = true;
            MessageBox.Show(this, "各機能の処理が終了してから閉じてください。", "UEEtc");
        }
        base.OnClosing(e);
    }
}
