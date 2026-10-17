using UEEtc.Core.Features.FunctionTracing;
using UEEtc.Core.Plans;
using UEEtc.Core.Reporting;
using UEEtc.Core.Features.EventCounters;
using UEEtc.Core.Features.CombinedTracing;
using UEEtc.Core.Source;
using System.Text.Json;

return Run(args);

static int Run(string[] arguments)
{
    if (arguments.Length == 0 || arguments is ["--help"] or ["-h"])
    {
        Console.WriteLine("UEEtc - Unreal Engine trace instrumentation (.NET 10)");
        Console.WriteLine("Usage: UEEtc.Console cpu <source-full-path> [--all-functions] [--report <empty-output-full-path>] [--apply]");
        Console.WriteLine("       UEEtc.Console combined <source-full-path> [--all-functions] [--report <empty-output-full-path>] [--apply]");
        Console.WriteLine("combined previews CPU scopes and all eligible event branches with recommended ADD/scoped counters in one plan. It is different from legacy --both function-entry counts.");
        Console.WriteLine("cpu defaults to static-evidence filtering (loops, known data/wait APIs, 4+ action calls). Cost is not measured. --all-functions retains broad instrumentation.");
        Console.WriteLine("CPU always emits a fixed string argument. Default: TRACE_CPUPROFILER_EVENT_SCOPE_STR. --cpu-macro-name <identifier> and --cpu-header <include-path> select a project wrapper/header for cpu, combined and legacy commands.");
        Console.WriteLine("       UEEtc.Console counters <source-full-path> [--selection <selection.json>] [--recommended-add | --recommended-counters] [--report <empty-output-full-path>] [--apply]");
        Console.WriteLine("counters: discover braced if/else branches; select none by default. --report exports candidates.json and an empty selection.json.");
        Console.WriteLine("Copy reviewed Id / CounterName / DisplayName into Selections; --selection validates the source fingerprint before preview/apply.");
        Console.WriteLine("Optional Quantity: { CounterName, DisplayName, Value, Unit, MeaningConfirmed: true }. Value: listed AddValues or decimal uint32 constant. Count and quantity are separate metrics.");
        Console.WriteLine("--recommended-add adds recommended input quantities to reviewed selections without a manual Quantity. Known const UE array Num() values are supported. Input quantities are not successes or completed work.");
        Console.WriteLine("--recommended-counters also adds a separate ActiveScopes INC/exit-DEC pair for direct input-array batches, preserving cumulative counts. Applies to reviewed selections only.");
        Console.WriteLine("Selection Measurement: Cumulative (default) increments only; ActiveScope adds ON_SCOPE_EXIT with a matching DECREMENT for this branch's lexical lifetime, not asynchronous work.");
        Console.WriteLine("CPU and event counters are separate features. Counters measure selected branch entries, not inferred successes.");
        Console.WriteLine("Legacy entry-count commands (retained for existing scripts):");
        Console.WriteLine("Usage: UEEtc.Console <source-full-path> [--cpu-only | --counter-only | --both] [--report <output-full-path>] [--apply]");
        Console.WriteLine("Default: CPU scopes only, preview only. Counters are optional function entry counts, not success/hit counts.");
        Console.WriteLine("--both opts into CPU scopes and entry counters. Existing tracing is preserved independently by kind.");
        Console.WriteLine("--report exports JSON, a review patch and modified copies.");
        Console.WriteLine("--apply writes source files after validating the preview and creating backups.");
        Console.WriteLine("--backup-dir <full-path> sets a backup folder outside the source tree for every command. Default: " + BackupStorage.DefaultRoot);
        return 0;
    }
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        if (arguments[0] == "counters") return RunCounters(arguments[1..], cancellation.Token);
        bool cpuCommand = arguments[0] == "cpu", combinedCommand = arguments[0] == "combined";
        if (cpuCommand || combinedCommand) arguments = arguments[1..];
        if (arguments.Length == 0) throw new ArgumentException("ソースフォルダーのフルパスを指定してください。");
        string root = arguments[0];
        bool apply = false;
        string? report = null, backupRoot = null;
        InstrumentationOptions options = new(FocusedCpu: cpuCommand || combinedCommand);
        bool modeChosen = false;
        bool macroChosen = false, headerChosen = false;
        for (int i = 1; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--cpu-only" when !modeChosen && !combinedCommand:
                    options = options with { CpuScopes = true, CallCounters = false }; modeChosen = true; break;
                case "--all-functions" when (cpuCommand || combinedCommand) && options.FocusedCpu:
                    options = options with { FocusedCpu = false }; break;
                case "--counter-only" when !modeChosen && !cpuCommand && !combinedCommand:
                    options = options with { CpuScopes = false, CallCounters = true }; modeChosen = true; break;
                case "--both" when !modeChosen && !cpuCommand && !combinedCommand:
                    options = options with { CpuScopes = true, CallCounters = true }; modeChosen = true; break;
                case "--cpu-macro-name" when !macroChosen && i + 1 < arguments.Length:
                    options = options with { CpuTrace = (options.CpuTrace ?? new()) with { MacroName = arguments[++i] } };
                    macroChosen = true; break;
                case "--cpu-header" when !headerChosen && i + 1 < arguments.Length:
                    options = options with { CpuTrace = (options.CpuTrace ?? new()) with { Header = arguments[++i] } };
                    headerChosen = true; break;
                case "--report" when i + 1 < arguments.Length && report is null:
                    report = arguments[++i]; break;
                case "--apply" when !apply:
                    apply = true; break;
                case "--backup-dir" when backupRoot is null && i + 1 < arguments.Length:
                    backupRoot = arguments[++i]; break;
                default:
                    throw new ArgumentException($"Unknown or duplicate option: {arguments[i]}");
            }
        }
        var service = new InstrumentationService();
        if (options.CallCounters) Console.Error.WriteLine("Legacy function-entry counts selected. For reviewed event locations use the separate counters command.");
        var plan = combinedCommand ? new CombinedTracingService().Analyze(root, options.FocusedCpu, options.CpuTrace, cancellationToken: cancellation.Token)
            : service.Analyze(root, options, cancellationToken: cancellation.Token);
        Console.WriteLine(ReportExporter.Summary(plan));
        foreach (var file in plan.Files.Where(f => f.HasChanges))
            Console.WriteLine($"  {file.RelativePath}: +{file.AddedFunctions} " + (combinedCommand ? "instrumentation sites" : "functions"));
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
            if (plan.ChangedFiles > 0) Console.WriteLine($"Applied. Backup: {service.Apply(plan, backupRoot)}");
            else Console.WriteLine("No changes to apply.");
        }
        return plan.DiagnosticCount > 0 ? 1 : 0;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
    catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException or JsonException)
    {
        Console.Error.WriteLine(error.Message);
        return 2;
    }
    finally { Console.CancelKeyPress -= cancel; }
}

