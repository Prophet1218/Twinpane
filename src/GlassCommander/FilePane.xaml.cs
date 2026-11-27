using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace GlassCommander;

public enum SortColumn { Name, Ext, Size, Date }

public sealed partial class FilePane : UserControl
{
    /// <summary>A panel fókuszt kapott (aktívvá kell tenni).</summary>
    public event EventHandler? Activated;

    /// <summary>Helyi menüből kért parancs (a főablak hajtja végre).</summary>
    public event Action<FilePane, string>? CommandRequested;

    /// <summary>Fájlokat ejtettek a panelre: (forrás útvonalak, célmappa, mozgatás?).</summary>
    public event Action<FilePane, IReadOnlyList<string>, string, bool>? DropRequested;

    /// <summary>Meghajtógomb jobb klikkes menüjéből kért művelet: (meghajtó gyökere, parancs).</summary>
    public event Action<FilePane, string, string>? DriveCommandRequested;

    /// <summary>Változott a kurzor alatti elem (a gyorsnézethez).</summary>
    public event EventHandler? CurrentItemChanged;

    public string CurrentPath { get; private set; } = "";
    public bool ShowHidden { get; set; }

    private ObservableCollection<FileItem> _items = new();
    private sealed class PaneTab
    {
        public string Path = "";
        public string? FocusName;
        public readonly Stack<string> Back = new();
        public TabViewItem Item = null!;
    }

    private PaneTab? _tab;
    private bool _suppressTabChange;
    private Stack<string> Back => EnsureTab().Back;

    private string _search = "";
    private readonly DispatcherQueueTimer _searchTimer;
    private const string InternalDragFormat = "GlassCommander.Paths";
    private readonly Dictionary<string, string> _lastPathPerDrive = new(StringComparer.OrdinalIgnoreCase);
    private SortColumn _sort = SortColumn.Name;
    private bool _ascending = true;
    private bool _suppressDriveChange;
    private bool _isActive;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _driveTimer;
    private string _driveSignature = "";
    private readonly MenuFlyout _menu = new();
    private readonly MenuFlyout _bgMenu = new();
    private readonly List<MenuFlyoutItemBase> _archiveItems = new();

    public FilePane()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(400);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(1600);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => ClearSearch();

        FileList.CharacterReceived += (_, e) =>
        {
            if (OnSearchChar(e.Character)) e.Handled = true;
        };

        // Új meghajtó (pl. pendrive) felismerése
        _driveTimer = DispatcherQueue.CreateTimer();
        _driveTimer.Interval = TimeSpan.FromSeconds(3);
        _driveTimer.IsRepeating = true;
        _driveTimer.Tick += (_, _) =>
        {
            try
            {
                if (DriveSignature() != _driveSignature) LoadDrives();
            }
            catch { }
        };
        _driveTimer.Start();

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
            Back.Push(CurrentPath);

