using System.Text.Json;

namespace UEEtc.Blueprint;

// Keep the exception narrow: a completed export alone must not hide UE load errors or crashes.
internal static class EditorExportOutcome
{
    private const string Deprecation = "r.Mobile.VirtualTextures is deprecated, use r.VirtualTextures in platform specific engine .ini files instead";
    private const string OutputError = "LogOutputDevice: Error:";

    public static string? Validate(EditorExportRequest request, int exitCode, IEnumerable<string> logLines)
    {
        bool knownOnly = true, sawDeprecation = false, sawEnsure = false, sawLocation = false;
        var causes = new List<string>();
        foreach (string line in logLines)
        {
            if (!line.Contains(": Error:", StringComparison.Ordinal) && !line.Contains(": Fatal:", StringComparison.Ordinal)) continue;
            int index = line.IndexOf(OutputError, StringComparison.Ordinal);
            string message = index < 0 ? line : line[(index + OutputError.Length)..].Trim();
            bool location = message.StartsWith("Ensure condition failed: false", StringComparison.Ordinal) &&
                message.Contains("ConsoleManager.cpp]", StringComparison.Ordinal);
            bool deprecated = message == Deprecation;
            sawDeprecation |= deprecated; sawLocation |= location;
            sawEnsure |= message == "=== Handled ensure: ===";
            bool detail = message.Length == 0 || message == "Stack:" || message.StartsWith("[Callstack]", StringComparison.Ordinal);
            knownOnly &= index >= 0 && (deprecated || location || detail || message == "=== Handled ensure: ===") &&
                !line.Contains(": Fatal:", StringComparison.Ordinal);
            if (!detail && !location && message != "=== Handled ensure: ===" && causes.Count < 5 && !causes.Contains(message)) causes.Add(message);
        }

        bool completed = Completed(request);
        if (exitCode == 0 && completed) return null;
        if (exitCode == 1 && completed && knownOnly && sawDeprecation && sawEnsure && sawLocation)
            return "UE起動時に廃止設定 r.Mobile.VirtualTextures のensureが記録されました。抽出完了と全出力を確認したため資料を作成しました。プロジェクト設定は変更していません。UEバージョンに合わせた設定の見直しが必要です。";

        throw new IOException($"UE解析が終了コード{exitCode}で失敗しました。" +
            (completed ? "抽出後のEngineエラーを確認してください。" : "抽出の正常完了を確認できませんでした。Engineバージョン・依存モジュール・Blueprintを確認してください。") +
            (causes.Count > 0 ? "\n原因:\n" + string.Join('\n', causes) : "\n詳細は診断ログを確認してください。"));
    }

    private static bool Completed(EditorExportRequest request)
    {
        try
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(request.WorkDirectory, "metadata.json")));
            var root = metadata.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("CompletionToken", out var token) && token.GetString() == request.CompletionToken &&
                root.TryGetProperty("PackagePath", out var package) && package.GetString() == request.PackagePath &&
                root.TryGetProperty("EngineVersion", out var version) && !string.IsNullOrWhiteSpace(version.GetString()) &&
                new[] { "Blueprint", "Class", "Defaults" }.All(name =>
                    File.Exists(Path.Combine(request.WorkDirectory, name + ".copy")) && new FileInfo(Path.Combine(request.WorkDirectory, name + ".copy")).Length > 0);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { return false; }
    }
}
