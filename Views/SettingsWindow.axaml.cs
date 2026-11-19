using Avalonia.Controls;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsViewModel vm)
        {
            vm.CloseRequested += ok => Close(ok);
            vm.PickIsoAsync = start => DialogService.PickIsoAsync(this, start);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is SettingsViewModel { IsBusy: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