        CurrentPath = path;
        PathBox.Text = path;
        ClearSearch();
        var tab = EnsureTab();
        tab.Path = path;
        tab.Item.Header = TabTitle(path);
        ToolTipService.SetToolTip(tab.Item, path);
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
        while (Back.Count > 0)
        {
            var p = Back.Pop();
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
        IconService.Request(_items, DispatcherQueue);
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

    private static string DriveSignature() => string.Join("|", DriveInfo.GetDrives().Select(d => d.Name));

    private void LoadDrives()
    {
        _suppressDriveChange = true;
        DriveBox.Items.Clear();
        DriveBar.Children.Clear();
        var drives = DriveInfo.GetDrives();
        _driveSignature = string.Join("|", drives.Select(d => d.Name));

        foreach (var d in drives)
        {
            string label = "", tip = d.Name;
            try
            {
                if (d.IsReady)
                {
                    label = d.VolumeLabel;
                    tip = $"{d.Name}  {label}\nSzabad: {FileItem.FormatSize(d.AvailableFreeSpace)} / {FileItem.FormatSize(d.TotalSize)}";
                }
            }
            catch { }

            var item = new ComboBoxItem { Content = d.Name.TrimEnd('\\'), Tag = d.Name };
            ToolTipService.SetToolTip(item, string.IsNullOrEmpty(label) ? d.Name : $"{d.Name}  {label}");
            DriveBox.Items.Add(item);

            // Gomb a meghajtósávba: ikon + betű
            var img = new Image { Width = 16, Height = 16 };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(img);
            content.Children.Add(new TextBlock
            {
                Text = d.Name.Substring(0, 1).ToLowerInvariant(),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var btn = new ToggleButton
            {
                Content = content,
                Tag = d.Name,
                Padding = new Thickness(8, 3, 10, 3),
                MinWidth = 0,
                AllowFocusOnInteraction = false,
            };
            ToolTipService.SetToolTip(btn, tip);
            btn.Click += DriveButton_Click;
            btn.RightTapped += DriveButton_RightTapped;
            DriveBar.Children.Add(btn);
            IconService.RequestPath(d.Name, DispatcherQueue, src => img.Source = src);
        }
        _suppressDriveChange = false;
        SelectDriveInBox();
    }

    private void UpdateDriveButtons()
    {
        var root = CurrentPath.Length > 0 ? Path.GetPathRoot(CurrentPath) : null;
        foreach (var b in DriveBar.Children.OfType<ToggleButton>())
            b.IsChecked = string.Equals(b.Tag as string, root, StringComparison.OrdinalIgnoreCase);
    }

    private void DriveButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string root } btn) return;
        e.Handled = true;
        Activated?.Invoke(this, EventArgs.Empty);

        DriveInfo? d = null;
        try { d = new DriveInfo(root); } catch { }

        var menu = new MenuFlyout();
        void Add(string text, string glyph, string cmd, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            mi.Click += (_, _) => DriveCommandRequested?.Invoke(this, root, cmd);
            menu.Items.Add(mi);
        }

        string title = root.TrimEnd('\\');
        try { if (d != null && d.IsReady && d.VolumeLabel.Length > 0) title = $"{d.VolumeLabel} ({title})"; } catch { }
        menu.Items.Add(new MenuFlyoutItem { Text = title, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Megnyitás", "", "open");
        Add("Megnyitás új fülön", "", "opentab");
        if (d != null && (DriveTools.CanEject(d) || DriveTools.CanFormat(d)))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            if (DriveTools.CanEject(d)) Add("Kiadás", "", "eject");
            if (DriveTools.CanFormat(d)) Add("Formázás…", "", "format");
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Tulajdonságok", "", "props");
        Add("Windows-tulajdonságok", "", "winprops");
        menu.ShowAt(btn, e.GetPosition(btn));
    }

    /// <summary>Ha a panel az adott meghajtón áll (pl. kiadás után), átlép máshová.</summary>
    public async Task LeaveDriveAsync(string root, string fallback)
    {
        if (CurrentPath.Length > 0 && string.Equals(Path.GetPathRoot(CurrentPath), root, StringComparison.OrdinalIgnoreCase))
            await NavigateAsync(fallback, pushHistory: false);
        LoadDrives();
    }

    public async Task GoToDriveAsync(string root)
    {
        await NavigateAsync(root);
        FocusList();
    }

    public async Task OpenDriveInNewTabAsync(string root) => await NewTabAsync(root);

    private async void DriveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string root }) return;
        UpdateDriveButtons();   // a kattintás átbillentette, visszaállítjuk
        Activated?.Invoke(this, EventArgs.Empty);

