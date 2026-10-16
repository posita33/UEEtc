namespace UEEtc.Core;

public sealed class InstrumentationService
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "Binaries", "Intermediate", "Saved", "DerivedDataCache", "ThirdParty",
        "Third-Party", "bin", "obj", ".ueetc-backups"
    };

    public AnalysisPlan Analyze(string rootPath, InstrumentationOptions? options = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
            throw new ArgumentException("ソースフォルダーのフルパスを指定してください。", nameof(rootPath));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        EnsureNoLinks(root);
        options ??= new();
        if (!options.CpuScopes && !options.CallCounters)
            throw new ArgumentException("CPU スコープまたは進入回数カウンターを選択してください。");
        var files = new List<FileAnalysis>();
        var diagnostics = new List<string>();
        var instrumenter = new CppInstrumenter();
        Walk(root);
        return new(root, options, files, diagnostics);

        void Walk(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"{directory}: {error.Message}");
                return;
            }
            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add($"リンクを除外: {entry}");
                        continue;
                    }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!ExcludedDirectories.Contains(Path.GetFileName(entry))) Walk(entry);
                        continue;
                    }
                    if (!Path.GetExtension(entry).Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
                        entry.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase) ||
                        entry.EndsWith(".generated.cpp", StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    progress?.Report(relative);
                    files.Add(instrumenter.Analyze(relative, File.ReadAllBytes(entry), options));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add($"{entry}: {error.Message}");
                }
            }
        }
    }

    // Called only by an explicit UI / CLI apply action. The experiment uses Analyze / Export only.
    public string Apply(AnalysisPlan plan)
    {
        if (plan.DiagnosticCount > 0) throw new InvalidOperationException("診断のある解析結果は適用できません。");
        var changes = plan.Files.Where(f => f.HasChanges).ToArray();
        if (changes.Length == 0) throw new InvalidOperationException("適用する変更がありません。");
        foreach (var file in changes) ValidateUnchanged(plan.RootPath, file);
        string backup = Path.Combine(plan.RootPath, ".ueetc-backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        EnsureNoLinks(backup);
        // Back up every file before the first source write.
        foreach (var file in changes)
        {
            string destination = Resolve(backup, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, file.OriginalBytes);
        }
        var written = new List<FileAnalysis>();
        try
        {
            foreach (var file in changes)
            {
                ValidateUnchanged(plan.RootPath, file);
                AtomicWrite(Resolve(plan.RootPath, file.RelativePath), file.ProposedBytes);
                written.Add(file);
            }
        }
        catch (Exception originalError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var file in written.AsEnumerable().Reverse())
            {
                try
                {
                    string path = Resolve(plan.RootPath, file.RelativePath);
                    EnsureNoLinks(path);
                    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(file.ProposedBytes))
                        throw new IOException($"他の変更を検出し復元を中止: {path}");
                    AtomicWrite(path, file.OriginalBytes);
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException($"適用・復元に失敗しました。バックアップ: {backup}", [originalError, .. rollbackErrors]);
            throw new IOException($"適用に失敗し、書き込んだファイルを復元しました。バックアップ: {backup}", originalError);
        }
        return backup;
    }

    internal static string Resolve(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("出力先が指定フォルダーの外です。");
        return path;
    }

    internal static void EnsureNoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"リンク／ジャンクションを含むパスは対象外です: {current}");
            current = Path.GetDirectoryName(current);
        }
    }

    private static void ValidateUnchanged(string root, FileAnalysis file)
    {
        string path = Resolve(root, file.RelativePath);
        EnsureNoLinks(path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) throw new IOException($"読み取り専用: {path}");
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(file.OriginalBytes))
            throw new IOException($"解析後に変更されています。再解析してください: {path}");
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string temp = path + ".ueetc-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
