using System.Windows;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Plans;
using UEEtc.Wpf.InputAssistance;
using UEEtc.Wpf.Workflow;

namespace UEEtc.Wpf.Features.CpuTracing;

public partial class CpuTracingWindow : Window
{
    private readonly PlanWindowWorkflow workflow;
    public AnalysisPlan? SelectedPlan => workflow.SelectedPlan;
    public bool IsBusy => workflow.IsBusy;
    public CpuTracingWindow() : this(InputSettingsStore.Default) { }
    public CpuTracingWindow(InputSettingsStore settings)
    {
        InitializeComponent();
        workflow = new(this, settings, "CpuTracing", "CPUスコープ",
            static (root, focused, macro, progress, token) => new InstrumentationService().Analyze(root,
                new(FocusedCpu: focused, CpuMacro: macro), progress, token));
    }
}
