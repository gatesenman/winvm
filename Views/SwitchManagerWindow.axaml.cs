using Avalonia.Controls;
using Avalonia.Interactivity;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class SwitchManagerWindow : Window
{
    public SwitchManagerWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is SwitchManagerViewModel { IsBusy: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
