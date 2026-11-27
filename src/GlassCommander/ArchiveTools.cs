using System.Diagnostics;
using System.IO.Compression;

namespace GlassCommander;

/// <summary>Kicsomagolás / tömörítés állapota a folyamatjelzőhöz.</summary>
internal readonly record struct ArchiveProgress(double Percent, long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles);

/// <summary>
/// Kicsomagolás és tömörítés. ZIP: a .NET saját motorja.
/// 7z, RAR, TAR, GZ, XZ, CAB, ISO…: a Windows beépített tar.exe-je (libarchive).
/// Mindkét esetben bájt alapú, valódi százalékos folyamatjelzéssel.
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

    /// <summary>Ritkítja a jelentéseket (kb. 10/mp), hogy a felület ne akadjon.</summary>
    private sealed class Reporter
    {
        private readonly IProgress<ArchiveProgress> _progress;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _last = -1000;

        public Reporter(IProgress<ArchiveProgress> progress) => _progress = progress;

        public void Report(long doneBytes, long totalBytes, int doneFiles, int totalFiles, bool force = false)
        {
            long now = _sw.ElapsedMilliseconds;
            if (!force && now - _last < 100) return;
            _last = now;
            double pct = totalBytes > 0 ? doneBytes * 100.0 / totalBytes
                       : totalFiles > 0 ? doneFiles * 100.0 / totalFiles : 0;
            _progress.Report(new ArchiveProgress(Math.Clamp(pct, 0, 100), doneBytes, totalBytes, doneFiles, totalFiles));
        }
    }

    public static Task ExtractAsync(string archive, string dest, IProgress<ArchiveProgress> progress)
    {
        Directory.CreateDirectory(dest);
        return IsZip(archive)
            ? Task.Run(() => ExtractZip(archive, dest, new Reporter(progress)))
            : ExtractWithTarAsync(archive, dest, new Reporter(progress));
    }

    private static void ExtractZip(string archive, string dest, Reporter rep)
    {
        var destFull = Path.GetFullPath(dest);
        if (!destFull.EndsWith(Path.DirectorySeparatorChar)) destFull += Path.DirectorySeparatorChar;

        using var za = ZipFile.OpenRead(archive);
        var entries = za.Entries;
        long total = entries.Sum(e => e.Length);
        int files = entries.Count(e => e.Name.Length > 0);
        long done = 0;
        int doneFiles = 0;
        var buffer = new byte[1024 * 1024];
        rep.Report(0, total, 0, files, force: true);

        foreach (var entry in entries)
        {
            var target = Path.GetFullPath(Path.Combine(destFull, entry.FullName));
            // Védelem a "../" trükkök ellen (zip slip)
            if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue;

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    done += read;
                    rep.Report(done, total, doneFiles, files);
                }
            }
            try { File.SetLastWriteTime(target, entry.LastWriteTime.DateTime); } catch { }
            doneFiles++;
            rep.Report(done, total, doneFiles, files);
        }
        rep.Report(total, total, files, files, force: true);
    }

    private static string TarPath()
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar))
            throw new InvalidOperationException("Ehhez a formátumhoz a Windows tar.exe-je kell, de nem található.");
        return tar;
    }

    private static ProcessStartInfo TarStart(params string[] args)
    {
        var psi = new ProcessStartInfo(TarPath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>A tartalomjegyzékből (tar -tv) az elemek mérete, archívumbeli sorrendben.</summary>
    private static async Task<List<long>> ListSizesAsync(string archive)
    {
        var sizes = new List<long>();
        using var p = Process.Start(TarStart("-tvf", archive));
        if (p == null) return sizes;
        _ = p.StandardError.ReadToEndAsync();
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            // pl.: "-rw-r--r--  0 user group  12345 Jan 01  2020 név"
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long size = 0;
            if (parts.Length > 4) long.TryParse(parts[4], out size);
            sizes.Add(Math.Max(0, size));
        }
        await p.WaitForExitAsync();
        return sizes;
    }

    private static async Task ExtractWithTarAsync(string archive, string dest, Reporter rep)
    {
        List<long> sizes;
        try { sizes = await ListSizesAsync(archive); }
        catch { sizes = new List<long>(); }
        long total = sizes.Sum();
        int count = sizes.Count;
        rep.Report(0, total, 0, count, force: true);

        using var p = Process.Start(TarStart("-xvf", archive, "-C", dest))
                      ?? throw new InvalidOperationException("A tar.exe nem indult el.");
        _ = p.StandardOutput.ReadToEndAsync();

        // A -v kapcsolóval minden kibontott elemhez kiír egy "x név" sort a hibakimenetre.
        var errors = new System.Text.StringBuilder();
        int index = 0;
        long done = 0;
        string? line;
        while ((line = await p.StandardError.ReadLineAsync()) != null)
        {
            if (line.StartsWith("x "))
            {
                if (index < sizes.Count) done += sizes[index];
                index++;
                rep.Report(done, total, Math.Min(index, count), count);
            }
            else if (line.Length > 0)
            {
                errors.AppendLine(line);
            }
        }
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"A Windows nem tudta kicsomagolni ezt a fájlt ({Path.GetExtension(archive)}).\n\n{errors.ToString().Trim()}");
        rep.Report(total, total, count, count, force: true);
    }

    /// <summary>A kijelölt fájlok és mappák tömörítése egy új ZIP-be.</summary>
    public static Task CompressAsync(IReadOnlyList<string> paths, string zipPath, IProgress<ArchiveProgress> progress) =>
        Task.Run(() =>
        {
            var rep = new Reporter(progress);

            // Az összes fájl összegyűjtése a relatív nevükkel és méretükkel
            var files = new List<(string Full, string Entry, long Size)>();
            var emptyDirs = new List<string>();
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    var baseDir = Path.GetDirectoryName(p.TrimEnd('\\')) ?? p;
                    bool any = false;
                    foreach (var f in new DirectoryInfo(p).EnumerateFiles("*", opts))
                    {
                        files.Add((f.FullName, Path.GetRelativePath(baseDir, f.FullName).Replace('\\', '/'), f.Length));
                        any = true;
                    }
                    if (!any) emptyDirs.Add(Path.GetFileName(p.TrimEnd('\\')) + "/");
                }
                else if (File.Exists(p))
                {
                    files.Add((p, Path.GetFileName(p), new FileInfo(p).Length));
                }
            }

            long total = files.Sum(f => f.Size), done = 0;
            int doneFiles = 0;
            var buffer = new byte[1024 * 1024];
            rep.Report(0, total, 0, files.Count, force: true);

            using var fs = new FileStream(zipPath, FileMode.CreateNew);
            using var za = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var d in emptyDirs) za.CreateEntry(d);
            foreach (var (full, entryName, size) in files)
            {
                try
                {
                    using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                    var entry = za.CreateEntry(entryName, CompressionLevel.Optimal);
                    try { entry.LastWriteTime = File.GetLastWriteTime(full); } catch { }
                    using var output = entry.Open();
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        done += read;
                        rep.Report(done, total, doneFiles, files.Count);
                    }
                }
                catch (IOException) { done += size; }                 // zárolt fájl kimarad
                catch (UnauthorizedAccessException) { done += size; }
                doneFiles++;
                rep.Report(done, total, doneFiles, files.Count);
            }
            rep.Report(total, total, files.Count, files.Count, force: true);
        });
}
