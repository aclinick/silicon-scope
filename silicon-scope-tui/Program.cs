using System.Diagnostics;
using System.Text;
using SiliconScope.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

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

    // Display state, tweened toward the live RollingAverage every frame so
    // updates feel smooth instead of snapping. Sampler still runs at 4 Hz in
    // Core; the renderer interpolates between samples at ~30 fps.
    private sealed class DisplayState
    {
        public double Cpu, Gpu, Npu;
    }

    private static readonly TimeSpan RenderFrame = TimeSpan.FromMilliseconds(33);   // ~30 fps
    private const double EasePerFrame = 0.22;                                       // 0..1

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

        var state = new DisplayState();
        var spinnerFrame = 0;
        var renderable = BuildRenderable(rootPid, rootName, trackedPids.Count, state, cpu, gpu, npuMetric, npu, spinnerFrame);
        AnsiConsole.Live(renderable).Overflow(VerticalOverflow.Crop).AutoClear(false).Start(ctx =>
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

                // Ease displayed values toward the latest rolling average.
                state.Cpu += (cpu.RollingAverage() - state.Cpu) * EasePerFrame;
                state.Gpu += (gpu.RollingAverage() - state.Gpu) * EasePerFrame;
                state.Npu += (npuMetric.RollingAverage() - state.Npu) * EasePerFrame;
                spinnerFrame++;

                try
                {
                    ctx.UpdateTarget(BuildRenderable(rootPid, rootName, trackedPids.Count, state, cpu, gpu, npuMetric, npu, spinnerFrame));
                }
                catch
                {
                    // Console resize / write race with another process: skip
                    // this frame, the next one will redraw cleanly.
                }

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

                Thread.Sleep(RenderFrame);
            }
        });

        monitor.Stop();
    }

    private static IRenderable BuildRenderable(
        int rootPid,
        string rootName,
        int pidCount,
        DisplayState state,
        MetricSnapshot cpu,
        MetricSnapshot gpu,
        MetricSnapshot npuMetric,
        NpuDetectionResult npu,
        int spinnerFrame)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());                  // label
        grid.AddColumn(new GridColumn().NoWrap().PadLeft(2));        // sparkline
        grid.AddColumn(new GridColumn().NoWrap().PadLeft(2));        // value

        AddRow(grid, "cpu", state.Cpu, cpu, available: true);
        AddRow(grid, "gpu", state.Gpu, gpu, available: true);
        AddRow(grid, "npu", state.Npu, npuMetric, available: npu.IsPresent);

        // Tiny breathing dot in the header so the user can tell it is live.
        const string spinner = "\u2022\u00b0\u2218\u00b0";  // • ° ∘ °
        var s = spinner[spinnerFrame % spinner.Length];
        var titleSuffix = pidCount > 1
            ? $" [grey](pid {rootPid} \u00b7 +{pidCount - 1})[/] "
            : $" [grey](pid {rootPid})[/] ";
        var titleMarkup = new Markup($"[bold]{Markup.Escape(rootName)}[/]{titleSuffix}[cyan1]{s}[/]");

        // Stack title above grid with one spacer row. No panel, no border:
        // Spectre's box-drawing math fights any width change and leaves a trail
        // of half-drawn boxes on resize. Stripping the chrome is the fix.
        return new Rows(titleMarkup, new Text(string.Empty), grid);
    }

    private static void AddRow(Grid grid, string label, double displayed, MetricSnapshot m, bool available)
    {
        if (!available)
        {
            grid.AddRow(
                $"[grey]{label}[/]",
                $"[grey15]{new string(' ', SparkCells)}[/]",
                "[grey]   n/a[/]");
            return;
        }

        var spark = RenderSparkline(m, SparkCells, m.IsStale);
        var valueColor = m.IsStale ? "grey" : GradientHex(displayed);
        var value = $"[{valueColor}]{displayed,5:F1}%[/]";
        grid.AddRow(
            $"[grey]{label}[/]",
            spark,
            value);
    }

    // -- Braille sparkline ---------------------------------------------------

    private const int SparkCells = 32;

    // Braille dot bit layout, low byte after U+2800:
    //   1 4
    //   2 5
    //   3 6
    //   7 8
    // Heights 0..4 from the bottom up on left column => 0, 0x40, 0x44, 0x46, 0x47.
    private static readonly byte[] LeftMask = { 0x00, 0x40, 0x44, 0x46, 0x47 };
    // Right column: 0, 0x80, 0xA0, 0xB0, 0xB8.
    private static readonly byte[] RightMask = { 0x00, 0x80, 0xA0, 0xB0, 0xB8 };

    private static string RenderSparkline(MetricSnapshot m, int cells, bool stale)
    {
        var samples = cells * 2;                                      // 2 samples per braille cell
        Span<double> buf = stackalloc double[samples];
        var copied = m.CopyRecent(buf);
        // Pad oldest entries with zero so the bar grows in from the right.
        if (copied < samples)
        {
            var shift = samples - copied;
            for (var i = samples - 1; i >= shift; i--) buf[i] = buf[i - shift];
            for (var i = 0; i < shift; i++) buf[i] = 0;
        }

        var sb = new StringBuilder(cells * 18);
        string? openColor = null;
        for (var c = 0; c < cells; c++)
        {
            var leftVal = buf[c * 2];
            var rightVal = buf[c * 2 + 1];
            var lh = HeightFromPercent(leftVal);
            var rh = HeightFromPercent(rightVal);
            var ch = (char)(0x2800 + LeftMask[lh] + RightMask[rh]);

            var avg = (leftVal + rightVal) * 0.5;
            var color = stale ? "grey50" : (lh + rh == 0 ? "grey15" : GradientHex(avg));

            if (color != openColor)
            {
                if (openColor is not null) sb.Append("[/]");
                sb.Append('[').Append(color).Append(']');
                openColor = color;
            }
            sb.Append(ch);
        }
        if (openColor is not null) sb.Append("[/]");
        return sb.ToString();
    }

    private static int HeightFromPercent(double percent)
    {
        var clamped = Math.Clamp(percent, 0.0, 100.0);
        // 0% -> 0 dots, >0..25 -> 1, ..50 -> 2, ..75 -> 3, ..100 -> 4
        if (clamped <= 0) return 0;
        if (clamped < 25) return 1;
        if (clamped < 50) return 2;
        if (clamped < 75) return 3;
        return 4;
    }

    // 3-stop gradient: cyan (cold) -> amber (warm) -> magenta (hot).
    // Returns a "#RRGGBB" hex usable in Spectre markup.
    private static string GradientHex(double percent)
    {
        var t = Math.Clamp(percent, 0.0, 100.0) / 100.0;
        // (R, G, B) stops
        (int r, int g, int b) cold = (0x4C, 0xC9, 0xE6);   // soft cyan
        (int r, int g, int b) warm = (0xF5, 0xC2, 0x42);   // amber
        (int r, int g, int b) hot  = (0xE0, 0x4F, 0xC0);   // magenta

        (int r, int g, int b) c;
        if (t < 0.5)
        {
            var u = t / 0.5;
            c = Lerp(cold, warm, u);
        }
        else
        {
            var u = (t - 0.5) / 0.5;
            c = Lerp(warm, hot, u);
        }
        return $"#{c.r:X2}{c.g:X2}{c.b:X2}";

        static (int r, int g, int b) Lerp((int r, int g, int b) a, (int r, int g, int b) b, double u)
        {
            int Mix(int x, int y) => (int)Math.Round(x + (y - x) * u);
            return (Mix(a.r, b.r), Mix(a.g, b.g), Mix(a.b, b.b));
        }
    }
}