        bool same = CurrentPath.Length > 0 &&
                    string.Equals(Path.GetPathRoot(CurrentPath), root, StringComparison.OrdinalIgnoreCase);
        // Az aktuális meghajtó gombja a gyökérbe visz (mint a TC-ben), a többi oda, ahol legutóbb jártál.
        var target = !same && _lastPathPerDrive.TryGetValue(root, out var last) && Directory.Exists(last) ? last : root;
        await NavigateAsync(target);
        FocusList();
    }

    private void SelectDriveInBox()
    {
        var root = CurrentPath.Length > 0 ? Path.GetPathRoot(CurrentPath) : null;
        _suppressDriveChange = true;
        DriveBox.SelectedItem = DriveBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, root, StringComparison.OrdinalIgnoreCase));
        _suppressDriveChange = false;
        UpdateDriveButtons();
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
        _driveTimer.Stop();
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
        string Arrow(SortColumn c) => c == _sort ? (_ascending ? "  ▲" : "  ▼") : "";
        NameHeader.Content = "Név" + Arrow(SortColumn.Name);
        ExtHeader.Content = "Kit." + Arrow(SortColumn.Ext);
        SizeHeader.Content = "Méret" + Arrow(SortColumn.Size);
        DateHeader.Content = "Módosítva" + Arrow(SortColumn.Date);
    }

    // ------------------------------------------------------------------ Állapot

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        CurrentItemChanged?.Invoke(this, EventArgs.Empty);
    }

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
        Activated?.Invoke(this, EventArgs.Empty);
        e.Handled = true;

        // Üres részre kattintott: háttérmenü (új mappa, beillesztés, …)
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItem item || item.IsParent)
        {
            _bgMenu.ShowAt(FileList, e.GetPosition(FileList));
            return;
        }

        if (!FileList.SelectedItems.Contains(item))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItems.Add(item);
            if (FileList.ContainerFromItem(item) is ListViewItem c) c.Focus(FocusState.Pointer);
        }

        // A kicsomagolás csak tömörített fájloknál látszik
        bool archive = GetSelection().Any(ArchiveTools.IsArchive);
        foreach (var mi in _archiveItems) mi.Visibility = archive ? Visibility.Visible : Visibility.Collapsed;

        _menu.ShowAt(FileList, e.GetPosition(FileList));
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
        MenuFlyoutItem Make(string text, string glyph, string cmd, string? keys)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (keys != null) mi.KeyboardAcceleratorTextOverride = keys;
            mi.Click += (_, _) => CommandRequested?.Invoke(this, cmd);
            return mi;
        }
        void Add(string text, string glyph, string cmd, string? keys = null) => _menu.Items.Add(Make(text, glyph, cmd, keys));
        void AddArchive(MenuFlyoutItemBase item)
        {
            _archiveItems.Add(item);
            _menu.Items.Add(item);
        }
        void AddBg(string text, string glyph, string cmd, string? keys = null) => _bgMenu.Items.Add(Make(text, glyph, cmd, keys));

        // Háttérmenü (jobb klikk üres részen)
        AddBg("Új mappa", "", "newfolder", "F7");
        AddBg("Új szöveges dokumentum", "", "newfile");
        _bgMenu.Items.Add(new MenuFlyoutSeparator());
        AddBg("Beillesztés", "", "paste", "Ctrl+V");
        AddBg("Frissítés", "", "refresh", "Ctrl+R");
        _bgMenu.Items.Add(new MenuFlyoutSeparator());
        AddBg("Terminál itt", "", "terminal", "Ctrl+Shift+T");
        AddBg("Megnyitás Intézőben", "", "explorer", "Ctrl+E");
        AddBg("Mappa tulajdonságai", "", "folderprops");

        Add("Megnyitás", "", "open", "Enter");
        Add("Megnyitás új fülön", "", "opentab", "Ctrl+↑");
        Add("Szerkesztés", "", "edit", "F4");
        _menu.Items.Add(new MenuFlyoutSeparator());
        Add("Kivágás", "", "cut", "Ctrl+X");
        Add("Másolás vágólapra", "", "clipcopy", "Ctrl+C");
        Add("Beillesztés", "", "paste", "Ctrl+V");
        Add("Elérési út másolása", "", "copypath", "Ctrl+Shift+C");
        _menu.Items.Add(new MenuFlyoutSeparator());
        Add("Másolás a másik panelre", "", "copy", "F5");
        Add("Mozgatás a másik panelre", "", "move", "F6");
        Add("Átnevezés", "", "rename", "F2");
        Add("Törlés", "", "delete", "Del");
        _menu.Items.Add(new MenuFlyoutSeparator());
        AddArchive(Make("Kicsomagolás mappába", "", "extract-folder", "Alt+F9"));
        AddArchive(Make("Kicsomagolás ide", "", "extract-here", null));
        AddArchive(Make("Kicsomagolás a másik panelre", "", "extract-other", null));
        Add("Tömörítés ZIP-be…", "", "compress", "Alt+F5");
        _menu.Items.Add(new MenuFlyoutSeparator());
        Add("Terminál itt", "", "terminal");
        Add("Tulajdonságok", "", "properties", "Alt+Enter");
    }

    // ------------------------------------------------------------------ Fülek

    private PaneTab EnsureTab()
    {
        if (_tab != null) return _tab;
        _tab = CreateTab(CurrentPath.Length > 0 ? CurrentPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _suppressTabChange = true;
        Tabs.TabItems.Add(_tab.Item);
        Tabs.SelectedItem = _tab.Item;
        _suppressTabChange = false;
        return _tab;
    }

    private static PaneTab CreateTab(string path)
    {
        var tab = new PaneTab { Path = path };
        tab.Item = new TabViewItem
        {
            Header = TabTitle(path),
            IconSource = new FontIconSource { Glyph = "" },
            Tag = tab,
        };
        ToolTipService.SetToolTip(tab.Item, path);
        return tab;
    }

    /// <summary>Indításkor: a mentett fülek visszaállítása.</summary>
    public async Task InitializeAsync(IReadOnlyList<string>? paths, int selectedIndex, string fallback)
    {
        var valid = (paths ?? Array.Empty<string>()).Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).ToList();
        if (valid.Count == 0) valid.Add(fallback);
        selectedIndex = Math.Clamp(selectedIndex, 0, valid.Count - 1);

        _suppressTabChange = true;
        Tabs.TabItems.Clear();
        PaneTab? selected = null;
        for (int i = 0; i < valid.Count; i++)
        {
            var t = CreateTab(valid[i]);
            Tabs.TabItems.Add(t.Item);
            if (i == selectedIndex) selected = t;
        }
        _tab = selected;
        Tabs.SelectedItem = selected!.Item;
        _suppressTabChange = false;

        await NavigateAsync(selected.Path, pushHistory: false);
    }

    public List<string> TabPaths =>
        Tabs.TabItems.OfType<TabViewItem>().Select(i => ((PaneTab)i.Tag).Path).ToList();

    public int SelectedTabIndex => Math.Max(0, Tabs.SelectedIndex);

    public async Task NewTabAsync(string? path = null)
    {
        var current = EnsureTab();
        current.FocusName = FocusedItem()?.Name;

        var tab = CreateTab(path ?? CurrentPath);
        _suppressTabChange = true;
        int idx = Tabs.TabItems.IndexOf(current.Item);
        Tabs.TabItems.Insert(idx + 1, tab.Item);
        Tabs.SelectedItem = tab.Item;
        _suppressTabChange = false;

        _tab = tab;
        await NavigateAsync(tab.Path, pushHistory: false);
        FocusList();
    }

    public async Task OpenFocusedInNewTabAsync()
    {
        var item = FocusedItem();
        if (item is { IsDirectory: true }) await NewTabAsync(item.FullPath);
        else await NewTabAsync();
    }

    public async Task CloseCurrentTabAsync()
    {
        if (_tab != null) await CloseTabAsync(_tab);
    }

    private async Task CloseTabAsync(PaneTab tab)
    {
        if (Tabs.TabItems.Count <= 1) return;
        int idx = Tabs.TabItems.IndexOf(tab.Item);
        bool wasCurrent = tab == _tab;

        _suppressTabChange = true;
        Tabs.TabItems.Remove(tab.Item);
        if (wasCurrent)
        {
            int next = Math.Min(idx, Tabs.TabItems.Count - 1);
            var nextItem = (TabViewItem)Tabs.TabItems[next];
            Tabs.SelectedItem = nextItem;
            _tab = (PaneTab)nextItem.Tag;
        }
        else if (_tab != null)
        {
            Tabs.SelectedItem = _tab.Item;
        }
        _suppressTabChange = false;

        if (wasCurrent && _tab != null)
            await NavigateAsync(_tab.Path, _tab.FocusName, pushHistory: false);
        FocusList();
    }

    public void SwitchTab(int direction)
    {
        int count = Tabs.TabItems.Count;
        if (count <= 1) return;
        int idx = (Math.Max(0, Tabs.SelectedIndex) + direction + count) % count;
        Tabs.SelectedIndex = idx;   // a SelectionChanged navigál
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabChange) return;
        if (Tabs.SelectedItem is not TabViewItem { Tag: PaneTab tab } || tab == _tab) return;

        if (_tab != null) _tab.FocusName = FocusedItem()?.Name;
        _tab = tab;
        Activated?.Invoke(this, EventArgs.Empty);
        await NavigateAsync(tab.Path, tab.FocusName, pushHistory: false);
        FocusList();
    }

    private async void Tabs_AddTabButtonClick(TabView sender, object args)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        await NewTabAsync();
    }

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is PaneTab tab) await CloseTabAsync(tab);
    }

    private static string TabTitle(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    // ------------------------------------------------------------------ Gyorskereső

    public bool SearchActive => _search.Length > 0;

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private bool OnSearchChar(char c)
    {
        if (char.IsControl(c) || c == ' ') return false;
        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)) return false;
        _search += c;
        ApplySearch();
        return true;
    }

    public void SearchBackspace()
    {
        if (_search.Length == 0) return;
        _search = _search[..^1];
        ApplySearch();
    }

    public void ClearSearch()
    {
        _search = "";
        _searchTimer?.Stop();
        if (SearchBox != null) SearchBox.Visibility = Visibility.Collapsed;
    }

    private void ApplySearch()
    {
        _searchTimer.Stop();
        if (_search.Length == 0) { ClearSearch(); return; }
        _searchTimer.Start();

        var match = _items.FirstOrDefault(i => !i.IsParent && i.Name.StartsWith(_search, StringComparison.CurrentCultureIgnoreCase))
                 ?? _items.FirstOrDefault(i => !i.IsParent && i.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase));

        SearchText.Text = _search;
        SearchHint.Text = match == null ? "nincs találat" : "";
        SearchBox.Visibility = Visibility.Visible;

        if (match != null)
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = match;
            FileList.ScrollIntoView(match);
            FocusItem(match);
        }
    }

    // ------------------------------------------------------------------ Drag & drop

    private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<FileItem>().Where(i => !i.IsParent).ToList();
        if (items.Count == 0) { e.Cancel = true; return; }

        e.Data.Properties[InternalDragFormat] = string.Join("\n", items.Select(i => i.FullPath));
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

        // Az Intéző (és más programok) felé valódi fájlokként adjuk át, igény szerint előállítva.
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var list = new List<Windows.Storage.IStorageItem>();
                foreach (var i in items)
                {
                    try
                    {
                        if (i.IsDirectory) list.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(i.FullPath));
                        else list.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(i.FullPath));
                    }
                    catch { /* nem elérhető elem kimarad */ }
                }
                request.SetData(list);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private string DropTarget(DragEventArgs e) =>
        (e.OriginalSource as FrameworkElement)?.DataContext is FileItem { IsDirectory: true } f ? f.FullPath : CurrentPath;

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        bool move = (e.Modifiers & DragDropModifiers.Shift) != 0;
        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"{(move ? "Mozgatás" : "Másolás")} ide: {TabTitle(DropTarget(e))}";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            string target = DropTarget(e);
            bool move = (e.Modifiers & DragDropModifiers.Shift) != 0;

            List<string> paths;
            if (e.DataView.Properties.TryGetValue(InternalDragFormat, out var v) && v is string s)
            {
                paths = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else
            {
                var items = await e.DataView.GetStorageItemsAsync();
                paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }

            paths.RemoveAll(p => PathEquals(p, target));   // mappát önmagába nem
            if (paths.Count == 0) return;
            if (paths.All(p => PathEquals(Path.GetDirectoryName(p) ?? "", target))) return;   // ugyanoda

            e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
            DropRequested?.Invoke(this, paths, target, move);
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(XamlRoot, ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
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
