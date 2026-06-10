using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using silicon_scope.Services;

namespace silicon_scope.ViewModels;

/// <summary>
/// Root view model. Owns the picker, the primary load monitor, the optional
/// pinned load monitor, and the projector-mode flag. Wires picker selection
/// changes into the load monitors.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ProcessTreeService _treeService = new();

    [ObservableProperty]
    public partial bool IsProjectorMode { get; set; }

    public ProcessPickerViewModel Picker { get; }
    public ProcessLoadViewModel Primary { get; }
    public ProcessLoadViewModel Pinned { get; }

    public NpuDetectionResult Npu { get; }

    public MainViewModel(DispatcherQueue ui)
    {
        Npu = new NpuDetectionService().Detect();
        Picker = new ProcessPickerViewModel();
        Primary = new ProcessLoadViewModel(ui, Npu, _treeService);
        Pinned = new ProcessLoadViewModel(ui, Npu, _treeService);

        Picker.PropertyChanged += OnPickerChanged;

        if (Picker.SelectedProcess is { } first)
        {
            ApplyPrimary(first);
        }
    }

    private void OnPickerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProcessPickerViewModel.SelectedProcess):
                if (Picker.SelectedProcess is { } sel) ApplyPrimary(sel);
                else Primary.Clear();
                break;
            case nameof(ProcessPickerViewModel.PinnedProcess):
                if (Picker.PinnedProcess is { } pin) ApplyPinned(pin);
                else Pinned.Clear();
                break;
        }
    }

    private void ApplyPrimary(ProcessEntry entry)
    {
        Primary.Track(entry.Pid, entry.Name);
        Picker.PrimaryTrackingSubtitle = Primary.TrackedPidsSubtitle;
    }

    private void ApplyPinned(ProcessEntry entry)
    {
        Pinned.Track(entry.Pid, entry.Name);
        Picker.PinnedTrackingSubtitle = Pinned.TrackedPidsSubtitle;
    }

    public void Dispose()
    {
        Primary.Dispose();
        Pinned.Dispose();
    }
}
