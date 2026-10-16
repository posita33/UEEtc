namespace UEEtc.Core.Reporting;

// Patience-style line anchors keep repeated braces from shifting the entire preview.
public static class ChangedLineMap
{
    public static IReadOnlyList<int> Compare(string original, string proposed)
    {
        var before = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var after = proposed.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var changed = new SortedSet<int>();
        CompareRange(0, before.Length, 0, after.Length);
        return changed.ToArray();

        void CompareRange(int b, int be, int a, int ae)
        {
            while (b < be && a < ae && before[b] == after[a]) { b++; a++; }
            while (b < be && a < ae && before[be - 1] == after[ae - 1]) { be--; ae--; }
            if (a == ae) return;
            var counts = new Dictionary<string, (int Position, int Count)>(StringComparer.Ordinal);
            for (int i = b; i < be; i++) counts[before[i]] = counts.TryGetValue(before[i], out var c) ? (c.Position, c.Count + 1) : (i, 1);
            var afterCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = a; i < ae; i++) afterCounts[after[i]] = afterCounts.GetValueOrDefault(after[i]) + 1;
            var pairs = new List<(int B, int A)>();
            for (int i = a; i < ae; i++)
                if (counts.TryGetValue(after[i], out var c) && c.Count == 1 && afterCounts[after[i]] == 1) pairs.Add((c.Position, i));
            var tails = new List<int>(); var previous = new int[pairs.Count];
            for (int i = 0; i < pairs.Count; i++)
            {
                int low = 0, high = tails.Count;
                while (low < high) { int mid = (low + high) / 2; if (pairs[tails[mid]].B < pairs[i].B) low = mid + 1; else high = mid; }
                previous[i] = low == 0 ? -1 : tails[low - 1];
                if (low == tails.Count) tails.Add(i); else tails[low] = i;
            }
            if (tails.Count == 0) { for (int i = a; i < ae; i++) changed.Add(i + 1); return; }
            var anchors = new Stack<(int B, int A)>();
            for (int i = tails[^1]; i >= 0; i = previous[i]) anchors.Push(pairs[i]);
            foreach (var anchor in anchors)
            {
                CompareRange(b, anchor.B, a, anchor.A);
                b = anchor.B + 1; a = anchor.A + 1;
            }
            CompareRange(b, be, a, ae);
        }
    }
}
