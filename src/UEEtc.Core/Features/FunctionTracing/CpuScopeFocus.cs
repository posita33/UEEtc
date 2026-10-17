using UEEtc.Core.Source;

namespace UEEtc.Core.Features.FunctionTracing;

// Static evidence for choosing measurement candidates, not a prediction of measured cost.
internal static class CpuScopeFocus
{
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "Serialize", "Deserialize", "Compress", "Decompress", "Uncompress", "Flush", "Wait",
        "WaitForCompletion", "ParallelFor", "LoadFileToArray", "SaveArrayToFile", "Sort", "StableSort"
    };
    public static (bool Include, string Reason) Evaluate(IReadOnlyList<CppToken> tokens, int position, int close, string cpuMacro)
    {
        int calls = 0;
        for (int i = 0; i < close; i++)
        {
            if (tokens[i].Start < position || tokens[i].Literal) continue;
            // Skip lambda bodies: constructing a deferred callable does not measure its later execution.
            if (tokens[i].Text == "[")
            {
                int end = Match(i, "[", "]");
                int next = end + 1;
                if (next < close && tokens[next].Text == "(") next = Match(next, "(", ")") + 1;
                // Also accept trailing return types, noexcept(expr), attributes and generic lambdas.
                while (next < close && tokens[next].Text is not ("{" or ";" or "=")) next++;
                if (next < close && tokens[next].Text == "{") { i = Match(next, "{", "}"); continue; }
            }
            string name = tokens[i].Text;
            if (name is "for" or "while" or "do") return (true, "繰り返し処理あり（実行回数・コストは未測定）");
            if (i + 1 >= close || tokens[i + 1].Text != "(" || !char.IsAsciiLetter(name.FirstOrDefault())) continue;
            if (Operations.Contains(name)) return (true, "明示的なデータ処理・待機 API: " + name + "（コストは未測定）");
            if (name == cpuMacro || name is "if" or "switch" or "catch" or "sizeof" or "alignof" or "static_cast" or "const_cast" or "reinterpret_cast" ||
                name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("Is", StringComparison.Ordinal) ||
                name.StartsWith("Has", StringComparison.Ordinal) || name.StartsWith("Can", StringComparison.Ordinal) ||
                name.StartsWith("UE_", StringComparison.Ordinal) || name.StartsWith("TRACE_", StringComparison.Ordinal) || name is "check" or "checkf" or "ensure" or "ensureMsgf") continue;
            calls++;
        }
        return calls >= 4 ? (true, $"処理呼び出し {calls} 箇所（コストは未測定）") :
            (false, "絞り込み対象外：ループ・対象 API・4 箇所以上の処理呼び出しを確認できない。全関数モードで指定可能");

        int Match(int start, string open, string shut)
        {
            int depth = 0;
            for (int j = start; j < close; j++)
            {
                if (tokens[j].Literal) continue;
                if (tokens[j].Text == open) depth++;
                else if (tokens[j].Text == shut && --depth == 0) return j;
            }
            return close - 1;
        }
    }
}
