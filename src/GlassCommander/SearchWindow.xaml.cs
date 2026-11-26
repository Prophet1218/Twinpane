using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace GlassCommander;

/// <summary>Globális kereső (Alt+F7 / Ctrl+F): név, joker, tartalom; egy mappában vagy minden meghajtón.</summary>
public sealed partial class SearchWindow : Window
{
    private const int MaxResults = 20000;

    private readonly Func<string, string, Task> _goTo;
    private readonly ObservableCollection<FileItem> _results = new();
    private readonly ConcurrentQueue<FileItem> _incoming = new();
    private readonly DispatcherQueueTimer _flushTimer;
    private CancellationTokenSource? _cts;
    private long _scanned;
    private int _found;
    private string _currentFolder = "";
    private DateTime _started;

    public SearchWindow(string startFolder, string backdrop, Func<string, string, Task> goTo)
    {
        InitializeComponent();
        _goTo = goTo;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new SizeInt32(1000, 720));
        AppIcon.Apply(AppWindow);
        SystemBackdrop = backdrop == "Acrylic" ? new DesktopAcrylicBackdrop()
                       : new MicaBackdrop { Kind = backdrop == "MicaAlt" ? MicaKind.BaseAlt : MicaKind.Base };

        RootBox.Text = startFolder;
        Results.ItemsSource = _results;

        _flushTimer = DispatcherQueue.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(200);
        _flushTimer.Tick += (_, _) => Flush();

