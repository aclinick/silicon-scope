using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using silicon_scope.Services;

namespace silicon_scope.Controls;

/// <summary>
/// One vertical column showing a single metric (CPU, GPU, or NPU) for the
/// primary process and, when pinned, the same metric for a second process
/// side-by-side. Bound from the parent via the <see cref="Primary"/> and
/// <see cref="Pinned"/> DependencyProperties; <see cref="IsPinnedVisible"/>
/// drives the column split.
///
/// The XAML hard-codes <c>Cpu</c> on the bound MetricSnapshots because each
/// instance of this control is configured by the parent to point at the
/// CPU / GPU / NPU snapshot via a wrapper MetricColumnViewModel.
/// </summary>
public sealed partial class BigNumberReadout : UserControl
{
    public BigNumberReadout() => InitializeComponent();

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(BigNumberReadout),
        new PropertyMetadata(string.Empty));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(
        nameof(Primary), typeof(MetricColumnView), typeof(BigNumberReadout),
        new PropertyMetadata(null));

    public MetricColumnView Primary
    {
        get => (MetricColumnView)GetValue(PrimaryProperty);
        set => SetValue(PrimaryProperty, value);
    }

    public static readonly DependencyProperty PinnedProperty = DependencyProperty.Register(
        nameof(Pinned), typeof(MetricColumnView), typeof(BigNumberReadout),
        new PropertyMetadata(null));

    public MetricColumnView Pinned
    {
        get => (MetricColumnView)GetValue(PinnedProperty);
        set => SetValue(PinnedProperty, value);
    }

    public static readonly DependencyProperty IsPinnedVisibleProperty = DependencyProperty.Register(
        nameof(IsPinnedVisible), typeof(bool), typeof(BigNumberReadout),
        new PropertyMetadata(false));

    public bool IsPinnedVisible
    {
        get => (bool)GetValue(IsPinnedVisibleProperty);
        set => SetValue(IsPinnedVisibleProperty, value);
    }

    // x:Bind function helpers ------------------------------------------------

    public static string FormatPercent(double value) => $"{value:F0}%";

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static GridLength PinnedWidth(bool visible) =>
        visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
}

/// <summary>
/// Tiny wrapper that exposes a single MetricSnapshot under the property name
/// <c>Cpu</c> so BigNumberReadout's XAML can reference <c>Primary.Cpu</c>
/// uniformly regardless of which underlying snapshot is being displayed.
/// Keeps the BigNumberReadout template generic instead of needing per-metric
/// variants.
/// </summary>
public sealed class MetricColumnView
{
    public MetricColumnView(string displayName, MetricSnapshot snapshot)
    {
        DisplayName = displayName;
        Cpu = snapshot;
    }

    public string DisplayName { get; }
    public MetricSnapshot Cpu { get; }
}
