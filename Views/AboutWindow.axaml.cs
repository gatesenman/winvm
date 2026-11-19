using Avalonia.Controls;
using Avalonia.Interactivity;
using WinVM.Services;

namespace WinVM.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {HyperVService.AppVersion}" + (Elevation.IsElevated ? " · 管理员" : "");
        StoreText.Text = HyperVService.DefaultStorePath;
        LogText.Text = Log.FilePath;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
    private void OnOpenLogs(object? sender, RoutedEventArgs e) => Elevation.OpenFolder(Log.Directory);
}
