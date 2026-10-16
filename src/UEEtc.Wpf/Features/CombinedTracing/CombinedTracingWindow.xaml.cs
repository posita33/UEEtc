using System.Windows;
using UEEtc.Core.Features.CombinedTracing;
using UEEtc.Core.Plans;
using UEEtc.Wpf.InputAssistance;
using UEEtc.Wpf.Workflow;

namespace UEEtc.Wpf.Features.CombinedTracing;

public partial class CombinedTracingWindow : Window
{
    private readonly PlanWindowWorkflow workflow;
    public AnalysisPlan? SelectedPlan => workflow.SelectedPlan;
    public bool IsBusy => workflow.IsBusy;
    public CombinedTracingWindow() : this(InputSettingsStore.Default) { }
    public CombinedTracingWindow(InputSettingsStore settings)
    {
        InitializeComponent();
        workflow = new(this, settings, "CombinedTracing", "CPU・イベントの統合変更案", new CombinedTracingService().Analyze);
    }
}
