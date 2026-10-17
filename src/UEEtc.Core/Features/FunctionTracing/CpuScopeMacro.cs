namespace UEEtc.Core.Features.FunctionTracing;

internal static class CpuScopeMacro
{
    public static (string Macro, string Argument, string Reason) Select(string label, CpuTraceSettings settings) =>
        (settings.MacroName, "\"" + label.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            "固定文字列リテラルを指定。定義ヘッダー: " + settings.Header);
}
