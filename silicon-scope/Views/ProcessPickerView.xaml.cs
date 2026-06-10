using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using silicon_scope.ViewModels;

namespace silicon_scope.Views;

public sealed partial class ProcessPickerView : UserControl
{
    public ProcessPickerView() => InitializeComponent();

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ProcessPickerViewModel), typeof(ProcessPickerView),
        new PropertyMetadata(null));

    public ProcessPickerViewModel ViewModel
    {
        get => (ProcessPickerViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
