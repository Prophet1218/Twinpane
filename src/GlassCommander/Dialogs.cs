using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace GlassCommander;

internal static class Dialogs
{
    public static bool IsOpen { get; private set; }

    private static void ApplyStyle(ContentDialog dlg)
    {
        try
        {
            if (Application.Current.Resources["DefaultContentDialogStyle"] is Style s) dlg.Style = s;
        }
        catch { /* alapstílus marad */ }
    }

    private static ContentDialog Create(XamlRoot root, string title, object content, string primary)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "Mégse",
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyStyle(dlg);
        return dlg;
    }

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dlg)
    {
        if (IsOpen) return ContentDialogResult.None;
        IsOpen = true;
        try { return await dlg.ShowAsync(); }
        finally { IsOpen = false; }
    }

    /// <summary>Szövegbekérő ablak. selectStemOnly: átnevezésnél csak a név legyen kijelölve, a kiterjesztés ne.</summary>
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string message,
                                                  string initial, string primary, bool selectStemOnly = false)
    {
        var box = new TextBox { Text = initial };
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var dlg = Create(root, title, panel, primary);
        bool accepted = false;

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
            if (e.Key == VirtualKey.Enter)
            {
                accepted = true;
                e.Handled = true;
                dlg.Hide();
            }
        };

        var result = await ShowAsync(dlg);
        if (!accepted && result != ContentDialogResult.Primary) return null;
        var text = box.Text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary)
    {
        var dlg = Create(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, primary);
        return await ShowAsync(dlg) == ContentDialogResult.Primary;
    }

    public static async Task ErrorAsync(XamlRoot? root, string message)
    {
        if (root == null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = "Hiba",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };
        ApplyStyle(dlg);
        await ShowAsync(dlg);
    }
}
