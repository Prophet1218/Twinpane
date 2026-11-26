using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GlassCommander;

public enum FileKind { Parent, Folder, File, Image, Video, Audio, Archive, Code, Executable, Pdf, Text }

public sealed class FileItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A valódi Windows-ikon (háttérben töltődik be; addig a Fluent glyph látszik).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphVisibility)));
        }
    }

    public Visibility GlyphVisibility => _icon == null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Ikon-gyorsítótár kulcsa: kiterjesztés szerint, vagy saját ikonú fájloknál (exe, lnk…) elérési út szerint.</summary>
    public string? IconKey
    {
        get
        {
            if (IsParent) return null;
            if (IsDirectory)
                return (Attributes & (FileAttributes.System | FileAttributes.ReadOnly)) != 0 ? "path:" + FullPath : "dir";
            var ext = Extension.ToLowerInvariant();
            return ext switch
            {
                "exe" or "lnk" or "ico" or "url" or "cur" or "scr" or "appref-ms" or "msc" => "path:" + FullPath,
                "" => "ext:",
                _ => "ext:." + ext,
            };
        }
    }

    public FileItem(string name, string fullPath, bool isDirectory, bool isParent,
                    long size, DateTime modified, FileAttributes attributes)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsParent = isParent;
        Size = size;
        Modified = modified;
        Attributes = attributes;

        if (isParent || isDirectory)
        {
            BaseName = isParent ? ".." : name;
            Extension = "";
        }
        else
        {
            var ext = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(ext))
            {
                BaseName = name;        // pl. ".gitignore"
                Extension = "";
            }
            else
            {
                BaseName = stem;
                Extension = ext.TrimStart('.');
            }
        }
        Kind = DetectKind();
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsParent { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public FileAttributes Attributes { get; }
    public FileKind Kind { get; }

    public string BaseName { get; }
    public string Extension { get; }

    public bool IsHidden => !IsParent && (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
    public double ItemOpacity => IsHidden ? 0.5 : 1.0;

    public string SizeText => IsParent ? "" : IsDirectory ? "<MAPPA>" : FormatSize(Size);
    public string DateText => IsParent ? "" : Modified.ToString("yyyy.MM.dd  HH:mm", CultureInfo.InvariantCulture);

    public string Glyph => Kind switch
    {
        FileKind.Parent => "",
        FileKind.Folder => "",
        FileKind.Image => "",
        FileKind.Video => "",
        FileKind.Audio => "",
        FileKind.Archive => "",
        FileKind.Code => "",
        FileKind.Executable => "",
        FileKind.Pdf => "",
        FileKind.Text => "",
        _ => "",
    };

    // A brush-okat csak a UI szál éri el (x:Bind getter), így biztonságos a lusta létrehozás.
    public Brush IconBrush => IconBrushes.For(Kind);

    public override string ToString() => Name;

    private FileKind DetectKind()
    {
        if (IsParent) return FileKind.Parent;
        if (IsDirectory) return FileKind.Folder;
        switch (Extension.ToLowerInvariant())
        {
            case "jpg": case "jpeg": case "png": case "gif": case "bmp": case "webp":
            case "heic": case "avif": case "tif": case "tiff": case "svg": case "ico": case "raw":
                return FileKind.Image;
            case "mp4": case "mkv": case "avi": case "mov": case "webm": case "wmv": case "m4v": case "flv":
                return FileKind.Video;
            case "mp3": case "flac": case "wav": case "ogg": case "m4a": case "aac": case "opus": case "wma":
                return FileKind.Audio;
            case "zip": case "rar": case "7z": case "tar": case "gz": case "xz": case "bz2": case "zst": case "cab": case "iso":
                return FileKind.Archive;
            case "cs": case "xaml": case "kt": case "java": case "py": case "js": case "ts": case "json":
            case "xml": case "html": case "css": case "cpp": case "c": case "h": case "rs": case "go":
            case "ps1": case "sh": case "bat": case "cmd": case "gradle": case "kts": case "yml": case "yaml": case "toml":
                return FileKind.Code;
            case "exe": case "msi": case "msix": case "appx": case "apk": case "lnk": case "dll":
                return FileKind.Executable;
            case "pdf":
                return FileKind.Pdf;
            case "txt": case "md": case "log": case "ini": case "cfg": case "csv": case "doc": case "docx": case "rtf":
                return FileKind.Text;
            default:
                return FileKind.File;
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} B" : $"{v:N1} {units[u]}";
    }
}

internal static class IconBrushes
{
    private static readonly Dictionary<FileKind, Brush> Cache = new();

    public static Brush For(FileKind kind)
    {
        if (Cache.TryGetValue(kind, out var b)) return b;
        var c = kind switch
        {
            FileKind.Parent => Rgb(0x9A, 0xA0, 0xA6),
            FileKind.Folder => Rgb(0xFF, 0xC8, 0x3D),
            FileKind.Image => Rgb(0x4C, 0xC2, 0xFF),
            FileKind.Video => Rgb(0xB5, 0x7B, 0xFF),
            FileKind.Audio => Rgb(0xFF, 0x6B, 0x9E),
            FileKind.Archive => Rgb(0xE8, 0xA3, 0x3D),
            FileKind.Code => Rgb(0x5F, 0xD3, 0x8D),
            FileKind.Executable => Rgb(0x7A, 0xA7, 0xFF),
            FileKind.Pdf => Rgb(0xFF, 0x5B, 0x5B),
            FileKind.Text => Rgb(0xC8, 0xCD, 0xD2),
            _ => Rgb(0x9A, 0xA0, 0xA6),
        };
        b = new SolidColorBrush(c);
        Cache[kind] = b;
        return b;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
