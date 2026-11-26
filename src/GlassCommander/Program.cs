using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace GlassCommander;

/// <summary>
/// Saját belépési pont: ha a Twinpane már fut, nem nyit új ablakot,
/// hanem előre hozza a meglévőt (mint a Total Commander).
/// </summary>
public static class Program
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToRunningInstance())
            return 0;

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        try
        {
            var mainInstance = AppInstance.FindOrRegisterForKey("Twinpane-main");
            if (mainInstance.IsCurrent)
            {
                mainInstance.Activated += (_, _) => App.BringToFront();
                return false;
            }

            // Már fut egy példány: átadjuk neki az indítást, és kilépünk.
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            AllowSetForegroundWindow(mainInstance.ProcessId);
            Task.Run(() => mainInstance.RedirectActivationToAsync(activation).AsTask()).Wait();
            return true;
        }
        catch
        {
            return false;   // ha bármi gond van, inkább induljon el normálisan
        }
    }
}
