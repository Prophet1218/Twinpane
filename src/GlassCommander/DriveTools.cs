using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace GlassCommander;

/// <summary>Meghajtóműveletek: kiadás (ISO leválasztás, pendrive), formázás, tulajdonságok grafikonnal.</summary>
internal static class DriveTools
{
    [DllImport("shell32.dll")]
    private static extern uint SHFormatDrive(IntPtr hwnd, uint drive, uint fmtID, uint options);

    private const uint SHFMT_ID_DEFAULT = 0xFFFF;

    public static string TypeName(DriveType t) => t switch
    {
        DriveType.Fixed => "Helyi lemez",
        DriveType.Removable => "Cserélhető lemez",
        DriveType.CDRom => "CD/DVD-meghajtó vagy csatolt ISO",
        DriveType.Network => "Hálózati meghajtó",
        DriveType.Ram => "RAM-lemez",
        _ => "Meghajtó",
    };

    public static bool CanEject(DriveInfo d) => d.DriveType is DriveType.CDRom or DriveType.Removable;

    public static bool CanFormat(DriveInfo d)
    {
        if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) return false;
        var sys = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return !string.Equals(sys, d.Name, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ Formázás

    /// <summary>A Windows saját formázó ablaka (ott kell még egyszer megerősíteni).</summary>
    public static void Format(IntPtr hwnd, string root)
    {
        uint index = (uint)(char.ToUpperInvariant(root[0]) - 'A');
        SHFormatDrive(hwnd, index, SHFMT_ID_DEFAULT, 0);
    }

    // ------------------------------------------------------------------ Kiadás

    /// <summary>Kiadás az Intéző "Kiadás" parancsával; csatolt ISO-nál tartalékként Dismount-DiskImage.</summary>
    public static async Task<bool> EjectAsync(string root)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t != null)
            {
                object shell = Activator.CreateInstance(t)!;
                object? folder = t.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { 17 }); // Ez a gép
                object? item = folder?.GetType().InvokeMember("ParseName", BindingFlags.InvokeMethod, null, folder, new object[] { root });
                item?.GetType().InvokeMember("InvokeVerb", BindingFlags.InvokeMethod, null, item, new object[] { "Eject" });
            }
        }
        catch { /* jön a tartalék */ }

        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(250);
            if (!DriveExists(root)) return true;
        }

        // Csatolt lemezkép leválasztása PowerShell-lel (rendszergazda nélkül is működik).
        try
        {
            var letter = char.ToUpperInvariant(root[0]);
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"Get-Volume -DriveLetter {letter} | Get-DiskImage | Dismount-DiskImage");
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }
        catch { }

        for (int i = 0; i < 12; i++)
        {
            if (!DriveExists(root)) return true;
            await Task.Delay(250);
        }
        return false;
    }

    private static bool DriveExists(string root) =>
        DriveInfo.GetDrives().Any(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ Tulajdonságok

    public static async Task ShowPropertiesAsync(XamlRoot xamlRoot, string root, IntPtr hwnd)
    {
        var d = new DriveInfo(root);
        if (!d.IsReady)
        {
            await Dialogs.ErrorAsync(xamlRoot, $"A(z) {root} meghajtó nem elérhető (nincs benne lemez?).");
            return;
        }

        long total = d.TotalSize, free = d.TotalFreeSpace, used = Math.Max(0, total - free);
        double pct = total > 0 ? used * 100.0 / total : 0;
        var accent = AccentBrush();
        var track = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

        // --- Bal oldal: fánkdiagram + adatok
        var ring = new ProgressRing
        {
            IsIndeterminate = false,
            Minimum = 0,
            Maximum = 100,
            Value = pct,
            Width = 170,
            Height = 170,
            Foreground = accent,
            Background = track,
        };
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(new TextBlock { Text = $"{pct:N0}%", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        center.Children.Add(new TextBlock { Text = "foglalt", FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        var donut = new Grid { Width = 170, Height = 170, HorizontalAlignment = HorizontalAlignment.Center };
        donut.Children.Add(ring);
        donut.Children.Add(center);

        var left = new StackPanel { Spacing = 14, Width = 240 };
        left.Children.Add(donut);
        left.Children.Add(LegendRow(accent, "Foglalt", used));
        left.Children.Add(LegendRow(track, "Szabad", free));
        left.Children.Add(new Rectangle { Height = 1, Fill = track, Margin = new Thickness(0, 4, 0, 4) });
        left.Children.Add(InfoRow("Kapacitás", $"{FileItem.FormatSize(total)}  ({total:N0} bájt)"));
        left.Children.Add(InfoRow("Típus", TypeName(d.DriveType)));
        string fs = "";
        try { fs = d.DriveFormat; } catch { }
        if (fs.Length > 0) left.Children.Add(InfoRow("Fájlrendszer", fs));

        // --- Jobb oldal: mi foglalja a helyet
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, Text = "Számolás indul…" };
        var list = new StackPanel { Spacing = 10 };
        var right = new StackPanel { Spacing = 10, Width = 440 };
        right.Children.Add(new TextBlock { Text = "Mi foglalja a helyet?", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        right.Children.Add(status);
        right.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, Padding = new Thickness(0, 0, 14, 0) });

        var layout = new Grid { ColumnSpacing = 32 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(left);
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);

        string label = "";
        try { label = d.VolumeLabel; } catch { }
        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = string.IsNullOrEmpty(label) ? $"{TypeName(d.DriveType)} ({root.TrimEnd('\\')})" : $"{label} ({root.TrimEnd('\\')})",
            Content = layout,
            PrimaryButtonText = "Windows-tulajdonságok",
            CloseButtonText = "Bezárás",
            DefaultButton = ContentDialogButton.Close,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 1000.0;
        try
        {
            if (Application.Current.Resources["DefaultContentDialogStyle"] is Style st) dlg.Style = st;
        }
        catch { }

        using var cts = new CancellationTokenSource();
        _ = ComputeUsageAsync(root, used, list, status, accent, cts.Token);

        var result = await dlg.ShowAsync();
        cts.Cancel();
        if (result == ContentDialogResult.Primary) ShellOps.ShowProperties(hwnd, root);
    }

    private static async Task ComputeUsageAsync(string root, long used, StackPanel list, TextBlock status,
                                                Brush accent, CancellationToken ct)
    {
        var results = new List<(string Name, long Size)>();
        try
        {
            var dirOpts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var dirs = await Task.Run(() => new DirectoryInfo(root).EnumerateDirectories("*", dirOpts).ToList(), ct);
            long rootFiles = await Task.Run(() =>
                new DirectoryInfo(root).EnumerateFiles("*", dirOpts).Sum(f => f.Length), ct);

            int n = 0;
            foreach (var dir in dirs)
            {
                if (ct.IsCancellationRequested) return;
                n++;
                status.Text = $"Számolás ({n}/{dirs.Count}): {dir.Name}…";
                long size = await Task.Run(() => DirSize(dir.FullName, ct), ct);
                results.Add((dir.Name, size));
                Render(list, results, accent);
            }

            if (rootFiles > 0) results.Add(("Fájlok a meghajtó gyökerében", rootFiles));
            long counted = results.Sum(r => r.Size);
            if (used - counted > 64L * 1024 * 1024)
                results.Add(("Egyéb (rendszerfájlok, nem elérhető mappák)", used - counted));
            Render(list, results, accent);
            status.Text = $"Kész – {results.Count} tétel. A mappák méretét a bennük lévő fájlok alapján számoltam.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            status.Text = "Nem sikerült kiszámolni: " + ex.Message;
        }
    }

    private static long DirSize(string path, CancellationToken ct)
    {
        long sum = 0;
        int i = 0;
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                sum += f.Length;
                if ((++i & 2047) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return sum;
    }

    private static void Render(StackPanel list, List<(string Name, long Size)> results, Brush accent)
    {
        var top = results.OrderByDescending(r => r.Size).Take(14).ToList();
        long max = top.Count > 0 ? Math.Max(1, top[0].Size) : 1;
        list.Children.Clear();
        foreach (var (name, size) in top)
        {
            var row = new Grid { RowSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            var sz = new TextBlock { Text = FileItem.FormatSize(size), Opacity = 0.75, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(sz, 1);
            row.Children.Add(sz);

            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = size * 100.0 / max, Foreground = accent, MinHeight = 6 };
            Grid.SetRow(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            row.Children.Add(bar);
            list.Children.Add(row);
        }
    }

    private static UIElement LegendRow(Brush brush, string label, long bytes)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        sp.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, Width = 70 });
        sp.Children.Add(new TextBlock { Text = FileItem.FormatSize(bytes), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        return sp;
    }

    private static UIElement InfoRow(string label, string value)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.65 });
        sp.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    private static Brush AccentBrush()
    {
        try
        {
            if (Application.Current.Resources["AccentFillColorDefaultBrush"] is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(Color.FromArgb(255, 0x3D, 0x7B, 0xF7));
    }
}
