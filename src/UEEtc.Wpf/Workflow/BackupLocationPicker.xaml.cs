using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UEEtc.Core.Source;
using UEEtc.Wpf.InputAssistance;

namespace UEEtc.Wpf.Workflow;

public partial class BackupLocationPicker : UserControl
{
    public string BackupRoot => PathInput.Text.Trim();
    public event EventHandler? Changed;
    public BackupLocationPicker() => InitializeComponent();

    public void Initialize(RememberedInputs inputs, string key)
    {
        PathInput.Text = BackupStorage.DefaultRoot;
        inputs.Bind(PathInput, key + ".BackupRoot");
        PathInput.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "ソース外のバックアップ先を選択" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) PathInput.Text = picker.FolderName;
    }
}
