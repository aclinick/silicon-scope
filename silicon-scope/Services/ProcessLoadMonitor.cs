using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace silicon_scope.Services;

/// <summary>
/// Samples per-PID CPU, GPU, and NPU utilization at 250 ms cadence and pushes
/// the results to three <see cref="MetricSnapshot"/> instances on the UI thread.
///
/// CPU is computed from <see cref="Process.TotalProcessorTime"/> deltas
/// (faster and more robust than the Process performance counter category,
/// which has process-name disambiguation problems).
///
/// GPU and NPU come from the <c>GPU Engine \ Utilization Percentage</c>
/// performance counters, filtered by PID and by adapter LUID.
/// </summary>
public sealed class ProcessLoadMonitor : IDisposable
{
    private static readonly TimeSpan SamplePeriod = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(2);

    private static readonly Regex GpuInstanceRegex = new(
        @"pid_(?<pid>\d+)_luid_0x(?<high>[0-9A-Fa-f]+)_0x(?<low>[0-9A-Fa-f]+).+engtype_(?<engtype>[A-Za-z0-9]+)",
        RegexOptions.Compiled);

    private readonly DispatcherQueue _ui;
    private readonly NpuDetectionResult _npu;
    private readonly MetricSnapshot _cpu;
    private readonly MetricSnapshot _gpu;
    private readonly MetricSnapshot _npuMetric;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private IReadOnlyList<int> _trackedPids = Array.Empty<int>();
    private readonly object _pidLock = new();

    // CPU bookkeeping: PID -> (lastTotalProcTime, lastWallClock).
    private readonly Dictionary<int, (TimeSpan cpu, DateTime when)> _cpuSnapshots = new();
    private DateTime _lastCpuSampleTime = DateTime.UtcNow;
    private DateTime _lastSuccessfulSample = DateTime.UtcNow;

    public ProcessLoadMonitor(
        DispatcherQueue ui,
        NpuDetectionResult npu,
        MetricSnapshot cpu,
        MetricSnapshot gpu,
        MetricSnapshot npuMetric)
    {
        _ui = ui;
        _npu = npu;
        _cpu = cpu;
        _gpu = gpu;
        _npuMetric = npuMetric;

        _npuMetric.IsAvailable = _npu.IsPresent;
        _npuMetric.Subtitle = _npu.DisplayName;
    }

