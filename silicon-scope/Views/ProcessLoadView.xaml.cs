using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using silicon_scope.Controls;
using silicon_scope.ViewModels;

namespace silicon_scope.Views;

public sealed partial class ProcessLoadView : UserControl
{
    public ProcessLoadView() => InitializeComponent();

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(ProcessLoadView),
        new PropertyMetadata(null, OnViewModelChanged));

    public MainViewModel ViewModel
    {
        get => (MainViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public MetricColumnView? PrimaryCpu { get; private set; }
    public MetricColumnView? PrimaryGpu { get; private set; }
    public MetricColumnView? PrimaryNpu { get; private set; }
    public MetricColumnView? PinnedCpu { get; private set; }
    public MetricColumnView? PinnedGpu { get; private set; }
    public MetricColumnView? PinnedNpu { get; private set; }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ProcessLoadView)d;
        if (e.NewValue is MainViewModel vm)
        {
            view.PrimaryCpu = new MetricColumnView("primary", vm.Primary.Cpu);
            view.PrimaryGpu = new MetricColumnView("primary", vm.Primary.Gpu);
            view.PrimaryNpu = new MetricColumnView("primary", vm.Primary.Npu);
            view.PinnedCpu  = new MetricColumnView("pinned",  vm.Pinned.Cpu);
            view.PinnedGpu  = new MetricColumnView("pinned",  vm.Pinned.Gpu);
            view.PinnedNpu  = new MetricColumnView("pinned",  vm.Pinned.Npu);
            view.Bindings.Update();
        }
    }
}
