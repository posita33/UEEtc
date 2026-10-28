using UEEtc.Blueprint;
using UEEtc.Blueprint.Cpp;

if (args.Length is < 4 or > 6 || args.Skip(4).Any(a => a is not ("--keep-layout" or "--cpp")) || args.Skip(4).Distinct().Count() != args.Length - 4)
{
    Console.WriteLine("UEEtc.Blueprint.Console <asset.uasset> <project.uproject> <UnrealEditor-Cmd.exe> <output-folder> [--keep-layout] [--cpp]");
    return args.Length == 0 ? 0 : 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var options = new BlueprintAnalysisOptions(args[0], args[1], args[2], args[3], !args.Contains("--keep-layout"));
    if (args.Contains("--cpp"))
    {
        var converted = await new BlueprintCppConversionService().ConvertAsync(options, new Progress<string>(Console.WriteLine), cancellation.Token);
        Console.WriteLine("C++: " + converted.Directory);
        Console.WriteLine(converted.Code.IsComplete ? "対応範囲内の生成が完了しました。ビルド・動作確認が必要です。" : "未対応箇所のある下書きです。conversion.mdを確認してください。");
        return converted.Code.IsComplete ? 0 : 3;
    }
    var result = await new BlueprintAnalysisService().AnalyzeAsync(options,
        new Progress<string>(Console.WriteLine), cancellation.Token);
    Console.WriteLine("資料: " + result.MarkdownPath);
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("解析を中止しました。"); return 130; }
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
