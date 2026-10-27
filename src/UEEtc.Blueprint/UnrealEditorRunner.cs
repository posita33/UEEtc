using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UEEtc.Blueprint;

public sealed record EditorExportRequest(string EditorPath, string ProjectPath, string PackagePath, string WorkDirectory);
public interface IUnrealEditorRunner
{
    Task ExportAsync(EditorExportRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed class UnrealEditorRunner : IUnrealEditorRunner
{
    public async Task ExportAsync(EditorExportRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string script = Path.Combine(request.WorkDirectory, "export_blueprint.py");
        using (var resource = typeof(UnrealEditorRunner).Assembly.GetManifestResourceStream("UEEtc.Blueprint.Unreal.export_blueprint.py")
            ?? throw new InvalidOperationException("UE抽出スクリプトがありません。"))
        await using (var destination = File.Create(script)) await resource.CopyToAsync(destination, cancellationToken);
        string requestFile = Path.Combine(request.WorkDirectory, "request.json");
        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(request), cancellationToken);
        var start = new ProcessStartInfo(request.EditorPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkDirectory
        };
        // UE's quoted -script parser treats backslashes as escapes. Forward slashes
        // keep Windows paths with spaces (including \U... directories) intact.
        foreach (string argument in new[] { request.ProjectPath.Replace('\\', '/'), "-run=pythonscript", "-script=" + script.Replace('\\', '/'),
            "-EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities", "-unattended", "-NullRHI", "-nosound",
            "-NoSplash", "-NoP4", "-NoAutoSave", "-stdout", "-UTF8Output", "-abslog=" + Path.Combine(request.WorkDirectory, "unreal.log").Replace('\\', '/') })
            start.ArgumentList.Add(argument);
        start.Environment["UEETC_BLUEPRINT_REQUEST"] = requestFile;
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("UE EditorでBlueprintを読み込み中（初回起動には時間がかかります）…");
        if (!process.Start()) throw new IOException("UnrealEditor-Cmdを起動できません。");
        await using var log = new StreamWriter(Path.Combine(request.WorkDirectory, "editor.log"), false, new UTF8Encoding(false));
        using var gate = new SemaphoreSlim(1, 1);
        var tail = new Queue<string>();
        Task stdout = Read(process.StandardOutput), stderr = Read(process.StandardError);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            throw;
        }
        finally
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(15));
            await log.FlushAsync();
        }
        if (process.ExitCode != 0)
            throw new IOException($"UE解析が終了コード{process.ExitCode}で失敗しました。Engineバージョン・依存モジュール・Blueprintを確認してください。\n" + string.Join('\n', tail));

        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await gate.WaitAsync();
                try
                {
                    await log.WriteLineAsync(line);
                    tail.Enqueue(line.Length > 1000 ? line[..1000] : line);
                    if (tail.Count > 20) tail.Dequeue();
                }
                finally { gate.Release(); }
            }
        }
    }
}
