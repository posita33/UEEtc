using System.Windows;
using System.Windows.Controls;
using UEEtc.Wpf.Preview;

namespace UEEtc.Wpf.Workflow;

// The two plan windows keep their own layout/columns and share this explicit control contract.
internal sealed class PlanWindowView(Window owner)
{
    public TextBox SourcePath { get; } = Find<TextBox>(owner, "SourcePath");
    public TextBox Filter { get; } = Find<TextBox>(owner, "FilterText");
    public TextBox Original { get; } = Find<TextBox>(owner, "OriginalText");
    public TextBox Diagnostics { get; } = Find<TextBox>(owner, "DiagnosticsText");
    public DataGrid Files { get; } = Find<DataGrid>(owner, "FileList");
    public DataGrid Functions { get; } = Find<DataGrid>(owner, "FunctionList");
    public CpuTraceSettingsEditor CpuTrace { get; } = Find<CpuTraceSettingsEditor>(owner, "CpuTraceSettings");
    public CheckBox AllFunctions { get; } = Find<CheckBox>(owner, "AllFunctionsOption");
    public CodeChangePreview Preview { get; } = Find<CodeChangePreview>(owner, "PreviewText");
    public TextBlock Status { get; } = Find<TextBlock>(owner, "StatusText");
    public ProgressBar Progress { get; } = Find<ProgressBar>(owner, "Progress");
    public BackupLocationPicker BackupLocation { get; } = Find<BackupLocationPicker>(owner, "BackupLocation");
    public Button Button(string name) => Find<Button>(owner, name);
    private static T Find<T>(Window owner, string name) where T : FrameworkElement => owner.FindName(name) as T
        ?? throw new InvalidOperationException($"{owner.GetType().Name} に {name} がありません。");
}
