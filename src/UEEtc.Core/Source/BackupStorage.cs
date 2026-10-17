namespace UEEtc.Core.Source;

public static class BackupStorage
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEEtc", "Backups");

    public static string ResolveRoot(string sourceRoot, string? backupRoot = null)
    {
        string requested = backupRoot ?? DefaultRoot;
        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested))
            throw new ArgumentException("バックアップ先のフルパスを指定してください。", nameof(backupRoot));
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        string backup = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        string prefix = Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar;
        if (backup.Equals(source, StringComparison.OrdinalIgnoreCase) || backup.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("バックアップ先はソースフォルダーの外に指定してください。", nameof(backupRoot));
        SourceWorkspace.EnsureNoLinks(backup);
        return backup;
    }
}
