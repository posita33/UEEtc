using UEEtc.Blueprint;

if (args.Length is < 4 or > 5 || (args.Length == 5 && args[4] != "--keep-layout"))
{
    Console.WriteLine("UEEtc.Blueprint.Console <asset.uasset> <project.uproject> <UnrealEditor-Cmd.exe> <output-folder> [--keep-layout]");
    return args.Length == 0 ? 0 : 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var result = await new BlueprintAnalysisService().AnalyzeAsync(new(args[0], args[1], args[2], args[3], args.Length != 5),
        new Progress<string>(Console.WriteLine), cancellation.Token);
    Console.WriteLine("資料: " + result.MarkdownPath);
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("解析を中止しました。"); return 130; }
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
