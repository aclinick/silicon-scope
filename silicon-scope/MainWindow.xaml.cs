using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using silicon_scope.ViewModels;
using Windows.Graphics;

namespace silicon_scope;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Size for projector demos: wide enough that the three columns each get
        // ~400 DIPs even with a pinned column splitting them.
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1380 * scale), (int)(820 * scale)));

        Closed += (_, _) => ViewModel.Dispose();
    }

    public static Visibility PickerVisibility(bool projectorMode) =>
        projectorMode ? Visibility.Collapsed : Visibility.Visible;
}
