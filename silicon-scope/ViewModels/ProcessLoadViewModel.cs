using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using silicon_scope.Services;

namespace silicon_scope.ViewModels;

/// <summary>
/// One process group's worth of live CPU / GPU / NPU data. The
/// <see cref="ProcessLoadMonitor"/> owns the timer and pushes values into
/// the three <see cref="MetricSnapshot"/> instances exposed here.
/// </summary>
public partial class ProcessLoadViewModel : ObservableObject, IDisposable
{
    private readonly ProcessLoadMonitor _monitor;
    private readonly ProcessTreeService _treeService;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TrackedPidsSubtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public MetricSnapshot Cpu { get; } = new();
    public MetricSnapshot Gpu { get; } = new();
    public MetricSnapshot Npu { get; } = new();

    public ProcessLoadViewModel(DispatcherQueue ui, NpuDetectionResult npu, ProcessTreeService treeService)
    {
        _treeService = treeService;
        _monitor = new ProcessLoadMonitor(ui, npu, Cpu, Gpu, Npu);
    }

    public void Track(int rootPid, string processName)
    {
        DisplayName = $"{processName} ({rootPid})";
        IsActive = true;

        var pids = _treeService.ExpandToTree(rootPid, processName);
        TrackedPidsSubtitle = pids.Count == 1
            ? $"tracking PID {pids[0]}"
            : $"tracking {pids.Count} PIDs: {string.Join(", ", pids)}";

        _monitor.SetTrackedPids(pids);
        _monitor.Start();
    }

    public void Clear()
    {
        IsActive = false;
        DisplayName = string.Empty;
        TrackedPidsSubtitle = string.Empty;
        _monitor.SetTrackedPids(Array.Empty<int>());
        _monitor.Stop();
    }

    public void Dispose()
    {
        _monitor.Dispose();
    }
}
