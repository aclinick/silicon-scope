namespace SiliconScope.Core;

/// <summary>
/// Live snapshot of a single metric column (CPU / GPU / NPU) for a process group.
///
/// Plain mutable state. Consumers (TUI render loop, future GUI) read these
/// fields on their own cadence. No INotifyPropertyChanged because that would
/// require either MVVM toolkit (heavy for a TUI) or hand-rolled property
/// change boilerplate (boilerplate for no benefit in a polling renderer).
///
/// Thread safety: assignments to primitive fields are atomic on .NET; the
/// renderer may see a slightly inconsistent mix of fields across one tick,
/// which is acceptable for a 4 Hz status readout.
/// </summary>
public sealed class MetricSnapshot
{
    public const int HistoryCapacity = 240; // 60 s at 250 ms cadence

    private readonly double[] _history = new double[HistoryCapacity];
    private int _historyCount;
    private int _historyHead;

    public double Value { get; set; }

    public string Subtitle { get; set; } = string.Empty;

    public bool IsStale { get; set; }

    public bool IsAvailable { get; set; } = true;

    public int HistoryCount => _historyCount;

    public void Push(double value)
    {
        Value = value;
        _history[_historyHead] = value;
        _historyHead = (_historyHead + 1) % HistoryCapacity;
        if (_historyCount < HistoryCapacity) _historyCount++;
    }

    /// <summary>
    /// Copies the last <paramref name="count"/> samples into the destination
    /// span, oldest-first. Returns the number of samples actually copied
    /// (less than <paramref name="count"/> if there is not yet enough history).
    /// </summary>
    public int CopyRecent(Span<double> destination)
    {
        var count = Math.Min(destination.Length, _historyCount);
        for (var i = 0; i < count; i++)
        {
            var idx = (_historyHead - count + i + HistoryCapacity) % HistoryCapacity;
            destination[i] = _history[idx];
        }
        return count;
    }

    /// <summary>
    /// Returns a 1-second rolling average of the most recent samples
    /// (4 samples at 250 ms cadence). Falls back to <see cref="Value"/>
    /// when fewer than 4 samples are present.
    /// </summary>
    public double RollingAverage(int samples = 4)
    {
        if (_historyCount == 0) return Value;
        var n = Math.Min(samples, _historyCount);
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var idx = (_historyHead - 1 - i + HistoryCapacity) % HistoryCapacity;
            sum += _history[idx];
        }
        return sum / n;
    }
}