static int RunCounters(string[] arguments, CancellationToken cancellation)
{
    if (arguments.Length == 0) throw new ArgumentException("ソースフォルダーのフルパスを指定してください。");
    string root = arguments[0]; string? report = null, selectionPath = null, backupRoot = null; bool apply = false, recommendedAdd = false, recommendedCounters = false;
    for (int i = 1; i < arguments.Length; i++)
    {
        switch (arguments[i])
        {
            case "--report" when report is null && i + 1 < arguments.Length: report = arguments[++i]; break;
            case "--selection" when selectionPath is null && i + 1 < arguments.Length: selectionPath = arguments[++i]; break;
            case "--apply" when !apply: apply = true; break;
            case "--backup-dir" when backupRoot is null && i + 1 < arguments.Length: backupRoot = arguments[++i]; break;
            case "--recommended-add" when !recommendedAdd: recommendedAdd = true; break;
            case "--recommended-counters" when !recommendedCounters: recommendedCounters = true; break;
            default: throw new ArgumentException("Unknown or duplicate option: " + arguments[i]);
        }
    }
    if (apply && selectionPath is null) throw new ArgumentException("適用には確認済みの --selection が必要です。");
    var service = new EventCounterService();
    var scan = service.Analyze(root, cancellationToken: cancellation);
    var selection = selectionPath is null ? service.CreateSelection(scan) :
        JsonSerializer.Deserialize<EventCounterSelectionFile>(File.ReadAllText(selectionPath), EventCounterService.JsonOptions)
            ?? throw new ArgumentException("選択ファイルが空です。");
    if (selection.Selections is null || selection.Selections.Any(s => s is null)) throw new ArgumentException("Selections に有効な選択一覧が必要です。");
    if (recommendedAdd || recommendedCounters)
    {
        var candidates = scan.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        selection = selection with { Selections = selection.Selections.Select(s =>
        {
            if (!candidates.TryGetValue(s.Id, out var c)) return s;
            var defaults = EventCounterDefaults.Create(c);
            return s with { Quantity = s.Quantity ?? defaults.Quantity,
                IncludeActiveScope = s.IncludeActiveScope || (recommendedCounters && s.Measurement == CounterMeasurement.Cumulative && defaults.IncludeActiveScope) };
        }).ToArray() };
    }
    var plan = service.BuildPlan(scan, selection, cancellation);
    Console.WriteLine($"イベント候補 {scan.Candidates.Count} / 選択 {selection.Selections.Count}");
    Console.WriteLine(ReportExporter.Summary(plan));
    foreach (var diagnostic in plan.Diagnostics.Concat(plan.Files.SelectMany(f => f.Diagnostics.Select(d => f.RelativePath + ": " + d)))) Console.Error.WriteLine(diagnostic);
    cancellation.ThrowIfCancellationRequested();
    if (report is not null)
    {
        ReportExporter.Export(plan, report);
        File.WriteAllText(Path.Combine(report, "candidates.json"), JsonSerializer.Serialize(scan.Candidates, EventCounterService.JsonOptions));
        File.WriteAllText(Path.Combine(report, "selection.json"), JsonSerializer.Serialize(selection, EventCounterService.JsonOptions));
        Console.WriteLine("Report: " + report);
    }
    if (apply)
    {
        if (plan.DiagnosticCount > 0) throw new InvalidOperationException("診断のある解析結果は適用できません。");
        if (plan.ChangedFiles > 0) Console.WriteLine("Applied. Backup: " + new SourceWorkspace().Apply(plan, backupRoot));
        else Console.WriteLine("No changes to apply.");
    }
    return plan.DiagnosticCount > 0 ? 1 : 0;
}
