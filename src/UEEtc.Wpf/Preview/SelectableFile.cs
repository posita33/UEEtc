using System.ComponentModel;
using UEEtc.Core.Plans;

namespace UEEtc.Wpf.Preview;

public sealed class SelectableFile(FileAnalysis file, Action changed, bool initiallySelected = false) : INotifyPropertyChanged, ISelectionRow
{
    public FileAnalysis File { get; } = file;
    public string RelativePath => File.RelativePath;
    public int AddedFunctions => File.AddedFunctions;
    public bool CanSelect => File.HasChanges;
    private bool selected = initiallySelected && file.HasChanges;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public static AnalysisPlan Select(AnalysisPlan plan, IEnumerable<SelectableFile> files) => plan with
    { Files = files.Where(f => f.Selected && f.File.HasChanges).Select(f => f.File).ToArray() };
}
