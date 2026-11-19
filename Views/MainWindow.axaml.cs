using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class MainWindow : Window
{
    private readonly AppPreferences _prefs;

    public MainWindow() : this(AppPreferences.Shared) { }

    public MainWindow(AppPreferences prefs)
    {
        _prefs = prefs;
        InitializeComponent();
        RestorePlacement();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Power shortcuts act only while this window is the focused one (not a dialog, not in the background).
        if (DataContext is MainViewModel vm) vm.IsWindowActive = () => IsActive && IsVisible && WindowState != WindowState.Minimized;
    }

    private void RestorePlacement()
    {
        Width = Math.Clamp(_prefs.WindowWidth, MinWidth, 4000);
        Height = Math.Clamp(_prefs.WindowHeight, MinHeight, 3000);
        if (_prefs.WindowX is int x && _prefs.WindowY is int y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (SplitGrid.ColumnDefinitions.Count > 0)
            SplitGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(_prefs.SidebarWidth, 220, 520));
        if (_prefs.WindowMaximized) WindowState = WindowState.Maximized;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Make sure a restored position is still on a connected screen.
        var screen = Screens.ScreenFromWindow(this);
        if (screen == null && Screens.Primary is { } primary)
            Position = new PixelPoint(primary.WorkingArea.X + 60, primary.WorkingArea.Y + 60);
    }

    /// <summary>True once the user really wants to exit (menu/tray "退出", or confirmed with running tasks).</summary>
    public bool ExitRequested { get; set; }
    /// <summary>Set by the app when a tray icon exists, so "minimize to tray" can hide instead of exit.</summary>
    public Func<bool>? TrayAvailable { get; set; }
    private bool _confirmingExit;
    private bool _tasksConfirmed;
    private bool _hintedTray;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        // 1) "关闭窗口时最小化到托盘": hide instead of exit (only for the user's own close request).
        if (!ExitRequested && e.CloseReason == WindowCloseReason.WindowClosing && _prefs.MinimizeToTray && TrayAvailable?.Invoke() == true)
        {
            e.Cancel = true;
            SavePlacement();
            Hide();
            if (!_hintedTray && vm != null)
            {
                _hintedTray = true;
                Log.Info("Window hidden to tray");
            }
            return;
        }
        // 2) Long background operations: ask before cancelling them.
        if (vm is { HasLongTasks: true } && !_tasksConfirmed && !vm.IsDisposed)
        {
            e.Cancel = true;
            if (!_confirmingExit) _ = ConfirmExitWithTasksAsync(vm);
            return;
        }
        SavePlacement();
        Log.Info($"Shutdown: window closing (reason={e.CloseReason})");
        base.OnClosing(e);
    }

    private async Task ConfirmExitWithTasksAsync(MainViewModel vm)
    {
        _confirmingExit = true;
        try
        {
            if (!IsVisible) Show();
            var names = string.Join("、", vm.Tasks.Select(t => t.Title).Take(3));
            var dlg = new ConfirmWindow("退出 WinVM", $"以下任务仍在进行：{names}。\n\n退出将取消这些任务并清理未完成的文件（已下载的镜像部分会保留，可下次继续）。",
                null, false, "取消任务并退出", danger: true);
            if (!await dlg.ShowDialog<bool>(this)) { ExitRequested = false; return; }
            vm.StatusMessage = "正在取消任务…";
            await vm.CancelAllTasksAsync(TimeSpan.FromSeconds(8));
            _tasksConfirmed = true;
            ExitRequested = true;
            Close();
        }
        finally
        {
            _confirmingExit = false;
        }
    }

    private void SavePlacement()
    {
        try
        {
            _prefs.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _prefs.WindowWidth = Bounds.Width;
                _prefs.WindowHeight = Bounds.Height;
                _prefs.WindowX = Position.X;
                _prefs.WindowY = Position.Y;
            }
            var w = SplitGrid.ColumnDefinitions[0].ActualWidth;
            if (w > 100) _prefs.SidebarWidth = w;
            _prefs.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("Saving window placement failed: " + ex.Message);
        }
    }

    /// <summary>Brings the window back (tray "显示主窗口", errors after tray actions).</summary>
    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsFocused && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && VmList.IsKeyboardFocusWithin
                 && DataContext is MainViewModel vm && vm.DeleteCommand.CanExecute(null))
        {
            vm.DeleteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && VmList.IsKeyboardFocusWithin
                 && DataContext is MainViewModel vm2 && vm2.OpenConsoleCommand.CanExecute(null))
        {
            vm2.OpenConsoleCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnVmListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only react when an item (not empty space / scrollbar) was double-clicked.
        if (e.Source is not Visual v || v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: VmInfo })
            return;
        if (DataContext is MainViewModel vm && vm.OpenConsoleCommand.CanExecute(null))
            vm.OpenConsoleCommand.Execute(null);
    }
}
