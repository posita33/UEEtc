using UEEtc.Core.Features.CombinedTracing;
using UEEtc.Core.Source;

internal static class BackupStorageTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        Case("Default backup location is outside source and under LocalApplicationData", (source, output) =>
        {
            string resolved = BackupStorage.ResolveRoot(source);
            Check(resolved == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEEtc", "Backups"));
            Check(!resolved.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }),
        Case("Custom backup stores original bytes and each apply has its own directory", (source, output) =>
        {
            var service = new CombinedTracingService(); var workspace = new SourceWorkspace();
            var plan = service.Analyze(source);
            string backup = workspace.Apply(plan, output);
            Check(Path.GetDirectoryName(backup) == output);
            Check(File.ReadAllBytes(Path.Combine(backup, "Nested/Work.cpp")).SequenceEqual(plan.Files.Single().OriginalBytes));
            Check(!Directory.Exists(Path.Combine(source, ".ueetc-backups")));
            File.WriteAllBytes(Path.Combine(source, "Nested/Work.cpp"), plan.Files.Single().OriginalBytes);
            string second = workspace.Apply(service.Analyze(source), output);
            Check(second != backup && Directory.Exists(backup) && Directory.Exists(second));
            Check(service.Analyze(source).ChangedFiles == 0);
        }),
        Case("Source root and descendants are rejected without source writes", (source, output) =>
        {
            var plan = new CombinedTracingService().Analyze(source);
            foreach (string invalid in new[] { source, source + Path.DirectorySeparatorChar, Path.Combine(source, "Backups"), Path.Combine(output, "..", "Source", "backup") })
                Reject<ArgumentException>(() => new SourceWorkspace().Apply(plan, invalid));
            Check(File.ReadAllBytes(Path.Combine(source, "Nested/Work.cpp")).SequenceEqual(plan.Files.Single().OriginalBytes));
            Check(!Directory.Exists(Path.Combine(source, "Backups")));
        }),
        Case("Blank and relative backup paths are rejected", (source, output) =>
        {
            foreach (string invalid in new[] { "", "  ", "Backups" })
                Reject<ArgumentException>(() => BackupStorage.ResolveRoot(source, invalid));
        }),
        Case("Backup storage failure leaves every original untouched", (source, output) =>
        {
            File.WriteAllText(Path.Combine(source, "Other.cpp"), "#include \"Fixture.h\"\nvoid F::Other() { if (Ready) { Run(); } }");
            var plan = new CombinedTracingService().Analyze(source);
            File.WriteAllText(output, "This is a file, not a directory");
            Reject<IOException>(() => new SourceWorkspace().Apply(plan, output));
            Check(plan.Files.All(file => File.ReadAllBytes(Path.Combine(source, file.RelativePath)).SequenceEqual(file.OriginalBytes)));
        })
    ];

    private static (string, Action) Case(string name, Action<string, string> action) => (name, () =>
    {
        string parent = Path.Combine(Path.GetTempPath(), "UEEtc-backup-tests");
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "Source"); Directory.CreateDirectory(Path.Combine(source, "Nested"));
        File.WriteAllText(Path.Combine(source, "Nested/Work.cpp"), "#include \"Fixture.h\"\nvoid F::Work() { if (Ready) { for (int i=0;i<4;++i) Use(); } }");
        try { action(source, Path.Combine(root, "Backups")); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary path");
            Directory.Delete(root, true);
        }
    });
    private static void Check(bool value) { if (!value) throw new Exception("Backup storage assertion failed"); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
