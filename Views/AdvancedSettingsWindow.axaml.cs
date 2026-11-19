using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WinVM.Models;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class AdvancedSettingsWindow : Window
{
    private BootEntry? _dragging;

    public AdvancedSettingsWindow()
    {
        InitializeComponent();
        // simple drag & drop reordering of boot entries (press, move over another entry, release)
        BootList.AddHandler(PointerPressedEvent, OnBootPressed, RoutingStrategies.Tunnel);
        BootList.AddHandler(PointerMovedEvent, OnBootMoved, RoutingStrategies.Tunnel);
        BootList.AddHandler(PointerReleasedEvent, (_, _) => _dragging = null, RoutingStrategies.Tunnel);
    }

    private static BootEntry? EntryAt(object? source) =>
        source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: BootEntry b } ? b : null;

    private void OnBootPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(BootList).Properties.IsLeftButtonPressed) _dragging = EntryAt(e.Source);
    }

    private void OnBootMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging == null || DataContext is not AdvancedSettingsViewModel vm) return;
        if (!e.GetCurrentPoint(BootList).Properties.IsLeftButtonPressed) { _dragging = null; return; }
        var hit = BootList.InputHitTest(e.GetPosition(BootList)) as Visual;
        if (EntryAt(hit) is { } target && !ReferenceEquals(target, _dragging))
            vm.MoveBootTo(_dragging, vm.BootOrder.IndexOf(target));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AdvancedSettingsViewModel vm) vm.CloseRequested += ok => Close(ok);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is AdvancedSettingsViewModel { IsBusy: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
