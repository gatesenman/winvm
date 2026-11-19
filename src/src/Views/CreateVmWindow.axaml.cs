using Avalonia.Controls;
using Avalonia.Interactivity;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class CreateVmWindow : Window
{
    public CreateVmWindow()
    {
        InitializeComponent();
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CreateVmViewModel vm)
        {
            vm.CloseRequested += ok => Close(ok);
            vm.PickIsoAsync = start => DialogService.PickIsoAsync(this, start);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is CreateVmViewModel { IsBusy: true }) e.Cancel = true; // never abandon a running create
        base.OnClosing(e);
    }

    private void OnRecentClicked(object? sender, RoutedEventArgs e) => RecentButton.Flyout?.Hide();
}
