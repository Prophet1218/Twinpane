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

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly IntPtr _hwnd;
    private bool _leftActive = true;

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

        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";

        HiddenToggle.IsChecked = _settings.ShowHidden;
        LeftPane.ShowHidden = RightPane.ShowHidden = _settings.ShowHidden;

        LeftPane.Activated += (_, _) => SetActive(true, focus: false);
        RightPane.Activated += (_, _) => SetActive(false, focus: false);
        LeftPane.CommandRequested += OnPaneCommand;
        RightPane.CommandRequested += OnPaneCommand;

        Root.Loaded += Root_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");

        await LeftPane.NavigateAsync(ValidDir(_settings.LeftPath) ?? home);
        await RightPane.NavigateAsync(ValidDir(_settings.RightPath) ?? (Directory.Exists(downloads) ? downloads : home));
        SetActive(true, focus: true);
    }

    private static string? ValidDir(string? p) => !string.IsNullOrEmpty(p) && Directory.Exists(p) ? p : null;

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _settings.LeftPath = LeftPane.CurrentPath;
        _settings.RightPath = RightPane.CurrentPath;
        _settings.Width = AppWindow.Size.Width;
        _settings.Height = AppWindow.Size.Height;
        _settings.Save();
        LeftPane.Shutdown();
        RightPane.Shutdown();
    }

    private void SetActive(bool left, bool focus)
    {
        _leftActive = left;
        LeftPane.IsActive = left;
        RightPane.IsActive = !left;
        if (focus) Active.FocusList();
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

        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        bool inList = focused is ListViewItem || focused is ListView;
        bool inText = focused is TextBox;

        Func<Task>? action = null;

        switch (e.Key)
        {
            case VirtualKey.Tab when !ctrl && !alt && !inText:
                action = () => { SetActive(!_leftActive, focus: true); return Task.CompletedTask; };
                break;

            case VirtualKey.F2 when !alt && !inText: action = RenameAsync; break;
            case VirtualKey.F3: action = ViewAsync; break;
            case VirtualKey.F4 when !alt: action = EditAsync; break;
            case VirtualKey.F5: action = () => CopyMoveAsync(move: false); break;
            case VirtualKey.F6 when shift: action = RenameAsync; break;
            case VirtualKey.F6: action = () => CopyMoveAsync(move: true); break;
            case VirtualKey.F7: action = NewFolderAsync; break;
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
            case VirtualKey.T when ctrl:
                action = () => { ShellOps.OpenTerminal(Active.CurrentPath); return Task.CompletedTask; };
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
