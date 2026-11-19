using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class OneClickWizardWindow : Window
{
    public OneClickWizardWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is OneClickInstallViewModel vm)
            vm.CloseRequested += ok => Close(ok);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is OneClickInstallViewModel { IsBusy: true } vm)
        {
            // Allow cancel via token rather than hard-block; user pressed window X → request cancel
            vm.CancelCommand.Execute(null);
            if (vm.IsBusy) e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not OneClickInstallViewModel vm || !vm.CanSelect) return;
        if (sender is Border { Tag: IsoItemViewModel item })
        {
            if (item.IsLinkOnly)
            {
                item.OpenHomePageCommand.Execute(null);
                return;
            }
            if (item.IsUnsupported) return;
            vm.PickCommand.Execute(item);
            HighlightSelection(item);
        }
    }

    private void HighlightSelection(IsoItemViewModel selected)
    {
        // Visual feedback is driven by Selected binding where possible; cards use pointer for pick.
        _ = selected;
    }
}
