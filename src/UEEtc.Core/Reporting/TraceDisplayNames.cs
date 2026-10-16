namespace UEEtc.Core.Reporting;

public static class TraceDisplayNames
{
    public static string Function(string name) => string.Join(" ", name.Split('/')[^1].Split("::").TakeLast(2));
    public static string Event(string name, string branch, int ordinal) => Function(name) + " " + branch.ToLowerInvariant() + ordinal;
}
