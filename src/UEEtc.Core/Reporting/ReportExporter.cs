using UEEtc.Core.Source;
using UEEtc.Core.Plans;

using System.Text;
using System.Text.Json;

namespace UEEtc.Core.Reporting;

public static class ReportExporter
{
    public static void Export(AnalysisPlan plan, string outputPath)
    {
        if (!Path.IsPathFullyQualified(outputPath)) throw new ArgumentException("出力先のフルパスを指定してください。");
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        string root = Path.TrimEndingDirectorySeparator(plan.RootPath);
        if (IsWithin(output, root) || IsWithin(root, output))
            throw new IOException("出力先は解析対象フォルダーの外側（親・子以外）を指定してください。");
        SourceWorkspace.EnsureNoLinks(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("出力先には空のフォルダーを指定してください。");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "changes.patch"), CreatePatch(plan), new UTF8Encoding(false));
        foreach (var file in plan.Files.Where(f => f.HasChanges))
        {
            string destination = SourceWorkspace.Resolve(Path.Combine(output, "modified"), file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, file.ProposedBytes);
        }
    }

    // Full-file unified hunks intentionally preserve all context for review; report copies preserve encoding.
    public static string CreatePatch(AnalysisPlan plan)
    {
        var patch = new StringBuilder();
        foreach (var file in plan.Files.Where(f => f.HasChanges))
        {
            var pathOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string oldPath = JsonSerializer.Serialize("a/" + file.RelativePath, pathOptions);
            string newPath = JsonSerializer.Serialize("b/" + file.RelativePath, pathOptions);
            var before = Lines(file.OriginalText);
            var after = Lines(file.ProposedText);
            if (file.OriginalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                if (before.Length > 0) before[0] = "\uFEFF" + before[0];
                if (after.Length > 0) after[0] = "\uFEFF" + after[0];
            }
            patch.Append("--- ").Append(oldPath).Append('\n').Append("+++ ").Append(newPath).Append('\n');
            patch.Append($"@@ -1,{before.Length} +1,{after.Length} @@\n");
            AppendLines('-', before, file.OriginalText.EndsWith('\n'));
            AppendLines('+', after, file.ProposedText.EndsWith('\n'));
        }
        return patch.ToString();

        void AppendLines(char prefix, string[] lines, bool finalNewline)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                patch.Append(prefix).Append(lines[i]).Append('\n');
                if (i == lines.Length - 1 && !finalNewline) patch.Append("\\ No newline at end of file\n");
            }
        }
    }

    public static string Summary(AnalysisPlan plan) =>
        $"{plan.Files.Count} ファイル解析 / {plan.ChangedFiles} ファイル変更候補 / {plan.AddedFunctions} 箇所付与（CPU {plan.AddedCpuScopes} / 進入回数 {plan.AddedCallCounters} / イベント {plan.AddedEventCounters} / 数量 ADD {plan.AddedQuantityCounters}） / {plan.SkippedFunctions} 関数スキップ / {plan.DiagnosticCount} 件の診断";

    private static string[] Lines(string text) => text.Length == 0 ? [] :
        (text.EndsWith('\n') ? text[..^1] : text).Split('\n');

    private static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
