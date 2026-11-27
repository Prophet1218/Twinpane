using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace GlassCommander;

/// <summary>
/// A főablak saját, villanásmentes párbeszéd-rétege (overlay).
/// Ha nincs beállítva (pl. másik ablakban), a beépített ContentDialog a tartalék.
/// </summary>
internal interface IDialogHost
{
    Task<bool> ShowAsync(string title, UIElement content, string? primaryText, string? closeText, bool primaryIsDefault);
    void Close(bool primary);
}

internal static class Dialogs
{
    public static IDialogHost? Host { get; set; }
    public static bool IsOpen { get; private set; }

    private static async Task<bool> ShowAsync(XamlRoot? root, string title, UIElement content,
                                              string? primary, string close, bool primaryIsDefault)
    {
        if (Host != null)
            return await Host.ShowAsync(title, content, primary, close, primaryIsDefault);

        if (root == null || IsOpen) return false;
        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            CloseButtonText = close,
            DefaultButton = primary != null && primaryIsDefault ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };
        if (primary != null) dlg.PrimaryButtonText = primary;
        try
        {
            if (Application.Current.Resources["DefaultContentDialogStyle"] is Style s) dlg.Style = s;
        }
        catch { }

        IsOpen = true;
        try { return await dlg.ShowAsync() == ContentDialogResult.Primary; }
        finally { IsOpen = false; }
    }

    /// <summary>Szövegbekérő ablak. selectStemOnly: átnevezésnél csak a név legyen kijelölve, a kiterjesztés ne.</summary>
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string message,
                                                  string initial, string primary, bool selectStemOnly = false)
    {
        var box = new TextBox { Text = initial, IsSpellCheckEnabled = false };
        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            if (selectStemOnly)
            {
                int dot = initial.LastIndexOf('.');
                box.Select(0, dot > 0 ? dot : initial.Length);
            }
            else box.SelectAll();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && Host != null)
            {
                e.Handled = true;
                Host.Close(true);
            }
        };

        bool ok = await ShowAsync(root, title, panel, primary, "Mégse", primaryIsDefault: true);
        if (!ok) return null;
        var text = box.Text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        return ShowAsync(root, title, text, primary, "Mégse", primaryIsDefault: true);
    }

    public static async Task ErrorAsync(XamlRoot? root, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        await ShowAsync(root, "Hiba", text, null, "OK", primaryIsDefault: false);
    }
}
