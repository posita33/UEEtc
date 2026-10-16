using System.ComponentModel;
using UEEtc.Core.Features.EventCounters;

namespace UEEtc.Wpf.Features.EventCounters;

public sealed class CandidateRow : INotifyPropertyChanged
{
    private readonly Action changed;
    private readonly Action<CandidateRow>? edited;
    public EventCounterCandidate Candidate { get; }
    public CandidateRow(EventCounterCandidate candidate, Action changed, Action<CandidateRow>? edited = null,
        string? savedName = null, string? savedDisplay = null)
    {
        Candidate = candidate; this.changed = changed; this.edited = edited;
        var defaults = EventCounterDefaults.Create(candidate);
        counterName = savedName ?? defaults.CounterName; displayName = savedDisplay ?? defaults.DisplayName;
        includeActiveScope = defaults.IncludeActiveScope;
        quantityEnabled = defaults.Quantity is not null; meaningConfirmed = defaults.Quantity?.MeaningConfirmed == true;
        quantityName = defaults.Quantity?.CounterName ?? candidate.SuggestedName + "Quantity";
        quantityDisplay = defaults.Quantity?.DisplayName ?? candidate.DisplayName + " Quantity";
        quantityValue = defaults.Quantity?.Value ?? ""; quantityUnit = defaults.Quantity?.Unit ?? "";
    }
    public bool CanTrackActiveScopes => Candidate.ScopeTrackingExclusion is null;
    private bool selected = true;
    private bool trackActiveScopes;
    private bool includeActiveScope;
    public bool IncludeActiveScope { get => includeActiveScope; set { includeActiveScope = value; if (value) TrackActiveScopes = false; Changed(nameof(IncludeActiveScope)); } }
    public string AutomaticDecision => EventCounterDefaults.Explain(Candidate);
    public string PlannedMacros => "INCREMENT" + (IncludeActiveScope || TrackActiveScopes ? " + DECREMENT（スコープ）" : "") + (QuantityEnabled ? " + ADD " + QuantityValue + " [" + QuantityUnit + "]" : "");
    private bool changingMeasurement;
    public bool TrackActiveScopes
    {
        get => trackActiveScopes;
        set
        {
            if (trackActiveScopes == value) return;
            if (value) IncludeActiveScope = false;
            changingMeasurement = true;
            string activeName = EventCounterDefaults.ActiveScopeName(Candidate.SuggestedName);
            if (CounterName == (value ? Candidate.SuggestedName : activeName)) CounterName = value ? activeName : Candidate.SuggestedName;
            if (DisplayName == (value ? Candidate.DisplayName : Candidate.DisplayName + " ActiveScopes")) DisplayName = Candidate.DisplayName + (value ? " ActiveScopes" : "");
            trackActiveScopes = value; changingMeasurement = false; Changed(nameof(TrackActiveScopes));
        }
    }
    private string counterName, displayName;
    private bool quantityEnabled, meaningConfirmed;
    private string quantityName, quantityDisplay, quantityValue, quantityUnit;
    public string Recommendation => Candidate.RecommendedQuantity is { } q ? $"{q.Value} [{q.Unit}] — {q.Reason}" : "推奨なし（値・単位を安全に推定できない、または候補が複数）";
    public bool QuantityEnabled { get => quantityEnabled; set { quantityEnabled = value; Changed(nameof(QuantityEnabled)); } }
    public bool MeaningConfirmed { get => meaningConfirmed; set { meaningConfirmed = value; Changed(nameof(MeaningConfirmed)); } }
    public string QuantityName { get => quantityName; set { quantityName = value; Changed(nameof(QuantityName)); } }
    public string QuantityDisplay { get => quantityDisplay; set { quantityDisplay = value; Changed(nameof(QuantityDisplay)); } }
    public string QuantityValue { get => quantityValue; set { if (quantityValue == value) return; quantityValue = value; MeaningConfirmed = false; Changed(nameof(QuantityValue)); } }
    public string QuantityUnit { get => quantityUnit; set { if (quantityUnit == value) return; quantityUnit = value; MeaningConfirmed = false; Changed(nameof(QuantityUnit)); } }
    public bool Selected { get => selected; set { selected = value; Changed(nameof(Selected)); } }
    public string CounterName { get => counterName; set { counterName = value; Changed(nameof(CounterName)); } }
    public string DisplayName { get => displayName; set { displayName = value; Changed(nameof(DisplayName)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) { PropertyChanged?.Invoke(this, new(name)); PropertyChanged?.Invoke(this, new(nameof(PlannedMacros))); changed(); if (!changingMeasurement && name is nameof(CounterName) or nameof(DisplayName)) edited?.Invoke(this); }
    public EventCounterSelection Selection() => new(Candidate.Id, CounterName, DisplayName,
        QuantityEnabled ? new(QuantityName, QuantityDisplay, QuantityValue, QuantityUnit, MeaningConfirmed) : null,
        TrackActiveScopes ? CounterMeasurement.ActiveScope : CounterMeasurement.Cumulative, IncludeActiveScope);
}
