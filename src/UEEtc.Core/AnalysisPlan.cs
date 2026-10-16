using System.Text.Json.Serialization;

namespace UEEtc.Core;

public sealed record InstrumentationOptions(bool CpuScopes = true, bool CallCounters = false);

public sealed record FunctionAnalysis(string Name, int Line, string Status, string Reason, string? Counter = null)
{
    public bool CpuScopeAdded { get; init; }
    public bool CallCounterAdded { get; init; }
    public string CpuDecision { get; init; } = "対象外";
    public string CounterDecision { get; init; } = "対象外";
    internal string? CounterBaseName { get; init; }
    internal string? CounterIdentity { get; init; }
    internal string? CounterHash { get; init; }
}

public sealed record FileAnalysis(string RelativePath, IReadOnlyList<FunctionAnalysis> Functions,
    IReadOnlyList<string> Diagnostics)
{
    [JsonIgnore] public byte[] OriginalBytes { get; init; } = [];
    [JsonIgnore] public byte[] ProposedBytes { get; init; } = [];
    [JsonIgnore] public string OriginalText { get; init; } = "";
    [JsonIgnore] public string ProposedText { get; init; } = "";
    public bool HasChanges => !OriginalBytes.AsSpan().SequenceEqual(ProposedBytes);
    public int AddedFunctions => Functions.Count(f => f.Status == "Add");
    public int AddedCpuScopes => Functions.Count(f => f.CpuScopeAdded);
    public int AddedCallCounters => Functions.Count(f => f.CallCounterAdded);
}

public sealed record AnalysisPlan(string RootPath, InstrumentationOptions Options,
    IReadOnlyList<FileAnalysis> Files, IReadOnlyList<string> Diagnostics)
{
    public int ChangedFiles => Files.Count(f => f.HasChanges);
    public int AddedFunctions => Files.Sum(f => f.AddedFunctions);
    public int AddedCpuScopes => Files.Sum(f => f.AddedCpuScopes);
    public int AddedCallCounters => Files.Sum(f => f.AddedCallCounters);
    public int SkippedFunctions => Files.Sum(f => f.Functions.Count(x => x.Status == "Skip"));
    public int DiagnosticCount => Diagnostics.Count + Files.Sum(f => f.Diagnostics.Count);
}
