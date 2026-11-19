using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace WinVM.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    public ConfirmWindow(string title, string message, string? optionLabel = null, bool optionDefault = false,
        string confirmLabel = "确定", bool danger = false, bool showCancel = true, bool error = false) : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        CancelButton.IsVisible = showCancel;
        if (!showCancel) ConfirmButton.IsCancel = true;
        if (danger)
        {
            ConfirmButton.Classes.Remove("accent");
            ConfirmButton.Classes.Add("danger");
        }
        if (danger || error)
        {
            // Theme-dependent resources must be bound dynamically: before the window is shown
            // there is no theme variant yet and a direct lookup returns UnsetValue.
            Glyph.Bind(WinVM.Controls.IconView.DataProperty, this.GetResourceObservable("I.Warning"));
            Glyph.Bind(WinVM.Controls.IconView.ForegroundProperty, this.GetResourceObservable("App.Danger"));
        }
        if (!string.IsNullOrEmpty(optionLabel))
        {
            OptionCheck.Content = optionLabel;
            OptionCheck.IsChecked = optionDefault;
            OptionCheck.IsVisible = true;
        }
        Opened += (_, _) => (showCancel && danger ? CancelButton : ConfirmButton).Focus();
    }

    public bool OptionChecked => OptionCheck.IsChecked == true;

    /// <summary>Shows a longer explanation below the message (e.g. host problems) with a copy button.</summary>
    public void SetDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return;
        DetailText.Text = detail;
        DetailPanel.IsVisible = true;
        CopyButton.IsVisible = true;
        Width = 600;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is { } cb) await cb.SetTextAsync(TitleText.Text + "\n" + MessageText.Text + "\n\n" + DetailText.Text);
            CopyButton.Content = "已复制";
        }
        catch { }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
