using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace GlassCommander;

/// <summary>
/// Fájlműveletek a Windows saját shelljén keresztül: natív folyamatjelző,
/// ütközés-kezelés ("Csere / Kihagyás") és Lomtár, pont mint az Intézőben.
/// </summary>
internal static class ShellOps
{
    private const uint FO_MOVE = 0x1, FO_COPY = 0x2, FO_DELETE = 0x3, FO_RENAME = 0x4;
    private const ushort FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMATION = 0x10,
                         FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Intéző-szerű "természetes" rendezés (file2 &lt; file10).</summary>
    public static int NaturalCompare(string a, string b) => StrCmpLogicalW(a, b);

    // A shell párbeszédablakai saját STA szálon futnak, így a felület nem fagy le.
    private static Task<bool> RunSta(Func<bool> work)
    {
        var tcs = new TaskCompletionSource<bool>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    private static bool Run(IntPtr hwnd, uint func, IEnumerable<string> from, string? to, ushort flags)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = hwnd,
            wFunc = func,
            pFrom = string.Join("\0", from) + "\0",   // dupla null-lezárás (a marshaller ad még egyet)
            pTo = to == null ? null : to + "\0",
            fFlags = flags,
        };
        int result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    public static Task<bool> CopyMoveAsync(IntPtr hwnd, IReadOnlyList<string> sources, string targetDir, bool move)
    {
        ushort flags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR);
        bool sameFolder = sources.Any(s =>
            string.Equals(Path.GetDirectoryName(s.TrimEnd('\\'))?.TrimEnd('\\'), targetDir.TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase));
        if (sameFolder && !move) flags |= FOF_RENAMEONCOLLISION;   // "név - Másolat"
        return RunSta(() => Run(hwnd, move ? FO_MOVE : FO_COPY, sources, targetDir, flags));
    }

    public static Task<bool> DeleteAsync(IntPtr hwnd, IReadOnlyList<string> paths, bool recycle)
    {
        ushort flags = FOF_NOCONFIRMATION;  // saját megerősítő ablakot mutatunk előtte
        if (recycle) flags |= FOF_ALLOWUNDO;
        return RunSta(() => Run(hwnd, FO_DELETE, paths, null, flags));
    }

    public static Task<bool> RenameAsync(IntPtr hwnd, string from, string to)
        => RunSta(() => Run(hwnd, FO_RENAME, new[] { from }, to, FOF_ALLOWUNDO));

    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        });
    }

    public static void Edit(string path)
    {
        var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(path);
        Process.Start(psi);
    }

    public static void ShowProperties(IntPtr hwnd, string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = 0x0000000C,   // SEE_MASK_INVOKEIDLIST
            hwnd = hwnd,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,            // SW_SHOW
        };
        ShellExecuteEx(ref info);
    }

    public static void OpenTerminal(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(dir);
            Process.Start(psi);
        }
        catch
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = dir, UseShellExecute = true });
        }
    }

    public static void OpenInExplorer(string dir)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(dir);
        Process.Start(psi);
    }

    // ---- Vágólap (kompatibilis az Intézővel) ----

    public static async Task SetClipboardFilesAsync(IEnumerable<FileItem> items, bool cut)
    {
        var storage = new List<IStorageItem>();
        foreach (var i in items)
        {
            if (i.IsDirectory) storage.Add(await StorageFolder.GetFolderFromPathAsync(i.FullPath));
            else storage.Add(await StorageFile.GetFileFromPathAsync(i.FullPath));
        }
        var dp = new DataPackage
        {
            RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy
        };
        dp.SetStorageItems(storage);
        Clipboard.SetContent(dp);
        Clipboard.Flush();
    }

    public static void SetClipboardText(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
        Clipboard.Flush();
    }

    public static async Task<(List<string> Paths, bool Move)?> GetClipboardFilesAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems)) return null;
        var items = await content.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        bool move = content.RequestedOperation.HasFlag(DataPackageOperation.Move);
        return (paths, move);
    }
}
