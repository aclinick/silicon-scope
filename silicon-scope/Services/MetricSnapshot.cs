using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace silicon_scope.Services;

/// <summary>
/// Live snapshot of a single metric column (CPU / GPU / NPU) for a process group.
/// Bindable; updated from the UI thread by <see cref="ProcessLoadMonitor"/>.
/// </summary>
public partial class MetricSnapshot : ObservableObject
{
    public const int HistoryCapacity = 240; // 60 s at 250 ms cadence

    [ObservableProperty]
    public partial double Value { get; set; }

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStale { get; set; }

    [ObservableProperty]
    public partial bool IsAvailable { get; set; } = true;

    public ObservableCollection<double> History { get; } = new();

    public void Push(double value)
    {
        Value = value;
        History.Add(value);
        while (History.Count > HistoryCapacity)
        {
            History.RemoveAt(0);
        }
    }
}
