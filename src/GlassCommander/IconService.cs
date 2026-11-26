using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlassCommander;

/// <summary>
/// A valódi Windows (shell) ikonokat tölti be egy háttérszálon, gyorsítótárazva.
/// Kiterjesztés szerinti ikonoknál nem nyúl a lemezhez, így nagy mappáknál is gyors.
/// </summary>
internal static class IconService
{
    private sealed record IconRequest(string Key, string Path);
    private sealed record IconResult(string Key, int Width, int Height, byte[]? Pixels);

    // Csak a UI szálról érjük el:
    private static readonly Dictionary<string, ImageSource?> Cache = new();
    private static readonly Dictionary<string, List<FileItem>> Waiting = new();

    private static readonly BlockingCollection<IconRequest> Queue = new();
    private static DispatcherQueue? _ui;
    private static Thread? _worker;

    public static void Request(IEnumerable<FileItem> items, DispatcherQueue ui)
    {
        _ui ??= ui;
        EnsureWorker();

        foreach (var item in items)
        {
            var key = item.IconKey;
            if (key == null) continue;

            if (Cache.TryGetValue(key, out var src))
            {
                if (src != null) item.Icon = src;
                continue;
            }
            if (Waiting.TryGetValue(key, out var list))
            {
                list.Add(item);
                continue;
            }
            Waiting[key] = new List<FileItem> { item };
            Queue.Add(new IconRequest(key, item.FullPath));
        }
    }

    private static void EnsureWorker()
    {
        if (_worker != null) return;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "IconLoader" };
        _worker.SetApartmentState(ApartmentState.STA);   // a shell COM-ot igényel
        _worker.Start();
    }

    private static void WorkerLoop()
    {
        foreach (var req in Queue.GetConsumingEnumerable())
        {
            IconResult res;
            try { res = Extract(req); }
            catch { res = new IconResult(req.Key, 0, 0, null); }
            _ui?.TryEnqueue(() => Deliver(res));
        }
    }

    private static void Deliver(IconResult res)
    {
        ImageSource? src = null;
        if (res.Pixels != null)
        {
            try
            {
                var wb = new WriteableBitmap(res.Width, res.Height);
                using (var s = wb.PixelBuffer.AsStream())
                    s.Write(res.Pixels, 0, res.Pixels.Length);
                wb.Invalidate();
                src = wb;
            }
            catch { src = null; }
        }

        Cache[res.Key] = src;
        if (Waiting.Remove(res.Key, out var items) && src != null)
            foreach (var i in items) i.Icon = src;
    }

    // ------------------------------------------------------------------ Win32

    private static IconResult Extract(IconRequest req)
    {
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;
        uint attr = 0;
        string path;

        if (req.Key == "dir")
        {
            path = "folder";
            attr = FILE_ATTRIBUTE_DIRECTORY;
            flags |= SHGFI_USEFILEATTRIBUTES;
        }
        else if (req.Key.StartsWith("ext:"))
        {
            path = "file" + req.Key.Substring(4);
            attr = FILE_ATTRIBUTE_NORMAL;
            flags |= SHGFI_USEFILEATTRIBUTES;
        }
        else
        {
            path = req.Path;
        }

        var info = new SHFILEINFO();
        SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (info.hIcon == IntPtr.Zero) return new IconResult(req.Key, 0, 0, null);

        try
        {
            return IconToPixels(req.Key, info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static IconResult IconToPixels(string key, IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var ii)) return new IconResult(key, 0, 0, null);
        try
        {
            if (ii.hbmColor == IntPtr.Zero) return new IconResult(key, 0, 0, null);
            if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return new IconResult(key, 0, 0, null);

            int w = bmp.bmWidth, h = bmp.bmHeight;
            var px = ReadBits(ii.hbmColor, w, h);
            if (px == null) return new IconResult(key, 0, 0, null);

            bool hasAlpha = false;
            for (int i = 3; i < px.Length; i += 4)
                if (px[i] != 0) { hasAlpha = true; break; }

            if (!hasAlpha)
            {
                // Régi ikon alfa nélkül: az átlátszóságot a maszk adja (fehér = átlátszó).
                var mask = ii.hbmMask != IntPtr.Zero ? ReadBits(ii.hbmMask, w, h) : null;
                for (int i = 0; i < px.Length; i += 4)
                    px[i + 3] = mask != null && mask[i] != 0 ? (byte)0 : (byte)255;
            }

            // A WriteableBitmap előre szorzott (premultiplied) BGRA-t vár.
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                if (a == 255) continue;
                px[i] = (byte)(px[i] * a / 255);
                px[i + 1] = (byte)(px[i + 1] * a / 255);
                px[i + 2] = (byte)(px[i + 2] * a / 255);
            }
            return new IconResult(key, w, h, px);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    private static byte[]? ReadBits(IntPtr hbm, int w, int h)
    {
        var bi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,      // felülről lefelé
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,  // BI_RGB
        };
        var px = new byte[w * h * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            int lines = GetDIBits(hdc, hbm, 0, (uint)h, px, ref bi, 0);
            return lines == 0 ? null : px;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
