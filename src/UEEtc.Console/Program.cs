using UEEtc.Core;

return Run(args);

static int Run(string[] arguments)
{
    if (arguments.Length == 0 || arguments is ["--help"] or ["-h"])
    {
        Console.WriteLine("UEEtc - Unreal Engine trace instrumentation (.NET 10)");
        Console.WriteLine("Usage: UEEtc.Console <source-full-path> [--cpu-only | --counter-only] [--report <output-full-path>] [--apply]");
        Console.WriteLine("Default: preview only. --report exports JSON, a review patch and modified copies.");
        Console.WriteLine("--apply writes source files after validating the preview and creating backups.");
        return 0;
    }
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        string root = arguments[0];
        bool apply = false;
        string? report = null;
        InstrumentationOptions options = new();
        bool modeChosen = false;
        for (int i = 1; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--cpu-only" when !modeChosen:
                    options = new(true, false); modeChosen = true; break;
                case "--counter-only" when !modeChosen:
                    options = new(false, true); modeChosen = true; break;
                case "--report" when i + 1 < arguments.Length && report is null:
                    report = arguments[++i]; break;
                case "--apply" when !apply:
                    apply = true; break;
                default:
                    throw new ArgumentException($"Unknown or duplicate option: {arguments[i]}");
            }
        }
        var service = new InstrumentationService();
        var plan = service.Analyze(root, options, cancellationToken: cancellation.Token);
        Console.WriteLine(ReportExporter.Summary(plan));
        foreach (var file in plan.Files.Where(f => f.HasChanges))
            Console.WriteLine($"  {file.RelativePath}: +{file.AddedFunctions} functions");
        foreach (var diagnostic in plan.Diagnostics.Concat(plan.Files.SelectMany(f => f.Diagnostics.Select(d => f.RelativePath + ": " + d))))
            Console.Error.WriteLine(diagnostic);
        cancellation.Token.ThrowIfCancellationRequested();
        if (report is not null)
        {
            ReportExporter.Export(plan, report);
            Console.WriteLine($"Report: {report}");
        }
        if (apply)
        {
            if (plan.DiagnosticCount > 0)
                throw new InvalidOperationException("診断のある解析結果は適用できません。レポートを確認してください。");
            if (plan.ChangedFiles > 0) Console.WriteLine($"Applied. Backup: {service.Apply(plan)}");
            else Console.WriteLine("No changes to apply.");
        }
        return plan.DiagnosticCount > 0 ? 1 : 0;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
    catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
    {
        Console.Error.WriteLine(error.Message);
        return 2;
    }
    finally { Console.CancelKeyPress -= cancel; }
}
