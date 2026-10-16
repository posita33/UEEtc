using UEEtc.Core.Source;

namespace UEEtc.Core.Features.FunctionTracing;

internal sealed record CpuScopePlacement(int? Position, string Decision)
{
    public static CpuScopePlacement Find(string functionName, IReadOnlyList<CppToken> tokens,
        IReadOnlyList<CppToken> scanned, int open, int close)
    {
        string method = functionName.Split("::")[^1];
        var calls = new List<(int Index, bool TopLevel)>();
        int depth = 0;
        for (int i = open + 1; i < close; i++)
        {
            if (tokens[i].Literal) continue;
            if (i + 3 < close && tokens[i].Text == "Super" && tokens[i + 1].Text == "::" &&
                tokens[i + 2].Text == method && tokens[i + 3].Text == "(")
                calls.Add((i, depth == 0));
            if (tokens[i].Text is "{" or "(" or "[") depth++;
            else if (tokens[i].Text is "}" or ")" or "]") depth--;
        }
        if (calls.Count == 0) return new(tokens[open].End, "追加：関数全体の時間");
        if (tokens.Skip(open + 1).Take(close - open - 1).Any(t => !t.Literal && t.Text == "goto"))
            return new(null, "gotoを含む関数は同名Super後のスコープ初期化を飛び越す可能性があるため手動確認");
        const string manual = "同名の Super 呼び出しの後に安全な計測位置を確定できないため手動確認";
        if (calls.Count != 1 || !calls[0].TopLevel ||
            scanned.Any(t => t.Directive && t.Start > tokens[open].Start && t.Start < tokens[close].End))
            return new(null, manual);
        int call = calls[0].Index;
        if (call != open + 1 && tokens[call - 1].Text is not (";" or "}")) return new(null, manual);
        // The statement must be precisely Super::CurrentMethod(arguments);, not a return/assignment/condition.
        int parenDepth = 0, end = call + 3;
        for (; end < close; end++)
        {
            if (tokens[end].Literal) continue;
            if (tokens[end].Text == "(") parenDepth++;
            else if (tokens[end].Text == ")" && --parenDepth == 0) break;
        }
        if (end + 1 >= close || tokens[end + 1].Text != ";") return new(null, manual);
        if (end + 2 == close) return new(null, "同名の Super 呼び出し後に計測する処理がないため CPU 付与を省略");
        return new(tokens[end + 1].End, "追加：同名の Super 呼び出し後から関数終了まで（先行処理を含まない）");
    }
}
