using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlassCommander;

/// <summary>Gyorsnézet (Ctrl+Q): képek, szöveges fájlok és mappa-összesítő a másik panel helyén.</summary>
public sealed partial class PreviewPanel : UserControl
{
    private int _version;

    public PreviewPanel()
    {
        InitializeComponent();
    }

    public async Task ShowAsync(FileItem? item)
    {
        int v = ++_version;

        ImageView.Source = null;
        ImageView.Visibility = Visibility.Collapsed;
        TextView.Text = "";
        TextScroll.Visibility = Visibility.Collapsed;
        InfoView.Visibility = Visibility.Collapsed;

        if (item == null || item.IsParent)
        {
            TitleText.Text = "Gyorsnézet";
            FooterText.Text = "";
            ShowInfo("", null, "Válassz ki egy fájlt vagy mappát az előnézethez.");
            return;
        }

        TitleText.Text = item.Name;
        FooterText.Text = item.IsDirectory
            ? $"Mappa  ·  {item.DateText}"
            : $"{FileItem.FormatSize(item.Size)}  ·  {item.DateText}";

        try
        {
            if (item.IsDirectory)
            {
                var summary = await Task.Run(() => DescribeFolder(item.FullPath));
                if (v != _version) return;
                ShowInfo(item.Glyph, item.IconBrush, summary);
                return;
            }

            if (item.Kind == FileKind.Image && !item.Extension.Equals("svg", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await Task.Run(() => item.Size < 150L * 1024 * 1024 ? File.ReadAllBytes(item.FullPath) : null);
                if (v != _version) return;
                if (bytes != null)
                {
                    var bmp = new BitmapImage();
                    if (item.Size > 6L * 1024 * 1024) bmp.DecodePixelWidth = 2400;   // nagy fotóknál memóriakímélés
                    using var mem = new MemoryStream(bytes);
                    await bmp.SetSourceAsync(mem.AsRandomAccessStream());
                    if (v != _version) return;
                    ImageView.Source = bmp;
                    ImageView.Visibility = Visibility.Visible;
                    if (bmp.PixelWidth > 0)
                        FooterText.Text += $"  ·  {bmp.PixelWidth} × {bmp.PixelHeight}";
                    return;
                }
            }

            if (LooksTextual(item))
            {
                var text = await Task.Run(() => ReadText(item.FullPath));
                if (v != _version) return;
                if (text != null)
                {
                    TextView.Text = text;
                    TextScroll.Visibility = Visibility.Visible;
                    TextScroll.ChangeView(0, 0, null, true);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            if (v != _version) return;
            ShowInfo("", null, "Nem sikerült megnyitni:\n" + ex.Message);
            return;
        }

        if (v != _version) return;
        ShowInfo(item.Glyph, item.IconBrush, "Ehhez a fájltípushoz nincs előnézet.\nEnter: megnyitás az alapértelmezett programmal");
    }

    private void ShowInfo(string glyph, Brush? brush, string text)
    {
        InfoGlyph.Glyph = glyph;
        if (brush != null) InfoGlyph.Foreground = brush;
        else InfoGlyph.ClearValue(IconElement.ForegroundProperty);
        InfoText.Text = text;
        InfoView.Visibility = Visibility.Visible;
    }

    private static bool LooksTextual(FileItem item)
    {
        var ext = item.Extension.ToLowerInvariant();
        if (ext is "doc" or "docx" or "rtf") return false;
        if (item.Kind is FileKind.Text or FileKind.Code) return true;
        // Ismeretlen, kis fájl: megpróbáljuk szövegként (a ReadText kiszűri a binárist).
        return item.Kind == FileKind.File && item.Size < 2 * 1024 * 1024;
    }

    private static string? ReadText(string path)
    {
        const int Max = 120_000;
        using var sr = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buf = new char[Max];
        int n = sr.ReadBlock(buf, 0, Max);
        var s = new string(buf, 0, n);

        // Bináris fájl felismerése: null karakter vagy sok vezérlőkarakter.
        int control = 0;
        foreach (var c in s.AsSpan(0, Math.Min(s.Length, 4000)))
        {
            if (c == '\0') return null;
            if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') control++;
        }
        if (control > 40) return null;

        if (n == Max) s += "\n\n… (a fájl további része nem jelenik meg)";
        return s;
    }

    private static string DescribeFolder(string path)
    {
        int dirs = 0, files = 0;
        long size = 0;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opts))
        {
            if ((e.Attributes & FileAttributes.Directory) != 0) dirs++;
            else { files++; size += ((FileInfo)e).Length; }
        }
        return $"{dirs} mappa, {files} fájl\n{FileItem.FormatSize(size)} közvetlenül a mappában";
    }
}
