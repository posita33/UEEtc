using System.Text.RegularExpressions;

namespace UEEtc.Core.Features.EventCounters;

internal static class EventSelectionValidator
{
    public static IReadOnlyList<string> Validate(EventCounterScan scan, EventCounterSelection[] requested, bool allowUnconfirmed)
    {
        var errors = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var available = scan.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var s in requested)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Id) || !available.TryGetValue(s.Id, out var candidate)) { errors.Add("候補が存在しません。再解析してください。"); continue; }
            if (!seen.Add(s.Id)) errors.Add("同じ候補が複数回選択されています。");
            Metric(s.CounterName, s.DisplayName);
            if (!Enum.IsDefined(s.Measurement)) errors.Add(candidate.DisplayName + ": 未知のカウンター計測モードです。");
            if (s.IncludeActiveScope && s.Measurement == CounterMeasurement.ActiveScope) errors.Add(candidate.DisplayName + ": スコープ現在数の二重設定を解除してください。");
            if ((s.IncludeActiveScope || s.Measurement == CounterMeasurement.ActiveScope) && candidate.ScopeTrackingExclusion is { } exclusion)
                errors.Add(candidate.DisplayName + ": " + exclusion);
            if (s.Quantity is not { } q) continue;
            Metric(q.CounterName, q.DisplayName);
            if (string.IsNullOrWhiteSpace(q.Unit) || q.Unit.Any(char.IsControl)) errors.Add(candidate.DisplayName + ": ADD の単位を入力してください。");
            if (string.IsNullOrWhiteSpace(q.Value) || !QuantityCounterValues.IsAllowed(q.Value, candidate)) errors.Add(candidate.DisplayName + ": ADD の値を候補一覧または十進整数で入力してください。");
            if (!allowUnconfirmed && !q.MeaningConfirmed) errors.Add(candidate.DisplayName + ": ADD の数量と単位の意味を確認してください。");
        }
        return errors;
        void Metric(string name, string display)
        {
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]*$") || name.Contains("__", StringComparison.Ordinal)) errors.Add("カウンター名は英字で始まる識別子にしてください（二重アンダースコアは不可）。");
            if (string.IsNullOrWhiteSpace(display) || display.Any(char.IsControl)) errors.Add("表示名には空白だけの名前や制御文字を使用できません。");
        }
    }

}