    public void SetTrackedPids(IReadOnlyList<int> pids)
    {
        lock (_pidLock)
        {
            _trackedPids = pids.ToArray();
            // Reset CPU snapshots so first sample doesn't show garbage spike.
            _cpuSnapshots.Clear();
        }
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => SampleLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _loop = null;
        _cts = null;
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SamplePeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    Sample();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcessLoadMonitor] Sample failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* expected on Stop */ }
    }

    private void Sample()
    {
        int[] pids;
        lock (_pidLock) pids = _trackedPids.ToArray();

        if (pids.Length == 0)
        {
            Publish(0, 0, 0, "no process selected", "no process selected", _npu.IsPresent ? "no process selected" : _npu.DisplayName, stale: false);
            return;
        }

        // CPU
        var (cpuPercent, coresActive) = SampleCpu(pids);

        // GPU + NPU from a single enumeration of GPU Engine instances.
        var (gpuPercent, npuPercent, gpuBreakdown, gpuAdapter) = SampleGpuAndNpu(pids);

        var any = cpuPercent > 0 || gpuPercent > 0 || npuPercent > 0;
        if (any) _lastSuccessfulSample = DateTime.UtcNow;
        var stale = DateTime.UtcNow - _lastSuccessfulSample > StaleThreshold;

        var cpuSubtitle = $"{coresActive:F1} of {Environment.ProcessorCount} cores active";
        var gpuSubtitle = string.IsNullOrEmpty(gpuAdapter)
            ? gpuBreakdown
            : $"{gpuAdapter} \u2022 {gpuBreakdown}";
        var npuSubtitle = _npu.IsPresent ? _npu.DisplayName : _npu.DisplayName;

        Publish(cpuPercent, gpuPercent, npuPercent, cpuSubtitle, gpuSubtitle, npuSubtitle, stale);
    }

    private (double percent, double coresActive) SampleCpu(int[] pids)
    {
        var now = DateTime.UtcNow;
        var wallDelta = (now - _lastCpuSampleTime).TotalMilliseconds;
        _lastCpuSampleTime = now;

        if (wallDelta < 1) return (0, 0);

        double totalCpuMs = 0;
        var alive = new HashSet<int>();
        // Pre-build the set of currently-running PIDs so we don't throw
        // ArgumentException (first-chance noise) from GetProcessById on
        // processes that have exited between picker refresh and sample.
        var runningPids = new HashSet<int>();
        foreach (var rp in Process.GetProcesses())
        {
            runningPids.Add(rp.Id);
            rp.Dispose();
        }
        foreach (var pid in pids)
        {
            if (!runningPids.Contains(pid)) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                alive.Add(pid);
                var cur = p.TotalProcessorTime;
                if (_cpuSnapshots.TryGetValue(pid, out var prev))
                {
                    var deltaMs = (cur - prev.cpu).TotalMilliseconds;
                    if (deltaMs > 0) totalCpuMs += deltaMs;
                }
                _cpuSnapshots[pid] = (cur, now);
            }
            catch
            {
                // Process exited or access denied. Drop it.
            }
        }

        // Clean dead PIDs.
        foreach (var dead in _cpuSnapshots.Keys.Where(k => !alive.Contains(k)).ToArray())
        {
            _cpuSnapshots.Remove(dead);
        }

        // CPU% normalized to logical core count: 100% means one full core.
        // Total cpuMs / wallMs gives cores busy; divide by core count for 0..100.
        var coresActive = totalCpuMs / wallDelta;
        var percent = Math.Min(100.0, coresActive / Environment.ProcessorCount * 100.0);
        return (percent, coresActive);
    }

    // Cache of (instance name -> counter). PerformanceCounter creation is cheap
    // but enumerating + recreating every tick is wasteful; we lazily build.
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();

    private (double gpuPercent, double npuPercent, string gpuBreakdown, string gpuAdapter)
        SampleGpuAndNpu(int[] pids)
    {
        double gpu = 0, npu = 0;
        var byEngType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string? gpuAdapterToken = null;

        try
        {
            var pidSet = new HashSet<int>(pids);
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames();

            foreach (var inst in instances)
            {
                var m = GpuInstanceRegex.Match(inst);
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups["pid"].Value, out var pid)) continue;
                if (!pidSet.Contains(pid)) continue;

                var luid = $"0x{m.Groups["high"].Value}_0x{m.Groups["low"].Value}";
                var engType = m.Groups["engtype"].Value;

                if (!_gpuCounters.TryGetValue(inst, out var counter))
                {
                    try
                    {
                        counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                        // Prime the counter; the first NextValue() on a rate counter
                        // returns 0 but is required to seed the baseline.
                        counter.NextValue();
                        _gpuCounters[inst] = counter;
                    }
                    catch
                    {
                        continue;
                    }
                }

                double value;
                try { value = counter.NextValue(); }
                catch { continue; }
                if (value < 0 || double.IsNaN(value)) continue;

                var isNpu = _npu.IsPresent &&
                            string.Equals(luid, _npu.LuidToken, StringComparison.OrdinalIgnoreCase);

                if (isNpu)
                {
                    npu += value;
                }
                else
                {
                    gpu += value;
                    gpuAdapterToken ??= luid;
                    if (!byEngType.TryGetValue(engType, out var sum)) sum = 0;
                    byEngType[engType] = sum + value;
                }
            }

            // Drop counters whose instance disappeared (process exit).
            var dead = _gpuCounters.Keys.Where(k => Array.IndexOf(instances, k) < 0).ToArray();
            foreach (var k in dead)
            {
                _gpuCounters[k].Dispose();
                _gpuCounters.Remove(k);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessLoadMonitor] GPU sample failed: {ex.Message}");
        }

        gpu = Math.Min(100.0, gpu);
        npu = Math.Min(100.0, npu);

        var breakdown = byEngType.Count == 0
            ? "no activity"
            : string.Join(", ", byEngType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:F0}%"));

        var adapter = gpuAdapterToken is null ? string.Empty : $"LUID {gpuAdapterToken}";
        return (gpu, npu, breakdown, adapter);
    }

    private void Publish(double cpuVal, double gpuVal, double npuVal,
        string cpuSub, string gpuSub, string npuSub, bool stale)
    {
        _ui.TryEnqueue(() =>
        {
            _cpu.Push(cpuVal);
            _cpu.Subtitle = cpuSub;
            _cpu.IsStale = stale;

            _gpu.Push(gpuVal);
            _gpu.Subtitle = gpuSub;
            _gpu.IsStale = stale;

            if (_npu.IsPresent)
            {
                _npuMetric.Push(npuVal);
                _npuMetric.Subtitle = npuSub;
                _npuMetric.IsStale = stale;
            }
            else
            {
                _npuMetric.Push(0);
                _npuMetric.Subtitle = _npu.DisplayName;
                _npuMetric.IsStale = false;
            }
        });
    }

    public void Dispose()
    {
        Stop();
        foreach (var c in _gpuCounters.Values) c.Dispose();
        _gpuCounters.Clear();
    }
}
