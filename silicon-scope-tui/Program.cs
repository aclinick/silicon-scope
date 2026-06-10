using System.Diagnostics;
using System.Text;
using SiliconScope.Core;
using Spectre.Console;

namespace SiliconScope.Tui;

/// <summary>
/// silicon-scope TUI entry point. Renders a compact CPU / GPU / NPU readout
/// for one target process and its descendants in a Spectre.Console Live region.
///
/// Always-on-top is achieved by toggling the Windows Terminal pane's
/// "Always on top" setting. We do not implement window chrome.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan RenderPeriod = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TreeRefreshPeriod = TimeSpan.FromSeconds(5);

    static int Main(string[] args)
    {
        // Spectre.Console emits Unicode block glyphs (U+2588, U+2591). On
        // conhost the default OEM code page mangles these and beeps for every
        // byte it can't map. Force UTF-8 before any output.
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Redirected stdout: nothing we can do, but also no bell to fix.
        }

        var (process, pid, error) = ParseArgs(args);
        if (error is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            PrintUsage();
            return 1;
        }

        var target = ResolveTarget(process, pid);
        if (target is null)
        {
            return target is null && pid is null && process is null ? 1 : 2;
        }

        Run(target.Value.Pid, target.Value.Name);
        return 0;
    }

    // -- CLI parsing ---------------------------------------------------------

    private static (string? process, int? pid, string? error) ParseArgs(string[] args)
    {
        string? process = null;
        int? pid = null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help")
            {
                PrintUsage();
                Environment.Exit(0);
            }
            else if (a == "--process" && i + 1 < args.Length)
            {
                process = args[++i];
            }
            else if (a == "--pid" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out var v))
                    return (null, null, $"--pid expects an integer, got '{args[i]}'");
                pid = v;
            }
            else
            {
                return (null, null, $"unknown argument: {a}");
            }
        }

        if (process is not null && pid is not null)
            return (null, null, "specify either --process or --pid, not both");

        return (process, pid, null);
    }

    private static void PrintUsage()
    {
        AnsiConsole.MarkupLine("[bold]silicon-scope-tui[/]  per-process CPU / GPU / NPU readout");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("usage:");
        AnsiConsole.MarkupLine("  silicon-scope-tui --process <name>");
        AnsiConsole.MarkupLine("  silicon-scope-tui --pid <int>");
        AnsiConsole.MarkupLine("  silicon-scope-tui                  (interactive picker)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("for always-on-top, set the Windows Terminal pane's");
        AnsiConsole.MarkupLine("\"Always on top\" option (right-click tab > Always on top).");
    }

    // -- Target resolution ---------------------------------------------------

    private readonly record struct Target(int Pid, string Name);

    private static Target? ResolveTarget(string? processName, int? pid)
    {
        if (pid is { } p)
        {
            try
            {
                using var proc = Process.GetProcessById(p);
                return new Target(p, proc.ProcessName);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]no process with pid {p}: {Markup.Escape(ex.Message)}[/]");
                return null;
            }
        }

        if (processName is { } name)
        {
            return PickByName(name);
        }

        return InteractivePicker();
    }

    private static Target? PickByName(string name)
    {
        var matches = Process.GetProcessesByName(name);
        try
        {
            if (matches.Length == 0)
            {
                AnsiConsole.MarkupLine($"[red]no process named '{Markup.Escape(name)}' is running[/]");
                return null;
            }
            if (matches.Length == 1)
            {
                return new Target(matches[0].Id, matches[0].ProcessName);
            }
            // Multiple instances: pick the one with the highest working set
            // (heuristic: the "main" instance, not a helper).
            var winner = matches.OrderByDescending(p =>
            {
                try { return p.WorkingSet64; }
                catch { return 0L; }
            }).First();
            AnsiConsole.MarkupLine($"[yellow]{matches.Length} processes named '{Markup.Escape(name)}', picking pid {winner.Id} (largest WS)[/]");
            return new Target(winner.Id, winner.ProcessName);
        }
        finally
        {
            foreach (var m in matches) m.Dispose();
        }
    }

    private static Target? InteractivePicker()
    {
        // Materialize lightweight DTOs so we can dispose every Process from
        // GetProcesses() before showing the picker.
        var all = Process.GetProcesses();
        var candidates = new List<(int Pid, string Name, long Ws)>(all.Length);
        try
        {
            foreach (var p in all)
            {
                try
                {
                    var ws = p.WorkingSet64;
                    if (ws > 50 * 1024 * 1024)
                    {
                        candidates.Add((p.Id, p.ProcessName, ws));
                    }
                }
                catch { /* exited / access denied */ }
            }
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }

        var snapshot = candidates
            .OrderByDescending(c => c.Ws)
            .Take(30)
            .ToArray();

        if (snapshot.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]no candidate processes found[/]");
            return null;
        }

        var choices = snapshot
            .Select(c => $"{c.Name,-30}  pid {c.Pid,-7}  {c.Ws / 1024 / 1024,5} MB")
            .ToArray();
        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("pick a process to monitor:")
                .PageSize(15)
                .AddChoices(choices));
        var idx = Array.IndexOf(choices, pick);
        return new Target(snapshot[idx].Pid, snapshot[idx].Name);
    }

    // -- Main render loop ----------------------------------------------------

    private static void Run(int rootPid, string rootName)
    {
        var npu = new NpuDetectionService().Detect();
        var cpu = new MetricSnapshot();
        var gpu = new MetricSnapshot();
        var npuMetric = new MetricSnapshot();
        using var monitor = new ProcessLoadMonitor(npu, cpu, gpu, npuMetric);

        var tree = new ProcessTreeService();
        var trackedPids = tree.ExpandToTree(rootPid, rootName);
        monitor.SetTrackedPids(trackedPids);
        monitor.Start();

        var lastTreeRefresh = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var panel = BuildPanel(rootPid, rootName, trackedPids.Count, cpu, gpu, npuMetric, npu);
        AnsiConsole.Live(panel).Start(ctx =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (lastTreeRefresh.Elapsed >= TreeRefreshPeriod)
                {
                    try
                    {
                        trackedPids = tree.ExpandToTree(rootPid, rootName);
                        monitor.SetTrackedPids(trackedPids);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Program] tree refresh failed: {ex.Message}");
                    }
                    lastTreeRefresh.Restart();
                }

                ctx.UpdateTarget(BuildPanel(rootPid, rootName, trackedPids.Count, cpu, gpu, npuMetric, npu));

                var quit = false;
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.KeyChar is 'q' or 'Q')
                    {
                        quit = true;
                        break;
                    }
                }
                if (quit) break;

                Thread.Sleep(RenderPeriod);
            }
        });

        monitor.Stop();
    }

    private static Panel BuildPanel(
        int rootPid,
        string rootName,
        int pidCount,
        MetricSnapshot cpu,
        MetricSnapshot gpu,
        MetricSnapshot npuMetric,
        NpuDetectionResult npu)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Width(5));
        grid.AddColumn(new GridColumn().Width(12));
        grid.AddColumn(new GridColumn().PadLeft(2));

        AddRow(grid, "CPU", cpu, available: true);
        AddRow(grid, "GPU", gpu, available: true);
        AddRow(grid, "NPU", npuMetric, available: npu.IsPresent);

        var title = pidCount > 1
            ? $" [bold]{Markup.Escape(rootName)}[/] [dim](pid {rootPid} + {pidCount - 1})[/] "
            : $" [bold]{Markup.Escape(rootName)}[/] [dim](pid {rootPid})[/] ";

        return new Panel(grid)
            .Header(title, Justify.Left)
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static void AddRow(Grid grid, string label, MetricSnapshot m, bool available)
    {
        if (!available)
        {
            grid.AddRow(
                $"[grey]{label}[/]",
                "[grey]--[/]",
                $"[grey]{Markup.Escape(m.Subtitle)}[/]");
            return;
        }

        var avg = m.RollingAverage();
        var bar = BuildBar(avg);
        var valueStyle = m.IsStale ? "grey" : "white";
        var value = $"[{valueStyle}]{avg,5:F1}%[/]";
        grid.AddRow(
            $"[bold]{label}[/]",
            $"{bar} {value}",
            $"[dim]{Markup.Escape(m.Subtitle)}[/]");
    }

    private static string BuildBar(double percent)
    {
        // 10-cell Unicode block bar matching btop sensibilities.
        const int cells = 10;
        const string fullBlock = "\u2588";    // █
        const string lightBlock = "\u2591";   // ░
        var clamped = Math.Clamp(percent, 0.0, 100.0);
        var filled = (int)Math.Round(clamped / 100.0 * cells);
        var color = clamped switch
        {
            < 33 => "green",
            < 66 => "yellow",
            _ => "red"
        };
        return $"[{color}]{string.Concat(Enumerable.Repeat(fullBlock, filled))}[/][grey]{string.Concat(Enumerable.Repeat(lightBlock, cells - filled))}[/]";
    }
}
