using System.Security.Cryptography;
using System.Text.Json;

namespace UEEtc.Blueprint;

public sealed class BlueprintAnalysisService(IUnrealEditorRunner? runner = null) : IBlueprintAnalysisService
{
    private readonly IUnrealEditorRunner runner = runner ?? new UnrealEditorRunner();

    public async Task<BlueprintAnalysisResult> AnalyzeAsync(BlueprintAnalysisOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        options = BlueprintPaths.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        string package = BlueprintPaths.PackagePath(options.AssetPath, options.ProjectPath);
        var inputs = new[] { options.AssetPath, options.ProjectPath, Path.ChangeExtension(options.AssetPath, ".uexp"),
            Path.ChangeExtension(options.AssetPath, ".ubulk") }.Where(File.Exists)
            .ToDictionary(path => path, path => Hash(path), StringComparer.OrdinalIgnoreCase);
        string workParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UEEtc.Blueprint"));
        string work = Path.Combine(workParent, Guid.NewGuid().ToString("N"));
        BlueprintPaths.EnsureNoLinks(work); Directory.CreateDirectory(work);
        string? staging = null;
        try
        {
            await runner.ExportAsync(new(options.EditorPath, options.ProjectPath, package, work), progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("グラフ・ピン・既定値を整理し、移植資料を生成中…");
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work, "metadata.json"), cancellationToken));
            var documents = new List<BlueprintDocument>();
            foreach (string name in new[] { "Blueprint", "Class", "Defaults" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = Path.Combine(work, name + ".copy");
                if (!File.Exists(file) || new FileInfo(file).Length > 256L * 1024 * 1024)
                    throw new InvalidDataException("UEの出力が存在しないか大きすぎます: " + name);
                documents.Add(UnrealTextParser.Parse(name, await File.ReadAllTextAsync(file, cancellationToken), options.RemoveLayout, cancellationToken));
            }
            var warnings = new List<string>();
            string log = Path.Combine(work, "editor.log");
            if (File.Exists(log)) warnings.AddRange(File.ReadLines(log).Where(line => line.Contains(": Warning:", StringComparison.Ordinal) || line.Contains(": Error:", StringComparison.Ordinal)).Take(100));
            if (documents[0].Graphs.Count == 0) warnings.Add("抽出されたBlueprintにグラフがありません。データのみのBlueprintか、エディター用情報の欠落を確認してください。");
            var report = new BlueprintReport(options.AssetPath, package, inputs[options.AssetPath],
                metadata.RootElement.GetProperty("EngineVersion").GetString() ?? "不明", options.RemoveLayout, documents, warnings);
            VerifyInputs();
            cancellationToken.ThrowIfCancellationRequested();
            string output = Path.Combine(options.OutputRoot, Path.GetFileNameWithoutExtension(options.AssetPath) + "-" +
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
            staging = Path.Combine(options.OutputRoot, ".pending-" + Guid.NewGuid().ToString("N"));
            BlueprintPaths.EnsureNoLinks(staging); Directory.CreateDirectory(staging);
            string markdown = Path.Combine(output, "analysis.md"), json = Path.Combine(output, "analysis.json");
            foreach (var document in documents) await File.WriteAllTextAsync(Path.Combine(staging, document.Name + ".txt"), document.Text, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "analysis.json"), JsonSerializer.Serialize(report, BlueprintReportWriter.JsonOptions), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "analysis.md"), BlueprintReportWriter.Markdown(report), cancellationToken);
            if (File.Exists(log)) File.Copy(log, Path.Combine(staging, "editor.log"));
            VerifyInputs(); cancellationToken.ThrowIfCancellationRequested();
            BlueprintPaths.EnsureNoLinks(output); Directory.Move(staging, output); staging = null;
            return new(output, markdown, json, report);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (SaveFailureLog() is { } saved) throw new IOException(error.Message + "\n診断ログ: " + saved, error);
            throw;
        }
        finally
        {
            try { VerifyInputs(); }
            finally
            {
                try
                {
                    if (staging is not null)
                    {
                        if (!BlueprintPaths.IsInside(staging, options.OutputRoot)) throw new IOException("資料の作業フォルダーが想定外です。");
                        BlueprintPaths.EnsureNoLinks(staging);
                        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                    }
                }
                finally
                {
                    if (!BlueprintPaths.IsInside(work, workParent)) throw new IOException("一時フォルダーが想定外です。");
                    BlueprintPaths.EnsureNoLinks(work);
                    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                }
            }
        }

        void VerifyInputs()
        {
            foreach (var input in inputs)
                if (!File.Exists(input.Key) || Hash(input.Key) != input.Value)
                    throw new IOException("解析中に入力ファイルが変更されました。自動復元せず停止します: " + input.Key);
        }
        string? SaveFailureLog()
        {
            string log = Path.Combine(work, "editor.log");
            if (!File.Exists(log)) return null;
            try
            {
                BlueprintPaths.EnsureNoLinks(options.OutputRoot); Directory.CreateDirectory(options.OutputRoot);
                string saved = Path.Combine(options.OutputRoot, "failed-" + Guid.NewGuid().ToString("N") + ".log");
                File.Copy(log, saved); return saved;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
