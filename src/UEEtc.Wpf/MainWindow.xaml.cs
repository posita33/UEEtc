using System.Windows;
using UEEtc.Wpf.Features.CpuTracing;
using UEEtc.Wpf.Features.EventCounters;

namespace UEEtc.Wpf;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
    private void OpenCpu_Click(object sender, RoutedEventArgs e) => new CpuTracingWindow { Owner = this }.Show();
    private void OpenEvents_Click(object sender, RoutedEventArgs e) => new EventCountersWindow { Owner = this }.Show();
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
