using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not PreferencesViewModel vm) return;
            vm.CloseRequested += ok => Close(ok);
            vm.PickFolderAsync = (title, start) => DialogService.PickFolderAsync(this, title, start);
        };
        Opened += (_, _) => OkButton.Focus();
    }
}
