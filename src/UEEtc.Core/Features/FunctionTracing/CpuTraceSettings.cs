using System.Text.RegularExpressions;

namespace UEEtc.Core.Features.FunctionTracing;

// The configured macro must accept one fixed string and keep the function's lexical lifetime.
public sealed record CpuTraceSettings(string MacroName = "TRACE_CPUPROFILER_EVENT_SCOPE_STR",
    string Header = "ProfilingDebugging/CpuProfilerTrace.h")
{
    public CpuTraceSettings Validated()
    {
        if (string.IsNullOrEmpty(MacroName) || !Regex.IsMatch(MacroName, @"\A[A-Za-z][A-Za-z0-9_]*\z") ||
            MacroName is "TRACE_CPUPROFILER_EVENT_SCOPE" or "TRACE_CPUPROFILER_EVENT_SCOPE_TEXT")
            throw new ArgumentException("CPUマクロ名にはTRACE_CPUPROFILER_EVENT_SCOPE_STR、または固定文字列1引数を受け取る独自マクロの識別子を指定してください。");
        string header = Header?.Replace('\\', '/') ?? "";
        if (string.IsNullOrWhiteSpace(header) || header.StartsWith('/') ||
            header.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or ':') ||
            header.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("CPUヘッダーにはモジュールからincludeできる相対パスを指定してください（例: Tracing/ProjectCpuTrace.h）。");
        return this with { Header = header };
    }
}
