using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WinVM.Views;

public partial class PromptWindow : Window
{
    private readonly bool _numeric;

    public PromptWindow()
    {
        InitializeComponent();
    }

    public PromptWindow(string title, string message, string? initialText = null, string confirmLabel = "确定",
        string? watermark = null, string? hint = null, bool numeric = false, decimal? numberValue = null,
        decimal numberMin = 1, decimal numberMax = 4096, string unit = "") : this()
    {
        _numeric = numeric;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        if (numeric)
        {
            InputBox.IsVisible = false;
            NumberRow.IsVisible = true;
            NumberBox.Minimum = numberMin;
            NumberBox.Maximum = numberMax;
            NumberBox.Value = Math.Clamp(numberValue ?? numberMin, numberMin, numberMax);
            UnitText.Text = unit;
        }
        else
        {
            InputBox.Text = initialText ?? "";
            InputBox.Watermark = watermark;
        }
        if (!string.IsNullOrEmpty(hint))
        {
            HintText.Text = hint;
            HintText.IsVisible = true;
        }
        Opened += (_, _) =>
        {
            if (_numeric) NumberBox.Focus();
            else { InputBox.Focus(); InputBox.SelectAll(); }
        };
    }

    public string InputText => InputBox.Text?.Trim() ?? "";
    public decimal NumberValue => NumberBox.Value ?? 0;

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (!_numeric && string.IsNullOrWhiteSpace(InputBox.Text))
        {
            ErrorText.Text = "请输入内容。";
            ErrorText.IsVisible = true;
            return;
        }
        if (_numeric && NumberBox.Value is null)
        {
            ErrorText.Text = "请输入有效的数字。";
            ErrorText.IsVisible = true;
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
