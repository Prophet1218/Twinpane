using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GlassCommander;

public enum SortColumn { Name, Ext, Size, Date }

public sealed partial class FilePane : UserControl
{
    /// <summary>A panel fókuszt kapott (aktívvá kell tenni).</summary>
    public event EventHandler? Activated;

    /// <summary>Helyi menüből kért parancs (a főablak hajtja végre).</summary>
    public event Action<FilePane, string>? CommandRequested;

    public string CurrentPath { get; private set; } = "";
    public bool ShowHidden { get; set; }

    private ObservableCollection<FileItem> _items = new();
    private readonly Stack<string> _back = new();
    private readonly Dictionary<string, string> _lastPathPerDrive = new(StringComparer.OrdinalIgnoreCase);
    private SortColumn _sort = SortColumn.Name;
    private bool _ascending = true;
    private bool _suppressDriveChange;
    private bool _isActive;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly MenuFlyout _menu = new();

    public FilePane()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(400);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        BuildMenu();
        LoadDrives();
        UpdateHeaders();
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            ActiveRing.Opacity = value ? 1 : 0;
        }
    }

    // ------------------------------------------------------------------ Navigáció

    public async Task NavigateAsync(string path, string? focusName = null, bool pushHistory = true,
                                    IEnumerable<string>? keepSelection = null)
    {
        try
        {
            path = Normalize(path);
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(XamlRoot, $"Érvénytelen elérési út:\n{path}\n\n{ex.Message}");
            PathBox.Text = CurrentPath;
            return;
        }

        // Ha fájlt írt be, lépjünk a mappájába és jelöljük ki.
        if (File.Exists(path))
        {
            focusName = Path.GetFileName(path);
            path = Path.GetDirectoryName(path) ?? path;
        }

        List<FileItem> items;
        bool showHidden = ShowHidden;
        var sort = _sort;
        bool asc = _ascending;
        try
        {
            items = await Task.Run(() => Load(path, showHidden, sort, asc));
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(XamlRoot, $"Nem nyitható meg:\n{path}\n\n{ex.Message}");
            PathBox.Text = CurrentPath;
            return;
        }

        if (pushHistory && CurrentPath.Length > 0 && !PathEquals(CurrentPath, path))
            _back.Push(CurrentPath);

        CurrentPath = path;
        PathBox.Text = path;
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root)) _lastPathPerDrive[root] = path;

        ApplyItems(items, keepSelection, focusName);
        SelectDriveInBox();
        UpdateFreeSpace();
        SetupWatcher();
    }

    public async Task RefreshAsync(string? focusName = null)
    {
        if (CurrentPath.Length == 0) return;

        // Ha a mappát közben törölték, lépjünk fel az első létezőre.
        var path = CurrentPath;
        while (!Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(parent)) { LoadDrives(); return; }
            path = parent;
        }

        var keep = FileList.SelectedItems.OfType<FileItem>().Where(i => !i.IsParent).Select(i => i.Name).ToList();
        focusName ??= FocusedItem()?.Name;
        await NavigateAsync(path, focusName, pushHistory: false, keepSelection: keep);
    }

    public async Task GoUpAsync()
    {
        var trimmed = CurrentPath.TrimEnd('\\');
        var parent = Directory.GetParent(CurrentPath);
        if (parent == null) return;
        await NavigateAsync(parent.FullName, focusName: Path.GetFileName(trimmed));
    }

    public async Task GoBackAsync()
    {
        while (_back.Count > 0)
        {
            var p = _back.Pop();
            if (Directory.Exists(p))
            {
                await NavigateAsync(p, pushHistory: false);
                return;
            }
        }
    }

    public async Task OpenAsync(FileItem item)
    {
        if (item.IsParent) { await GoUpAsync(); return; }
        if (item.IsDirectory) { await NavigateAsync(item.FullPath); return; }
        try { ShellOps.Open(item.FullPath); }
        catch (Exception ex) { await Dialogs.ErrorAsync(XamlRoot, ex.Message); }
    }

    public async Task OpenFocusedAsync()
    {
        var item = FocusedItem();
        if (item != null) await OpenAsync(item);
    }

    public void ReloadDrives() => LoadDrives();

    public void OpenDriveList()
    {
        LoadDrives();
        DriveBox.IsDropDownOpen = true;
    }

    public void FocusPathBox()
    {
        PathBox.Focus(FocusState.Keyboard);
        PathBox.SelectAll();
    }

    private static List<FileItem> Load(string path, bool showHidden, SortColumn sort, bool asc)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) throw new DirectoryNotFoundException("A mappa nem létezik.");

        var list = new List<FileItem>();
        if (di.Parent != null)
            list.Add(new FileItem("..", di.Parent.FullName, true, true, 0, DateTime.MinValue, FileAttributes.Directory));

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = showHidden ? 0 : (FileAttributes.Hidden | FileAttributes.System),
        };

        foreach (var fsi in di.EnumerateFileSystemInfos("*", opts))
        {
            bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
            long size = isDir ? 0 : ((FileInfo)fsi).Length;
            list.Add(new FileItem(fsi.Name, fsi.FullName, isDir, false, size, fsi.LastWriteTime, fsi.Attributes));
        }

        SortList(list, sort, asc);
        return list;
    }

    private static void SortList(List<FileItem> list, SortColumn sort, bool asc)
    {
        list.Sort((a, b) =>
        {
            if (a.IsParent != b.IsParent) return a.IsParent ? -1 : 1;
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

            int c = sort switch
            {
                SortColumn.Ext => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
                SortColumn.Size => a.Size.CompareTo(b.Size),
                SortColumn.Date => a.Modified.CompareTo(b.Modified),
                _ => 0,
            };
            if (c == 0) c = ShellOps.NaturalCompare(a.Name, b.Name);
            return asc ? c : -c;
        });
    }

    private void ApplyItems(List<FileItem> list, IEnumerable<string>? keepSelection, string? focusName)
    {
        _items = new ObservableCollection<FileItem>(list);
        FileList.ItemsSource = _items;

        if (keepSelection != null)
        {
            var set = new HashSet<string>(keepSelection, StringComparer.OrdinalIgnoreCase);
            if (set.Count > 1)   // egyetlen elemnél a fókusz úgyis kijelöli
                foreach (var i in _items)
                    if (!i.IsParent && set.Contains(i.Name)) FileList.SelectedItems.Add(i);
        }

        FileItem? focus = null;
        if (focusName != null)
            focus = _items.FirstOrDefault(i => string.Equals(i.Name, focusName, StringComparison.OrdinalIgnoreCase));
        focus ??= _items.FirstOrDefault();

        if (focus != null)
        {
            if (FileList.SelectedItems.Count == 0) FileList.SelectedItem = focus;
            FileList.ScrollIntoView(focus);
            if (IsActive) FocusItem(focus);
        }
        UpdateStatus();
    }

    // ------------------------------------------------------------------ Kijelölés / fókusz

    public List<FileItem> GetSelection()
    {
        var sel = FileList.SelectedItems.OfType<FileItem>().Where(i => !i.IsParent).ToList();
        if (sel.Count == 0 && FocusedItem() is { IsParent: false } f) sel.Add(f);
        return sel;
    }

    public FileItem? FocusedItem()
    {
        int idx = -1;
        if (XamlRoot != null && FocusManager.GetFocusedElement(XamlRoot) is ListViewItem lvi)
            idx = FileList.IndexFromContainer(lvi);
        if (idx < 0) idx = FileList.SelectedIndex;
        return idx >= 0 && idx < _items.Count ? _items[idx] : null;
    }

    public void FocusList()
    {
        var item = FocusedItem() ?? FileList.SelectedItems.OfType<FileItem>().FirstOrDefault() ?? _items.FirstOrDefault();
        if (item == null) { FileList.Focus(FocusState.Keyboard); return; }
        if (FileList.SelectedItems.Count == 0) FileList.SelectedItem = item;
        FocusItem(item);
    }

    private void FocusItem(FileItem item)
    {
        FileList.UpdateLayout();
        if (FileList.ContainerFromItem(item) is ListViewItem c) c.Focus(FocusState.Keyboard);
        else FileList.Focus(FocusState.Keyboard);
    }

    /// <summary>Total Commander "Insert": kijelölés váltása és ugrás a következőre.</summary>
    public void ToggleFocusedAndAdvance()
    {
        var item = FocusedItem();
        if (item == null) return;
        int idx = _items.IndexOf(item);
        if (!item.IsParent)
        {
            if (FileList.SelectedItems.Contains(item)) FileList.SelectedItems.Remove(item);
            else FileList.SelectedItems.Add(item);
        }
        int next = Math.Min(idx + 1, _items.Count - 1);
        FileList.ScrollIntoView(_items[next]);
        FileList.UpdateLayout();
        if (FileList.ContainerFromIndex(next) is ListViewItem c) c.Focus(FocusState.Keyboard);
    }

    public void SelectAll()
    {
        FileList.SelectedItems.Clear();
        foreach (var i in _items) if (!i.IsParent) FileList.SelectedItems.Add(i);
    }

    // ------------------------------------------------------------------ Meghajtók

    private void LoadDrives()
    {
        _suppressDriveChange = true;
        DriveBox.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            string label = "";
            try { if (d.IsReady) label = d.VolumeLabel; } catch { }
            var item = new ComboBoxItem { Content = d.Name.TrimEnd('\\'), Tag = d.Name };
            ToolTipService.SetToolTip(item, string.IsNullOrEmpty(label) ? d.Name : $"{d.Name}  {label}");
            DriveBox.Items.Add(item);
        }
        _suppressDriveChange = false;
        SelectDriveInBox();
    }

    private void SelectDriveInBox()
    {
        var root = CurrentPath.Length > 0 ? Path.GetPathRoot(CurrentPath) : null;
        _suppressDriveChange = true;
        DriveBox.SelectedItem = DriveBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, root, StringComparison.OrdinalIgnoreCase));
        _suppressDriveChange = false;
    }

    private async void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDriveChange || DriveBox.SelectedItem is not ComboBoxItem { Tag: string root }) return;
        if (CurrentPath.Length > 0 && string.Equals(Path.GetPathRoot(CurrentPath), root, StringComparison.OrdinalIgnoreCase))
            return;

        var target = _lastPathPerDrive.TryGetValue(root, out var last) && Directory.Exists(last) ? last : root;
        await NavigateAsync(target);
        FocusList();
    }

    private void UpdateFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(CurrentPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) { FreeText.Text = ""; return; }
            var d = new DriveInfo(root);
            FreeText.Text = $"Szabad: {FileItem.FormatSize(d.AvailableFreeSpace)} / {FileItem.FormatSize(d.TotalSize)}";
        }
        catch { FreeText.Text = ""; }
    }

    // ------------------------------------------------------------------ Figyelés

    private void SetupWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var w = new FileSystemWatcher(CurrentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            w.Created += OnFsChanged;
            w.Deleted += OnFsChanged;
            w.Renamed += OnFsChanged;
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { /* pl. hálózati / speciális mappa */ }
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    public void Shutdown()
    {
        _refreshTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }

    // ------------------------------------------------------------------ Rendezés

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var col = Enum.Parse<SortColumn>(tag);
        if (col == _sort) _ascending = !_ascending;
        else { _sort = col; _ascending = col != SortColumn.Date && col != SortColumn.Size; }

        var keep = FileList.SelectedItems.OfType<FileItem>().Select(i => i.Name).ToList();
        var focus = FocusedItem()?.Name;
        var list = _items.ToList();
        SortList(list, _sort, _ascending);
        ApplyItems(list, keep, focus);
        UpdateHeaders();
    }

    private void UpdateHeaders()
    {
        string Arrow(SortColumn c) => c == _sort ? (_ascending ? "  \u25B2" : "  \u25BC") : "";
        NameHeader.Content = "Név" + Arrow(SortColumn.Name);
        ExtHeader.Content = "Kit." + Arrow(SortColumn.Ext);
        SizeHeader.Content = "Méret" + Arrow(SortColumn.Size);
        DateHeader.Content = "Módosítva" + Arrow(SortColumn.Date);
    }

    // ------------------------------------------------------------------ Állapot

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        var sel = FileList.SelectedItems.OfType<FileItem>().Where(i => !i.IsParent).ToList();
        int dirs = _items.Count(i => i.IsDirectory && !i.IsParent);
        int files = _items.Count(i => !i.IsDirectory);
        if (sel.Count > 1)
        {
            long size = sel.Where(i => !i.IsDirectory).Sum(i => i.Size);
            StatusText.Text = $"{sel.Count} kijelölve  ·  {FileItem.FormatSize(size)}";
        }
        else
        {
            StatusText.Text = $"{dirs} mappa, {files} fájl";
        }
    }

    // ------------------------------------------------------------------ Események

    private void Pane_GotFocus(object sender, RoutedEventArgs e) => Activated?.Invoke(this, EventArgs.Empty);

    private async void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item)
            await OpenAsync(item);
    }

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item && !FileList.SelectedItems.Contains(item))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItems.Add(item);
            if (FileList.ContainerFromItem(item) is ListViewItem c) c.Focus(FocusState.Pointer);
        }
        Activated?.Invoke(this, EventArgs.Empty);
        _menu.ShowAt(FileList, e.GetPosition(FileList));
        e.Handled = true;
    }

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var text = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));
            await NavigateAsync(text);
            FocusList();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            PathBox.Text = CurrentPath;
            FocusList();
        }
    }

    private async void Back_Click(object sender, RoutedEventArgs e) { await GoBackAsync(); FocusList(); }
    private async void Up_Click(object sender, RoutedEventArgs e) { await GoUpAsync(); FocusList(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { LoadDrives(); await RefreshAsync(); FocusList(); }

    private void BuildMenu()
    {
        void Add(string text, string glyph, string cmd, string? keys = null)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (keys != null) mi.KeyboardAcceleratorTextOverride = keys;
            mi.Click += (_, _) => CommandRequested?.Invoke(this, cmd);
            _menu.Items.Add(mi);
        }

        Add("Megnyitás", "\uE8E5", "open", "Enter");
        Add("Szerkesztés", "\uE70F", "edit", "F4");
        _menu.Items.Add(new MenuFlyoutSeparator());
        Add("Kivágás", "\uE8C6", "cut", "Ctrl+X");
        Add("Másolás vágólapra", "\uE8C8", "clipcopy", "Ctrl+C");
        Add("Beillesztés", "\uE77F", "paste", "Ctrl+V");
        Add("Elérési út másolása", "\uE71B", "copypath", "Ctrl+Shift+C");
        _menu.Items.Add(new MenuFlyoutSeparator());
        Add("Másolás a másik panelre", "\uE8C8", "copy", "F5");
        Add("Mozgatás a másik panelre", "\uE8DE", "move", "F6");
        Add("Átnevezés", "\uE8AC", "rename", "F2");
        Add("Törlés", "\uE74D", "delete", "Del");
        _menu.Items.Add(new MenuFlyoutSeparator());
        Add("Terminál itt", "\uE756", "terminal");
        Add("Tulajdonságok", "\uE946", "properties", "Alt+Enter");
    }

    // ------------------------------------------------------------------ Segédek

    private static string Normalize(string path)
    {
        path = path.Trim();
        if (path.Length == 2 && path[1] == ':') path += "\\";
        path = Path.GetFullPath(path);
        if (path.Length > 3 && path.EndsWith('\\')) path = path.TrimEnd('\\');
        return path;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
