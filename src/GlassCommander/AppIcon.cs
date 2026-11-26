using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace GlassCommander;

/// <summary>Az ablak (és így a tálca) ikonjának beállítása.</summary>
internal static class AppIcon
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    private static IntPtr _exeIcon;

    public static void Apply(AppWindow window)
    {
        // 1) Az exe-be ágyazott ikon közvetlenül az erőforrásból (nem a Windows ikon-gyorsítótárából).
        try
        {
            if (_exeIcon == IntPtr.Zero)
            {
                var exe = Environment.ProcessPath;
                if (exe != null)
                {
                    var large = new IntPtr[1];
                    if (ExtractIconEx(exe, 0, large, null, 1) > 0) _exeIcon = large[0];
                }
            }
            if (_exeIcon != IntPtr.Zero)
            {
                window.SetIcon(Win32Interop.GetIconIdFromIcon(_exeIcon));
                return;
            }
        }
        catch { }

        // 2) Tartalék: a program mellé másolt app.ico.
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(ico)) window.SetIcon(ico);
        }
        catch { }
    }
}
