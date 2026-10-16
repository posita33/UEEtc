using System.Text.RegularExpressions;

namespace UEEtc.Core.Features.FunctionTracing;

internal static class CpuScopeMacro
{
    public static (string Macro, string Argument, string Reason) Select(string label, CpuMacroKind kind)
    {
        if (kind != CpuMacroKind.StaticString && Regex.IsMatch(label, @"^[A-Za-z_][A-Za-z0-9_]*(?: [A-Za-z_][A-Za-z0-9_]*)*$"))
            return ("TRACE_CPUPROFILER_EVENT_SCOPE", label, "固定のplain textを文字列化。STRと同じ静的ID経路");
        return ("TRACE_CPUPROFILER_EVENT_SCOPE_STR", "\"" + label.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            kind == CpuMacroKind.StaticString ? "固定文字列リテラルを指定（静的IDキャッシュ）" : "plain textとして安全に表せないため固定文字列リテラルを指定");
    }
}
