using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace silicon_scope.ViewModels;

/// <summary>
/// Backs the process picker at the top of the window. Holds the master list
/// of running processes, the user's primary + pinned selections, and a
/// human-readable "tracking PIDs" subtitle derived from the expanded tree.
/// </summary>
public partial class ProcessPickerViewModel : ObservableObject
{
    public ObservableCollection<ProcessEntry> AllProcesses { get; } = new();

    [ObservableProperty]
    public partial ProcessEntry? SelectedProcess { get; set; }

    [ObservableProperty]
    public partial ProcessEntry? PinnedProcess { get; set; }

    [ObservableProperty]
    public partial string PrimaryTrackingSubtitle { get; set; } = "no process selected";

    [ObservableProperty]
    public partial string PinnedTrackingSubtitle { get; set; } = "no pinned process";

    public ProcessPickerViewModel()
    {
        RefreshProcessList();
    }

    [RelayCommand]
    private void RefreshProcessList()
    {
        AllProcesses.Clear();
        try
        {
            foreach (var p in Process.GetProcesses()
                .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    // Skip the Idle (0) and System (4) pseudo-processes —
                    // they cannot be opened by user code and would generate
                    // noisy first-chance access-denied exceptions on every
                    // sample tick.
                    if (p.Id <= 4 || string.IsNullOrEmpty(p.ProcessName)) continue;
                    AllProcesses.Add(new ProcessEntry(p.Id, p.ProcessName));
                }
                catch { /* zombie process */ }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessPickerViewModel] Enumeration failed: {ex.Message}");
        }

        // Default to AudioWorker if present.
        SelectedProcess = AllProcesses.FirstOrDefault(e =>
            string.Equals(e.Name, "AudioWorker", StringComparison.OrdinalIgnoreCase))
            ?? AllProcesses.FirstOrDefault();
    }

    [RelayCommand]
    private void ClearPin() => PinnedProcess = null;
}

public sealed record ProcessEntry(int Pid, string Name)
{
    public string Display => $"{Name} ({Pid})";
}
