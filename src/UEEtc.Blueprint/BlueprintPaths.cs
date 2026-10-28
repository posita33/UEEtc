using System.Text.Json;

namespace UEEtc.Blueprint;

public static class BlueprintPaths
{
    public static string DefaultOutputRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEEtc", "BlueprintReports");

    public static string? FindProject(string assetPath)
    {
        if (!Path.IsPathFullyQualified(assetPath) || !File.Exists(assetPath)) return null;
        for (var folder = new DirectoryInfo(Path.GetDirectoryName(assetPath)!); folder is not null; folder = folder.Parent)
        {
            FileInfo[] projects;
            try { projects = folder.GetFiles("*.uproject"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            if (projects.Length == 1) return projects[0].FullName;
            if (projects.Length > 1) return null;
        }
        return null;
    }

    public static string FindEditor(string? projectPath = null)
    {
        string version = "5.8";
        if (File.Exists(projectPath))
        {
            try
            {
                using var project = JsonDocument.Parse(File.ReadAllText(projectPath));
                if (project.RootElement.ValueKind == JsonValueKind.Object &&
                    project.RootElement.TryGetProperty("EngineAssociation", out var association) && association.ValueKind == JsonValueKind.String &&
                    Version.TryParse(association.GetString(), out _)) version = association.GetString()!;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Epic Games", "UE_" + version, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
    }

    public static string PackagePath(string assetPath, string projectPath)
    {
        var mount = FindMount(assetPath, projectPath);
        return "/" + mount.Name + "/" + Path.ChangeExtension(
            Path.GetRelativePath(mount.Content, assetPath), null).Replace('\\', '/');
    }

    private static (string Name, string Content) FindMount(string assetPath, string projectPath)
    {
        string content = Path.Combine(Path.GetDirectoryName(projectPath)!, "Content");
        if (IsInside(assetPath, content)) return ("Game", content);
        for (var folder = new DirectoryInfo(Path.GetDirectoryName(assetPath)!); folder is not null; folder = folder.Parent)
        {
            if (!folder.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) || folder.Parent is null) continue;
            var plugins = folder.Parent.GetFiles("*.uplugin");
            if (plugins.Length == 1) return (Path.GetFileNameWithoutExtension(plugins[0].Name), folder.FullName);
        }
        throw new ArgumentException(".uassetは所属プロジェクト、または有効なプラグインのContent内にある必要があります。");

    }

    public static BlueprintAnalysisOptions Validate(BlueprintAnalysisOptions options)
    {
        string asset = InputFile(options.AssetPath, ".uasset", "Blueprint");
        string project = InputFile(options.ProjectPath, ".uproject", "所属プロジェクト");
        string editor = InputFile(options.EditorPath, ".exe", "UnrealEditor-Cmd");
        if (!Path.GetFileName(editor).Equals("UnrealEditor-Cmd.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("対応バージョンのUnrealEditor-Cmd.exeを指定してください。");
        if (string.IsNullOrWhiteSpace(options.OutputRoot) || !Path.IsPathFullyQualified(options.OutputRoot))
            throw new ArgumentException("資料の出力先をフルパスで指定してください。");
        string output = Path.GetFullPath(options.OutputRoot);
        if (File.Exists(output)) throw new ArgumentException("資料の出力先にはファイルではなくフォルダーを指定してください。");
        var mount = FindMount(asset, project);
        if (IsInside(output, Path.GetDirectoryName(project)!) || IsInside(output, mount.Content))
            throw new ArgumentException("資料の出力先はUEプロジェクトと解析対象のContentの外に指定してください。");
        EnsureNoLinks(asset); EnsureNoLinks(project); EnsureNoLinks(output);
        return options with { AssetPath = asset, ProjectPath = project, EditorPath = editor, OutputRoot = output };
    }

    public static bool IsInside(string path, string root)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
    public static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("リンク／ジャンクションは対象外です: " + current);
    }
    private static string InputFile(string path, string extension, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException(label + "の実在するフルパスを指定してください。");
        return Path.GetFullPath(path);
    }
}
