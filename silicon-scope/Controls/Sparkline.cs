using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace silicon_scope.Controls;

/// <summary>
/// Lightweight sparkline that renders a single <see cref="Polyline"/> over a
/// rolling window of doubles. No third-party charting library. Re-renders
/// on <see cref="INotifyCollectionChanged"/> events from the bound source
/// and on size changes.
///
/// Values are expected in the 0..100 range (CPU/GPU/NPU percent). The Y
/// axis is inverted so 100 sits at the top of the control.
/// </summary>
public sealed class Sparkline : Control
{
    private Polyline? _polyline;
    private Grid? _root;

    public Sparkline()
    {
        DefaultStyleKey = typeof(Sparkline);
        SizeChanged += (_, _) => Render();
    }

    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(object), typeof(Sparkline),
            new PropertyMetadata(null, OnValuesChanged));

    public object? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush), typeof(Sparkline),
            new PropertyMetadata(null, (d, _) => ((Sparkline)d).Render()));

    public Brush? StrokeBrush
    {
        get => (Brush?)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public static readonly DependencyProperty MaxValueProperty =
        DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(Sparkline),
            new PropertyMetadata(100.0, (d, _) => ((Sparkline)d).Render()));

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sl = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged oldNcc) oldNcc.CollectionChanged -= sl.OnSourceChanged;
        if (e.NewValue is INotifyCollectionChanged newNcc) newNcc.CollectionChanged += sl.OnSourceChanged;
        sl.Render();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Render();

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _root = GetTemplateChild("PART_Root") as Grid;
        _polyline = GetTemplateChild("PART_Polyline") as Polyline;
        Render();
    }

    private void Render()
    {
        if (_polyline is null) return;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        _polyline.Points.Clear();
        if (StrokeBrush is not null) _polyline.Stroke = StrokeBrush;

        if (Values is not IEnumerable<double> source) return;
        var arr = source as IList<double> ?? source.ToList();
        if (arr.Count < 2) return;

        var max = Math.Max(1.0, MaxValue);
        var stepX = arr.Count == 1 ? width : width / (arr.Count - 1);
        var points = new PointCollection();
        for (int i = 0; i < arr.Count; i++)
        {
            var v = Math.Clamp(arr[i], 0, max);
            var x = i * stepX;
            var y = height - (v / max) * height;
            points.Add(new Point(x, y));
        }
        _polyline.Points = points;
    }
}
