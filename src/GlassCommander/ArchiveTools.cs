using System.Diagnostics;
using System.IO.Compression;

namespace GlassCommander;

/// <summary>
/// Kicsomagolás és tömörítés. ZIP: a .NET saját motorja (folyamatjelzéssel).
/// 7z, RAR, TAR, GZ, XZ, CAB, ISO…: a Windows beépített tar.exe-je (libarchive).
/// </summary>
internal static class ArchiveTools
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "tbz", "tbz2", "xz", "txz", "zst", "cab", "iso", "lzma", "jar",
    };

    public static bool IsArchive(FileItem item) =>
        !item.IsDirectory && !item.IsParent && Extensions.Contains(item.Extension);

    /// <summary>"anyag.tar.gz" → "anyag"</summary>
    public static string FolderNameFor(string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? "Kicsomagolt" : name;
    }

    /// <summary>Szabad mappanév: "név", "név (2)", "név (3)"…</summary>
    public static string UniquePath(string path, string extension = "")
    {
        string candidate = path + extension;
        for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
            candidate = $"{path} ({n}){extension}";
        return candidate;
    }

    private static bool IsZip(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hány fájlt írna felül a kicsomagolás (csak ZIP-nél tudjuk előre).</summary>
    public static int CountConflicts(string archive, string dest)
    {
        if (!IsZip(archive)) return 0;
        try
        {
            using var za = ZipFile.OpenRead(archive);
            return za.Entries.Count(e => e.Name.Length > 0 && File.Exists(Path.Combine(dest, e.FullName)));
        }
        catch { return 0; }
    }

    public static Task ExtractAsync(string archive, string dest, IProgress<double> progress)
    {
        Directory.CreateDirectory(dest);
        return IsZip(archive)
            ? Task.Run(() => ExtractZip(archive, dest, progress))
            : ExtractWithTarAsync(archive, dest);
    }

    private static void ExtractZip(string archive, string dest, IProgress<double> progress)
    {
        var destFull = Path.GetFullPath(dest);
        if (!destFull.EndsWith(Path.DirectorySeparatorChar)) destFull += Path.DirectorySeparatorChar;

        using var za = ZipFile.OpenRead(archive);
        int total = Math.Max(1, za.Entries.Count), i = 0;
        foreach (var entry in za.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destFull, entry.FullName));
            // Védelem a "../" trükkök ellen (zip slip)
            if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue;

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            progress.Report(++i * 100.0 / total);
        }
    }

    private static async Task ExtractWithTarAsync(string archive, string dest)
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar))
            throw new InvalidOperationException("Ehhez a formátumhoz a Windows tar.exe-je kell, de nem található.");

        var psi = new ProcessStartInfo(tar)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-xf");
        psi.ArgumentList.Add(archive);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dest);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("A tar.exe nem indult el.");
        var errTask = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var err = (await errTask).Trim();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"A Windows nem tudta kicsomagolni ezt a fájlt ({Path.GetExtension(archive)}).\n\n{err}");
    }

    /// <summary>A kijelölt fájlok és mappák tömörítése egy új ZIP-be.</summary>
    public static Task CompressAsync(IReadOnlyList<string> paths, string zipPath, IProgress<double> progress) =>
        Task.Run(() =>
        {
            // Az összes fájl összegyűjtése a relatív nevükkel
            var files = new List<(string Full, string Entry)>();
            var emptyDirs = new List<string>();
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    var baseDir = Path.GetDirectoryName(p.TrimEnd('\\')) ?? p;
                    bool any = false;
                    foreach (var f in Directory.EnumerateFiles(p, "*", opts))
                    {
                        files.Add((f, Path.GetRelativePath(baseDir, f).Replace('\\', '/')));
                        any = true;
                    }
                    if (!any) emptyDirs.Add(Path.GetFileName(p.TrimEnd('\\')) + "/");
                }
                else if (File.Exists(p))
                {
                    files.Add((p, Path.GetFileName(p)));
                }
            }

            using var fs = new FileStream(zipPath, FileMode.CreateNew);
            using var za = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var d in emptyDirs) za.CreateEntry(d);
            int total = Math.Max(1, files.Count), i = 0;
            foreach (var (full, entry) in files)
            {
                try { za.CreateEntryFromFile(full, entry, CompressionLevel.Optimal); }
                catch (IOException) { /* zárolt fájl kimarad */ }
                catch (UnauthorizedAccessException) { }
                progress.Report(++i * 100.0 / total);
            }
        });
}
