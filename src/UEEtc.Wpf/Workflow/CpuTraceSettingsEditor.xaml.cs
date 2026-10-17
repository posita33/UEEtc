using System.Windows.Controls;
using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Wpf.InputAssistance;

namespace UEEtc.Wpf.Workflow;

public partial class CpuTraceSettingsEditor : UserControl
{
    public CpuTraceSettings Settings => new(MacroNameInput.Text.Trim(), HeaderInput.Text.Trim());
    public event EventHandler? Changed;
    public CpuTraceSettingsEditor() => InitializeComponent();

    public void Initialize(RememberedInputs inputs, string key)
    {
        var defaults = new CpuTraceSettings();
        MacroNameInput.Text = defaults.MacroName; HeaderInput.Text = defaults.Header;
        inputs.Bind(MacroNameInput, key + ".CpuMacroName");
        inputs.Bind(HeaderInput, key + ".CpuHeader");
        MacroNameInput.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        HeaderInput.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }
}
