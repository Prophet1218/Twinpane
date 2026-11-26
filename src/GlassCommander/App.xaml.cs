using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GlassCommander;

public partial class App : Application
{
    private static Window? _window;
    private static DispatcherQueue? _ui;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>Egy újabb indítás ide lett átirányítva: a főablak jöjjön előre.</summary>
    public static void BringToFront()
    {
        _ui?.TryEnqueue(() =>
        {
            if (_window == null) return;
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // SW_RESTORE
                _window.Activate();
                SetForegroundWindow(hwnd);
            }
            catch { }
        });
    }
}
