using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace GlassCommander;

public sealed partial class MainWindow : Window, IDialogHost
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly IntPtr _hwnd;
    private bool _leftActive = true;
    private bool _previewOn;
    private SearchWindow? _search;

    private FilePane Active => _leftActive ? LeftPane : RightPane;
    private FilePane Other => _leftActive ? RightPane : LeftPane;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new SizeInt32(Math.Max(900, _settings.Width), Math.Max(600, _settings.Height)));

        ApplyBackdrop(_settings.Backdrop);
        AppIcon.Apply(AppWindow);

        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";

        HiddenToggle.IsChecked = _settings.ShowHidden;
        LeftPane.ShowHidden = RightPane.ShowHidden = _settings.ShowHidden;

        LeftPane.Activated += (_, _) => SetActive(true, focus: false);
        RightPane.Activated += (_, _) => SetActive(false, focus: false);
        LeftPane.CommandRequested += OnPaneCommand;
        RightPane.CommandRequested += OnPaneCommand;
        LeftPane.DropRequested += OnPaneDrop;
        RightPane.DropRequested += OnPaneDrop;
        LeftPane.CurrentItemChanged += OnPaneCurrentItemChanged;
        LeftPane.DriveCommandRequested += OnDriveCommand;
        RightPane.DriveCommandRequested += OnDriveCommand;
        RightPane.CurrentItemChanged += OnPaneCurrentItemChanged;

        Dialogs.Host = this;
        Root.Loaded += Root_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");

        var rightDefault = Directory.Exists(downloads) ? downloads : home;

        await LeftPane.InitializeAsync(
            _settings.LeftTabs ?? new List<string> { ValidDir(_settings.LeftPath) ?? home },
            _settings.LeftTabIndex, home);
        await RightPane.InitializeAsync(
            _settings.RightTabs ?? new List<string> { ValidDir(_settings.RightPath) ?? rightDefault },
            _settings.RightTabIndex, rightDefault);
        SetActive(true, focus: true);
    }

    private static string? ValidDir(string? p) => !string.IsNullOrEmpty(p) && Directory.Exists(p) ? p : null;

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _settings.LeftPath = LeftPane.CurrentPath;
        _settings.RightPath = RightPane.CurrentPath;
        _settings.LeftTabs = LeftPane.TabPaths;
        _settings.RightTabs = RightPane.TabPaths;
        _settings.LeftTabIndex = LeftPane.SelectedTabIndex;
        _settings.RightTabIndex = RightPane.SelectedTabIndex;
        _settings.Width = AppWindow.Size.Width;
        _settings.Height = AppWindow.Size.Height;
        _settings.Save();
        LeftPane.Shutdown();
        RightPane.Shutdown();
        try { _search?.Close(); } catch { }
    }

    private void SetActive(bool left, bool focus)
    {
        bool changed = _leftActive != left;
        _leftActive = left;
        LeftPane.IsActive = left;
        RightPane.IsActive = !left;
        if (changed && _previewOn) UpdatePreview(reposition: true);
        if (focus) Active.FocusList();
    }

    // ------------------------------------------------------------------ Meghajtók

    private async void OnDriveCommand(FilePane pane, string root, string cmd)
    {
        SetActive(pane == LeftPane, focus: false);
        try
        {
            switch (cmd)
            {
                case "open": await pane.GoToDriveAsync(root); break;
                case "opentab": await pane.OpenDriveInNewTabAsync(root); break;
                case "props": await ShowDrivePropertiesAsync(root); break;
                case "winprops": ShellOps.ShowProperties(_hwnd, root); break;
                case "format":
                    DriveTools.Format(_hwnd, root);
                    await RefreshBothAsync();
                    break;
                case "eject":
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    // Előbb kilépünk a meghajtóról, különben a Windows "használatban van" hibát adhat.
                    await LeftPane.LeaveDriveAsync(root, home);
                    await RightPane.LeaveDriveAsync(root, home);
                    if (!await DriveTools.EjectAsync(root))
                        await Dialogs.ErrorAsync(Root.XamlRoot,
                            $"A(z) {root.TrimEnd('\\')} meghajtót nem sikerült kiadni. Lehet, hogy egy program még használja.");
                    LeftPane.ReloadDrives();
                    RightPane.ReloadDrives();
                    break;
            }
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message); }
        Active.FocusList();
    }

    private async Task ShowDrivePropertiesAsync(string root)
    {
        var view = DriveTools.CreateProperties(root);
        if (view == null)
        {
            await Dialogs.ErrorAsync(Root.XamlRoot, $"A(z) {root.TrimEnd('\\')} meghajtó nem elérhető (nincs benne lemez?).");
            return;
        }
        using var cts = new CancellationTokenSource();
        var closed = ShowOverlayAsync(view.Title, view.Content, "Windows-tulajdonságok", "Bezárás", primaryIsDefault: false);
        _ = view.StartAsync(cts.Token);
        bool primary = await closed;
        cts.Cancel();
        if (primary) ShellOps.ShowProperties(_hwnd, root);
    }

    // ------------------------------------------------------------------ Saját párbeszéd-réteg

    private TaskCompletionSource<bool>? _overlayTcs;

    private bool _overlayCancelable;

    private bool OverlayOpen => Overlay.Visibility == Visibility.Visible;

    private static Style? FindStyle(string key)
    {
        try { return Application.Current.Resources[key] as Style; }
        catch { return null; }
    }

    /// <summary>
    /// Villanásmentes párbeszéd a főablakon belül. primaryText null: csak egy gomb;
    /// closeText is null: gomb nélküli "dolgozom" ablak, amit a kód zár be.
    /// </summary>
    private Task<bool> ShowOverlayAsync(string title, UIElement content, string? primaryText, string? closeText,
                                        bool primaryIsDefault)
    {
        _overlayTcs?.TrySetResult(false);
        OverlayTitle.Text = title;
        OverlayContent.Content = content;

        bool hasPrimary = primaryText != null;
        OverlayPrimary.Visibility = hasPrimary ? Visibility.Visible : Visibility.Collapsed;
        OverlayPrimaryColumn.Width = hasPrimary ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        OverlayPrimary.Content = primaryText ?? "";
        OverlayClose.Content = closeText ?? "";
        OverlayButtons.Visibility = closeText != null ? Visibility.Visible : Visibility.Collapsed;
        _overlayCancelable = closeText != null;

        // Az alapértelmezett (Enterrel aktiválható) gomb legyen kiemelt
        var accent = FindStyle("AccentButtonStyle");
        var normal = FindStyle("DefaultButtonStyle");
        if (accent != null && normal != null)
        {
            OverlayPrimary.Style = hasPrimary && primaryIsDefault ? accent : normal;
            OverlayClose.Style = hasPrimary && primaryIsDefault ? normal : accent;
        }

        Overlay.Visibility = Visibility.Visible;
        if (hasPrimary && primaryIsDefault) OverlayPrimary.Focus(FocusState.Programmatic);
        else if (closeText != null) OverlayClose.Focus(FocusState.Programmatic);

        _overlayTcs = new TaskCompletionSource<bool>();
        return _overlayTcs.Task;
    }

    Task<bool> IDialogHost.ShowAsync(string title, UIElement content, string? primaryText, string? closeText, bool primaryIsDefault)
        => ShowOverlayAsync(title, content, primaryText, closeText, primaryIsDefault);

    void IDialogHost.Close(bool primary) => CloseOverlay(primary);

    /// <summary>Folyamatjelzős "dolgozom" ablak (pl. kicsomagolás).</summary>
    private (ProgressBar Bar, TextBlock Text) ShowBusy(string title, string text)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true, Width = 420 };
        var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Width = 420 };
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(label);
        panel.Children.Add(bar);
        _ = ShowOverlayAsync(title, panel, null, null, false);
        return (bar, label);
    }

    private void CloseOverlay(bool primary)
    {
        Overlay.Visibility = Visibility.Collapsed;
        OverlayContent.Content = null;
        var tcs = _overlayTcs;
        _overlayTcs = null;
        tcs?.TrySetResult(primary);
        Active.FocusList();
    }

    private void OverlayPrimary_Click(object sender, RoutedEventArgs e) => CloseOverlay(true);
    private void OverlayClose_Click(object sender, RoutedEventArgs e) => CloseOverlay(false);

    // ------------------------------------------------------------------ Új fájl, kicsomagolás, tömörítés

    private async Task NewTextFileAsync()
    {
        var name = await Dialogs.PromptAsync(Root.XamlRoot, "Új szöveges dokumentum", $"Új fájl itt: {Active.CurrentPath}",
                                             "Új szöveges dokumentum.txt", "Létrehozás", selectStemOnly: true);
        if (name == null) { Active.FocusList(); return; }
        var path = Path.Combine(Active.CurrentPath, name);
        if (File.Exists(path) || Directory.Exists(path))
        {
            await Dialogs.ErrorAsync(Root.XamlRoot, $"Már létezik ilyen nevű elem: {name}");
            return;
        }
        try { await File.WriteAllTextAsync(path, ""); }
        catch (Exception ex) { await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message); return; }
        await Active.RefreshAsync(name);
        Active.FocusList();
    }

    /// <summary>mode: "folder" = saját mappába, "here" = ide, "other" = a másik panelre (saját mappába).</summary>
    private async Task ExtractAsync(string mode)
    {
        var archives = Active.GetSelection().Where(ArchiveTools.IsArchive).ToList();
        if (archives.Count == 0)
        {
            await Dialogs.ErrorAsync(Root.XamlRoot, "Válassz ki egy tömörített fájlt (zip, 7z, rar, tar, gz…).");
            return;
        }

        var baseDir = mode == "other" ? Other.CurrentPath : Active.CurrentPath;
        string? lastFolder = null;

        foreach (var a in archives)
        {
            string dest = mode == "here"
                ? baseDir
                : ArchiveTools.UniquePath(Path.Combine(baseDir, ArchiveTools.FolderNameFor(a.FullPath)));

            if (mode == "here")
            {
                int conflicts = ArchiveTools.CountConflicts(a.FullPath, dest);
                if (conflicts > 0 && !await Dialogs.ConfirmAsync(Root.XamlRoot, "Felülírás",
                        $"{conflicts} fájl már létezik ezen a helyen, és felülíródik. Folytatod?", "Felülírás"))
                    continue;
            }

            var (bar, label) = ShowBusy("Kicsomagolás", $"{a.Name}  →  {dest}");
            var progress = new Progress<double>(v => { bar.IsIndeterminate = false; bar.Value = v; });
            try
            {
                await ArchiveTools.ExtractAsync(a.FullPath, dest, progress);
                if (mode != "here") lastFolder = Path.GetFileName(dest);
                CloseOverlay(true);
            }
            catch (Exception ex)
            {
                CloseOverlay(false);
                await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message);
                break;
            }
        }

        await LeftPane.RefreshAsync();
        await RightPane.RefreshAsync();
        var target = mode == "other" ? Other : Active;
        if (lastFolder != null) await target.RefreshAsync(lastFolder);
        Active.FocusList();
    }

    private async Task CompressAsync()
    {
        var sel = Active.GetSelection();
        if (sel.Count == 0) return;

        string suggested = sel.Count == 1
            ? (sel[0].IsDirectory ? sel[0].Name : Path.GetFileNameWithoutExtension(sel[0].Name))
            : Path.GetFileName(Active.CurrentPath.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(suggested)) suggested = "Archívum";

        var name = await Dialogs.PromptAsync(Root.XamlRoot, "Tömörítés ZIP-be",
            $"{Describe(sel)} tömörítése ide: {Active.CurrentPath}", suggested + ".zip", "Tömörítés", selectStemOnly: true);
        if (name == null) { Active.FocusList(); return; }
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";

        var zip = Path.Combine(Active.CurrentPath, name);
        if (File.Exists(zip))
            zip = ArchiveTools.UniquePath(Path.Combine(Active.CurrentPath, Path.GetFileNameWithoutExtension(name)), ".zip");

        var (bar, _) = ShowBusy("Tömörítés", $"{Describe(sel)}  →  {Path.GetFileName(zip)}");
        var progress = new Progress<double>(v => { bar.IsIndeterminate = false; bar.Value = v; });
        try
        {
            await ArchiveTools.CompressAsync(sel.Select(i => i.FullPath).ToList(), zip, progress);
            CloseOverlay(true);
        }
        catch (Exception ex)
        {
            CloseOverlay(false);
            await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message);
        }
        await Active.RefreshAsync(Path.GetFileName(zip));
        Active.FocusList();
    }

    // ------------------------------------------------------------------ Kereső

    private void OpenSearch()
    {
        if (_search != null)
        {
            _search.SetFolder(Active.CurrentPath);
            _search.Activate();
            return;
        }
        _search = new SearchWindow(Active.CurrentPath, _settings.Backdrop, GoToFoundAsync);
        _search.Closed += (_, _) => _search = null;
        _search.Activate();
    }

    /// <summary>Kereső találat: az aktív panel a fájl mappájába lép, és kijelöli.</summary>
    private async Task GoToFoundAsync(string folder, string name)
    {
        if (_previewOn) TogglePreview();
        await Active.NavigateAsync(folder, focusName: name);
        Activate();
        Active.FocusList();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => OpenSearch();

    // ------------------------------------------------------------------ Gyorsnézet

    private void TogglePreview()
    {
        _previewOn = !_previewOn;
        PreviewToggle.IsChecked = _previewOn;
        if (_previewOn) UpdatePreview(reposition: true);
        else
        {
            Preview.Visibility = Visibility.Collapsed;
            LeftPane.Visibility = Visibility.Visible;
            RightPane.Visibility = Visibility.Visible;
        }
        Active.FocusList();
    }

    private async void UpdatePreview(bool reposition = false)
    {
        if (!_previewOn) return;
        if (reposition)
        {
            Grid.SetColumn(Preview, _leftActive ? 1 : 0);
            Other.Visibility = Visibility.Collapsed;
            Active.Visibility = Visibility.Visible;
            Preview.Visibility = Visibility.Visible;
        }
        try { await Preview.ShowAsync(Active.FocusedItem()); }
        catch { }
    }

    private void OnPaneCurrentItemChanged(object? sender, EventArgs e)
    {
        if (_previewOn && ReferenceEquals(sender, Active)) UpdatePreview();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        PreviewToggle.IsChecked = _previewOn;   // a TogglePreview állítja át
        TogglePreview();
    }

    private async void OnPaneDrop(FilePane pane, IReadOnlyList<string> paths, string target, bool move)
    {
        SetActive(pane == LeftPane, focus: false);
        try
        {
            await ShellOps.CopyMoveAsync(_hwnd, paths, target, move);
            await RefreshBothAsync();
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message); }
    }

    // ------------------------------------------------------------------ Háttér (Mica / Acrylic)

    private void ApplyBackdrop(string kind)
    {
        SystemBackdrop = kind switch
        {
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "MicaAlt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            _ => new MicaBackdrop { Kind = MicaKind.Base },
        };
        BackdropButton.Label = kind switch
        {
            "Acrylic" => "Háttér: Acrylic",
            "MicaAlt" => "Háttér: Mica Alt",
            _ => "Háttér: Mica",
        };
        _settings.Backdrop = kind;
    }

    private void Backdrop_Click(object sender, RoutedEventArgs e)
    {
        ApplyBackdrop(_settings.Backdrop switch
        {
            "Mica" => "MicaAlt",
            "MicaAlt" => "Acrylic",
            _ => "Mica",
        });
    }

    // ------------------------------------------------------------------ Billentyűzet

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Dialogs.IsOpen) return;
        if (OverlayOpen)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                if (_overlayCancelable) CloseOverlay(false);
            }
            return;
        }

        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        bool inList = focused is ListViewItem || focused is ListView;
        bool inText = focused is TextBox;

        Func<Task>? action = null;

        switch (e.Key)
        {
            case VirtualKey.Tab when ctrl:
                action = () => { Active.SwitchTab(shift ? -1 : 1); return Task.CompletedTask; };
                break;
            case VirtualKey.Tab when !alt && !inText:
                action = () => { SetActive(!_leftActive, focus: true); return Task.CompletedTask; };
                break;

            case VirtualKey.Escape when Active.SearchActive:
                action = () => { Active.ClearSearch(); return Task.CompletedTask; };
                break;
            case VirtualKey.Back when inList && Active.SearchActive:
                action = () => { Active.SearchBackspace(); return Task.CompletedTask; };
                break;

            case VirtualKey.F2 when !alt && !inText: action = RenameAsync; break;
            case VirtualKey.F3: action = ViewAsync; break;
            case VirtualKey.F4 when !alt: action = EditAsync; break;
            case VirtualKey.F5 when alt: action = CompressAsync; break;
            case VirtualKey.F5: action = () => CopyMoveAsync(move: false); break;
            case VirtualKey.F9 when alt: action = () => ExtractAsync("folder"); break;
            case VirtualKey.F6 when shift: action = RenameAsync; break;
            case VirtualKey.F6: action = () => CopyMoveAsync(move: true); break;
            case VirtualKey.F7 when alt:
                action = () => { OpenSearch(); return Task.CompletedTask; };
                break;
            case VirtualKey.F7: action = NewFolderAsync; break;
            case VirtualKey.F when ctrl:
                action = () => { OpenSearch(); return Task.CompletedTask; };
                break;
            case VirtualKey.F8: action = () => DeleteAsync(permanent: shift); break;
            case VirtualKey.Delete when inList: action = () => DeleteAsync(permanent: shift); break;

            case VirtualKey.F1 when alt:
                action = () => { SetActive(true, false); LeftPane.OpenDriveList(); return Task.CompletedTask; };
                break;
            case VirtualKey.F2 when alt:
                action = () => { SetActive(false, false); RightPane.OpenDriveList(); return Task.CompletedTask; };
                break;

            case VirtualKey.Enter when alt && inList:
                action = () => { PropertiesOfSelection(); return Task.CompletedTask; };
                break;
            case VirtualKey.Enter when inList: action = () => Active.OpenFocusedAsync(); break;
            case VirtualKey.Back when inList: action = () => Active.GoUpAsync(); break;
            case VirtualKey.Left when alt: action = () => Active.GoBackAsync(); break;
            case VirtualKey.Up when alt: action = () => Active.GoUpAsync(); break;
            case VirtualKey.Up when ctrl && inList: action = () => Active.OpenFocusedInNewTabAsync(); break;
            case VirtualKey.Insert when inList:
                action = () => { Active.ToggleFocusedAndAdvance(); return Task.CompletedTask; };
                break;

            case VirtualKey.A when ctrl && inList:
                action = () => { Active.SelectAll(); return Task.CompletedTask; };
                break;
            case VirtualKey.C when ctrl && shift && inList:
                action = () => { CopyPaths(); return Task.CompletedTask; };
                break;
            case VirtualKey.C when ctrl && inList: action = () => ClipboardCopyAsync(cut: false); break;
            case VirtualKey.X when ctrl && inList: action = () => ClipboardCopyAsync(cut: true); break;
            case VirtualKey.V when ctrl && !inText: action = PasteAsync; break;

            case VirtualKey.R when ctrl: action = RefreshBothAsync; break;
            case VirtualKey.H when ctrl: action = ToggleHiddenAsync; break;
            case VirtualKey.U when ctrl: action = SwapAsync; break;
            case VirtualKey.I when ctrl: action = SamePathAsync; break;
            case VirtualKey.L when ctrl:
                action = () => { Active.FocusPathBox(); return Task.CompletedTask; };
                break;
            case VirtualKey.T when ctrl && shift:
                action = () => { ShellOps.OpenTerminal(Active.CurrentPath); return Task.CompletedTask; };
                break;
            case VirtualKey.T when ctrl: action = () => Active.NewTabAsync(); break;
            case VirtualKey.W when ctrl: action = () => Active.CloseCurrentTabAsync(); break;
            case VirtualKey.Q when ctrl:
                action = () => { TogglePreview(); return Task.CompletedTask; };
                break;
            case VirtualKey.E when ctrl:
                action = () => { ShellOps.OpenInExplorer(Active.CurrentPath); return Task.CompletedTask; };
                break;
        }

        if (action == null) return;
        e.Handled = true;   // még az első await előtt!
        try { await action(); }
        catch (Exception ex) { await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message); }
    }

    // ------------------------------------------------------------------ Műveletek

    private static string Describe(IReadOnlyList<FileItem> items) =>
        items.Count == 1 ? $"„{items[0].Name}”" : $"{items.Count} elem";

    private async Task CopyMoveAsync(bool move)
    {
        var sel = Active.GetSelection();
        if (sel.Count == 0) return;

        var target = await Dialogs.PromptAsync(Root.XamlRoot,
            move ? "Mozgatás" : "Másolás",
            $"{Describe(sel)} {(move ? "mozgatása" : "másolása")} ide:",
            Other.CurrentPath,
            move ? "Mozgatás" : "Másolás");
        if (target == null) { Active.FocusList(); return; }

        target = Environment.ExpandEnvironmentVariables(target.Trim('"'));
        if (!Path.IsPathRooted(target)) target = Path.Combine(Active.CurrentPath, target);

        await ShellOps.CopyMoveAsync(_hwnd, sel.Select(i => i.FullPath).ToList(), target, move);
        await RefreshBothAsync();
    }

    private async Task RenameAsync()
    {
        var item = Active.FocusedItem();
        if (item == null || item.IsParent) return;

        var newName = await Dialogs.PromptAsync(Root.XamlRoot, "Átnevezés", "Új név:", item.Name, "Átnevezés",
                                                selectStemOnly: !item.IsDirectory);
        if (newName == null || newName == item.Name) { Active.FocusList(); return; }
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await Dialogs.ErrorAsync(Root.XamlRoot, "A név nem tartalmazhatja ezeket: \\ / : * ? \" < > |");
            return;
        }

        var dest = Path.Combine(Path.GetDirectoryName(item.FullPath)!, newName);
        await ShellOps.RenameAsync(_hwnd, item.FullPath, dest);
        await Active.RefreshAsync(newName);
        Active.FocusList();
    }

    private async Task NewFolderAsync()
    {
        var name = await Dialogs.PromptAsync(Root.XamlRoot, "Új mappa", $"Új mappa itt: {Active.CurrentPath}",
                                             "Új mappa", "Létrehozás");
        if (name == null) { Active.FocusList(); return; }
        try
        {
            Directory.CreateDirectory(Path.Combine(Active.CurrentPath, name));
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message);
            return;
        }
        var first = name.Split('\\', '/')[0];
        await Active.RefreshAsync(first);
        Active.FocusList();
    }

    private async Task DeleteAsync(bool permanent)
    {
        var sel = Active.GetSelection();
        if (sel.Count == 0) return;

        var msg = permanent
            ? $"{Describe(sel)} VÉGLEGES törlése? Ez nem vonható vissza."
            : $"{Describe(sel)} áthelyezése a Lomtárba?";
        if (!await Dialogs.ConfirmAsync(Root.XamlRoot, permanent ? "Végleges törlés" : "Törlés", msg, "Törlés"))
        {
            Active.FocusList();
            return;
        }

        await ShellOps.DeleteAsync(_hwnd, sel.Select(i => i.FullPath).ToList(), recycle: !permanent);
        await RefreshBothAsync();
    }

    private async Task ViewAsync()
    {
        var item = Active.FocusedItem();
        if (item == null) return;
        await Active.OpenAsync(item);
    }

    private async Task EditAsync()
    {
        var item = Active.FocusedItem();
        if (item == null || item.IsDirectory) return;
        try { ShellOps.Edit(item.FullPath); }
        catch (Exception ex) { await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message); }
    }

    private async Task ClipboardCopyAsync(bool cut)
    {
        var sel = Active.GetSelection();
        if (sel.Count == 0) return;
        await ShellOps.SetClipboardFilesAsync(sel, cut);
    }

    private async Task PasteAsync()
    {
        var clip = await ShellOps.GetClipboardFilesAsync();
        if (clip == null || clip.Value.Paths.Count == 0) return;
        await ShellOps.CopyMoveAsync(_hwnd, clip.Value.Paths, Active.CurrentPath, clip.Value.Move);
        await RefreshBothAsync();
    }

    private void CopyPaths()
    {
        var sel = Active.GetSelection();
        ShellOps.SetClipboardText(sel.Count == 0
            ? Active.CurrentPath
            : string.Join(Environment.NewLine, sel.Select(i => i.FullPath)));
    }

    private void PropertiesOfSelection()
    {
        var item = Active.FocusedItem();
        ShellOps.ShowProperties(_hwnd, item == null || item.IsParent ? Active.CurrentPath : item.FullPath);
    }

    private async Task RefreshBothAsync()
    {
        await LeftPane.RefreshAsync();
        await RightPane.RefreshAsync();
        Active.FocusList();
    }

    private async Task ToggleHiddenAsync()
    {
        _settings.ShowHidden = !_settings.ShowHidden;
        HiddenToggle.IsChecked = _settings.ShowHidden;
        LeftPane.ShowHidden = RightPane.ShowHidden = _settings.ShowHidden;
        await RefreshBothAsync();
    }

    private async Task SwapAsync()
    {
        var l = LeftPane.CurrentPath;
        var r = RightPane.CurrentPath;
        await LeftPane.NavigateAsync(r);
        await RightPane.NavigateAsync(l);
        SetActive(!_leftActive, focus: true);
    }

    private async Task SamePathAsync()
    {
        await Other.NavigateAsync(Active.CurrentPath);
        Active.FocusList();
    }

    private async void OnPaneCommand(FilePane pane, string cmd)
    {
        SetActive(pane == LeftPane, focus: false);
        try
        {
            switch (cmd)
            {
                case "open": await Active.OpenFocusedAsync(); break;
                case "opentab": await Active.OpenFocusedInNewTabAsync(); break;
                case "newfolder": await NewFolderAsync(); break;
                case "newfile": await NewTextFileAsync(); break;
                case "refresh": await RefreshBothAsync(); break;
                case "explorer": ShellOps.OpenInExplorer(Active.CurrentPath); break;
                case "folderprops": ShellOps.ShowProperties(_hwnd, Active.CurrentPath); break;
                case "extract-folder": await ExtractAsync("folder"); break;
                case "extract-here": await ExtractAsync("here"); break;
                case "extract-other": await ExtractAsync("other"); break;
                case "compress": await CompressAsync(); break;
                case "edit": await EditAsync(); break;
                case "cut": await ClipboardCopyAsync(cut: true); break;
                case "clipcopy": await ClipboardCopyAsync(cut: false); break;
                case "paste": await PasteAsync(); break;
                case "copypath": CopyPaths(); break;
                case "copy": await CopyMoveAsync(move: false); break;
                case "move": await CopyMoveAsync(move: true); break;
                case "rename": await RenameAsync(); break;
                case "delete": await DeleteAsync(permanent: false); break;
                case "terminal": ShellOps.OpenTerminal(Active.CurrentPath); break;
                case "properties": PropertiesOfSelection(); break;
            }
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message); }
    }

    // ------------------------------------------------------------------ Gombok

    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await Dialogs.ErrorAsync(Root.XamlRoot, ex.Message); }
    }

    private void F2_Click(object sender, RoutedEventArgs e) => Run(RenameAsync);
    private void F3_Click(object sender, RoutedEventArgs e) => Run(ViewAsync);
    private void F4_Click(object sender, RoutedEventArgs e) => Run(EditAsync);
    private void F5_Click(object sender, RoutedEventArgs e) => Run(() => CopyMoveAsync(false));
    private void F6_Click(object sender, RoutedEventArgs e) => Run(() => CopyMoveAsync(true));
    private void F7_Click(object sender, RoutedEventArgs e) => Run(NewFolderAsync);
    private void F8_Click(object sender, RoutedEventArgs e) => Run(() => DeleteAsync(false));
    private void NewFolder_Click(object sender, RoutedEventArgs e) => Run(NewFolderAsync);
    private void Paste_Click(object sender, RoutedEventArgs e) => Run(PasteAsync);
    private void Swap_Click(object sender, RoutedEventArgs e) => Run(SwapAsync);
    private void SamePath_Click(object sender, RoutedEventArgs e) => Run(SamePathAsync);

    private void Hidden_Click(object sender, RoutedEventArgs e)
    {
        // A toggle már átállt; a ToggleHiddenAsync visszaállítja a saját állapotából.
        HiddenToggle.IsChecked = _settings.ShowHidden;
        Run(ToggleHiddenAsync);
    }

    private void Terminal_Click(object sender, RoutedEventArgs e) => ShellOps.OpenTerminal(Active.CurrentPath);
    private void Explorer_Click(object sender, RoutedEventArgs e) => ShellOps.OpenInExplorer(Active.CurrentPath);
}
