using System.Text;
using System.Text.Json;
using UEEtc.Blueprint;

namespace UEEtc.Blueprint.Cpp;

public sealed class BlueprintCppConversionService(IBlueprintAnalysisService? analysis = null) : IBlueprintCppConversionService
{
    private readonly IBlueprintAnalysisService analysis = analysis ?? new BlueprintAnalysisService();
    public async Task<BlueprintCppResult> ConvertAsync(BlueprintAnalysisOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await analysis.AnalyzeAsync(options, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("抽出したBlueprintからC++を生成中（実験）…");
        var code = BlueprintCppEmitter.Generate(result.Report, cancellationToken);
        string directory = Path.Combine(result.Directory, "Cpp"), staging = Path.Combine(result.Directory, ".cpp-" + Guid.NewGuid().ToString("N"));
        BlueprintPaths.EnsureNoLinks(staging); Directory.CreateDirectory(staging);
        try
        {
            var files = PluginFiles(code);
            foreach (var file in files)
            {
                string path = Path.Combine(staging, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, file.Value, new UTF8Encoding(false), cancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "conversion.json"), JsonSerializer.Serialize(new
            {
                code.ClassName, code.IsComplete, code.FunctionCount, code.Diagnostics, result.Report.AssetPath, result.Report.AssetSha256,
                Limits = "Experimental subset only; no gameplay equivalence or build guarantee."
            }, BlueprintReportWriter.JsonOptions), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "conversion.md"), Markdown(code), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            BlueprintPaths.EnsureNoLinks(directory); Directory.Move(staging, directory);
            return new(result, directory, code);
        }
        finally
        {
            if (!BlueprintPaths.IsInside(staging, result.Directory)) throw new IOException("C++作業フォルダーが想定外です。");
            BlueprintPaths.EnsureNoLinks(staging);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static IReadOnlyDictionary<string, string> PluginFiles(CppConversionOutput code) => new Dictionary<string, string>
    {
        ["BlueprintConversion/BlueprintConversion.uplugin"] = """
        {"FileVersion":3,"Version":1,"VersionName":"0.1","FriendlyName":"Experimental Blueprint Conversion","CanContainContent":false,"Modules":[{"Name":"BlueprintConversion","Type":"Runtime","LoadingPhase":"Default"}]}
        """,
        ["BlueprintConversion/Source/BlueprintConversion/BlueprintConversion.Build.cs"] = """
        using UnrealBuildTool;
        public class BlueprintConversion : ModuleRules
        {
            public BlueprintConversion(ReadOnlyTargetRules Target) : base(Target)
            {
                PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
                PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine" });
            }
        }
        """,
        ["BlueprintConversion/Source/BlueprintConversion/Private/Module.cpp"] = "#include \"Modules/ModuleManager.h\"\nIMPLEMENT_MODULE(FDefaultModuleImpl, BlueprintConversion)\n",
        ["BlueprintConversion/Source/BlueprintConversion/Public/ConvertedBlueprint.h"] = code.Header,
        ["BlueprintConversion/Source/BlueprintConversion/Private/ConvertedBlueprint.cpp"] = code.Implementation
    };

    public static string Markdown(CppConversionOutput code)
    {
        static string Cell(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "&#124;").Replace("\r", "").Replace("\n", "<br>");
        var text = new StringBuilder("# Blueprint → C++ 実験的変換\n\n");
        text.AppendLine(code.IsComplete ? "対応範囲内のコードを生成しました。コンパイル・実機動作・ゲーム全体の等価性は別途確認してください。" : "**未対応箇所のある下書きです。既定では#errorでコンパイルを停止します。** conversion.jsonと元資料を確認してください。");
        text.AppendLine($"\nクラス: {code.ClassName} / 生成関数: {code.FunctionCount}\n");
        text.AppendLine("UEのBuildPluginでCpp/BlueprintConversionをビルドし、コピーしたプロジェクトで検証してください。元BPとの置き換えやBPI接続は自動では行いません。\n");
        text.AppendLine("IsCompleteはこの実験の対応範囲に未対応診断がないことだけを表し、完全な移植の証明ではありません。\n");
        text.AppendLine("未完成コードの確認目的でのみ、ビルド側でUEETC_ALLOW_INCOMPLETE_BLUEPRINTを定義できます。未変換ノードへ到達するとエラーを記録して関数を終了します。\n");
        text.AppendLine("| 種類 | グラフ | ノード | 内容 |\n| --- | --- | --- | --- |");
        foreach (var diagnostic in code.Diagnostics) text.AppendLine($"| {Cell(diagnostic.Severity)} | {Cell(diagnostic.Graph)} | {Cell(diagnostic.Node)} | {Cell(diagnostic.Message)} |");
        return text.ToString();
    }
}
