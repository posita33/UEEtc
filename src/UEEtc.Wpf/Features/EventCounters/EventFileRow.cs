using System.ComponentModel;
using UEEtc.Core.Plans;
using UEEtc.Wpf.Preview;

namespace UEEtc.Wpf.Features.EventCounters;

public sealed class EventFileRow(FileAnalysis file, IReadOnlyList<CandidateRow> candidates, Action changed) : INotifyPropertyChanged, ISelectionRow
{
    public FileAnalysis File { get; } = file;
    public IReadOnlyList<CandidateRow> Candidates { get; } = candidates;
    public string RelativePath => File.RelativePath;
    public int CandidateCount => Candidates.Count(c => c.Selected);
    public bool CanSelect => Candidates.Count > 0;
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); } }
    public void RefreshCount() => PropertyChanged?.Invoke(this, new(nameof(CandidateCount)));
    public event PropertyChangedEventHandler? PropertyChanged;
}