        Root.Loaded += (_, _) => PatternBox.Focus(FocusState.Programmatic);
        Closed += (_, _) => _cts?.Cancel();
    }

    public void SetFolder(string folder)
    {
        if (_cts == null) RootBox.Text = folder;
    }

    // ------------------------------------------------------------------ Indítás / leállítás

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) Stop();
        else StartSearch();
    }

    private void Box_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (_cts == null) StartSearch();
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (_cts != null) Stop();
            else Close();
        }
    }

    private void AllDrives_Click(object sender, RoutedEventArgs e)
    {
        RootBox.IsEnabled = AllDrivesCheck.IsChecked != true;
    }

    private async void StartSearch()
    {
        var pattern = PatternBox.Text.Trim();
        var content = ContentBox.Text;
        if (pattern.Length == 0 && content.Length == 0)
        {
            StatusText.Text = "Írj be egy fájlnevet (vagy egy szöveget, amit a fájl tartalmaz).";
            return;
        }

        List<string> roots;
        if (AllDrivesCheck.IsChecked == true)
        {
            roots = DriveInfo.GetDrives()
                .Where(d => { try { return d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable); } catch { return false; } })
                .Select(d => d.RootDirectory.FullName).ToList();
        }
        else
        {
            var root = Environment.ExpandEnvironmentVariables(RootBox.Text.Trim().Trim('"'));
            if (!Directory.Exists(root))
            {
                StatusText.Text = "A megadott mappa nem létezik.";
                return;
            }
            roots = new List<string> { root };
        }

        var matcher = BuildMatcher(pattern);
        bool subfolders = SubfoldersCheck.IsChecked == true;
        bool includeFolders = FoldersCheck.IsChecked == true && content.Length == 0;
        bool hidden = HiddenCheck.IsChecked == true;

        _results.Clear();
        while (_incoming.TryDequeue(out _)) { }
        _scanned = 0;
        _found = 0;
        _started = DateTime.Now;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetRunning(true);
        _flushTimer.Start();

        try
        {
            await Task.Run(() => Search(roots, matcher, content, subfolders, includeFolders, hidden, token), token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Hiba: " + ex.Message;
        }

        bool cancelled = token.IsCancellationRequested;
        _cts = null;
        _flushTimer.Stop();
        Flush();
        SetRunning(false);

        var secs = (DateTime.Now - _started).TotalSeconds;
        StatusText.Text = $"{(cancelled ? "Leállítva" : "Kész")}: {_found:N0} találat, {_scanned:N0} elem átnézve ({secs:N1} mp)"
                          + (_found >= MaxResults ? $" – az első {MaxResults:N0} találat látszik" : "");
    }

    private void Stop() => _cts?.Cancel();

    private void SetRunning(bool running)
    {
        Busy.IsActive = running;
        StartText.Text = running ? "Leállítás" : "Keresés";
        StartIcon.Glyph = running ? "" : "";
    }

    // ------------------------------------------------------------------ Keresés (háttérszál)

    /// <summary>"szamla" → tartalmazza; "*.pdf;*.docx" → joker minták pontosvesszővel elválasztva.</summary>
    private static Func<string, bool> BuildMatcher(string pattern)
    {
        if (pattern.Length == 0) return _ => true;

        var parts = pattern.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matchers = new List<Func<string, bool>>();
        foreach (var p in parts)
        {
            if (p.Contains('*') || p.Contains('?'))
            {
                var rx = new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                matchers.Add(name => rx.IsMatch(name));
            }
            else
            {
                var needle = p;
                matchers.Add(name => name.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
            }
        }
        return name => matchers.Any(m => m(name));
    }

    private void Search(List<string> roots, Func<string, bool> matcher, string content,
                        bool subfolders, bool includeFolders, bool hidden, CancellationToken token)
    {
        var skip = FileAttributes.ReparsePoint;   // csomópontokon nem megyünk körbe
        if (!hidden) skip |= FileAttributes.Hidden | FileAttributes.System;

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = subfolders,
            AttributesToSkip = skip,
            ReturnSpecialDirectories = false,
        };

        foreach (var root in roots)
        {
            if (token.IsCancellationRequested) return;
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts); }
            catch { continue; }

            foreach (var fsi in entries)
            {
                if (token.IsCancellationRequested) return;
                Interlocked.Increment(ref _scanned);
                if ((_scanned & 1023) == 0) _currentFolder = (fsi as FileInfo)?.DirectoryName ?? fsi.FullName;

                bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                if (isDir && !includeFolders) continue;
                if (!matcher(fsi.Name)) continue;

                long size = 0;
                if (!isDir)
                {
                    size = ((FileInfo)fsi).Length;
                    if (content.Length > 0 && !FileContains(fsi.FullName, size, content, token)) continue;
                }

                if (Interlocked.Increment(ref _found) <= MaxResults)
                    _incoming.Enqueue(new FileItem(fsi.Name, fsi.FullName, isDir, false, size, fsi.LastWriteTime, fsi.Attributes));
                else
                    return;
            }
        }
    }

    private static bool FileContains(string path, long size, string text, CancellationToken token)
    {
        if (size == 0 || size > 64L * 1024 * 1024) return false;
        try
        {
            using var sr = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[64 * 1024];
            string carry = "";
            bool first = true;
            int read;
            while ((read = sr.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (token.IsCancellationRequested) return false;
                var chunk = carry + new string(buffer, 0, read);
                if (first)
                {
                    // Bináris fájlokat (null karakter az elején) kihagyjuk.
                    if (chunk.AsSpan(0, Math.Min(chunk.Length, 8000)).IndexOf('\0') >= 0) return false;
                    first = false;
                }
                if (chunk.Contains(text, StringComparison.CurrentCultureIgnoreCase)) return true;
                carry = chunk.Length > text.Length ? chunk.Substring(chunk.Length - text.Length) : chunk;
            }
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ Eredmények

    private void Flush()
    {
        var batch = new List<FileItem>();
        while (batch.Count < 800 && _incoming.TryDequeue(out var item)) batch.Add(item);
        foreach (var i in batch) _results.Add(i);
        if (batch.Count > 0) IconService.Request(batch, DispatcherQueue);

        if (_cts != null)
        {
            var folder = _currentFolder;
            StatusText.Text = $"{_found:N0} találat  ·  {_scanned:N0} elem átnézve  ·  {folder}";
        }
    }

    private FileItem? Selected => Results.SelectedItem as FileItem;

    private async Task GoToSelectedAsync()
    {
        var item = Selected;
        if (item == null) return;
        await _goTo(item.Folder, item.Name);
    }

    private async void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item)
        {
            Results.SelectedItem = item;
            await GoToSelectedAsync();
        }
    }

    private async void Results_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await GoToSelectedAsync();
        }
    }

    private void Results_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItem item) return;
        Results.SelectedItem = item;

        var menu = new MenuFlyout();
        var go = new MenuFlyoutItem { Text = "Ugrás a fájlhoz", Icon = new FontIcon { Glyph = "" }, KeyboardAcceleratorTextOverride = "Enter" };
        go.Click += async (_, _) => await GoToSelectedAsync();
        var open = new MenuFlyoutItem { Text = "Megnyitás", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => { try { ShellOps.Open(item.FullPath); } catch { } };
        var copy = new MenuFlyoutItem { Text = "Elérési út másolása", Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) => ShellOps.SetClipboardText(item.FullPath);
        menu.Items.Add(go);
        menu.Items.Add(open);
        menu.Items.Add(copy);
        menu.ShowAt(Results, e.GetPosition(Results));
        e.Handled = true;
    }
}
