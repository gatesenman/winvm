using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinVM.Services;

namespace WinVM.ViewModels;

/// <summary>文件 → 偏好设置. Edits a copy; <see cref="Apply"/> writes to <see cref="AppPreferences"/> on 确定.</summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    private readonly AppPreferences _prefs;

    public static IReadOnlyList<string> ThemeNames { get; } = new[] { "跟随系统", "浅色", "深色" };
    public static IReadOnlyList<string> RefreshNames { get; } = new[] { "2 秒", "4 秒（推荐）", "10 秒", "30 秒" };
    private static readonly int[] RefreshValues = { 2, 4, 10, 30 };

    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private bool _autoElevate;
    [ObservableProperty] private bool _showTrayIcon;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _showNotifications;
    [ObservableProperty] private int _refreshIndex;
    [ObservableProperty] private string _isoFolder;

    public string StorePath { get; }
    public string LogFolder => Log.Directory;
    public bool IsElevatedNow => Elevation.IsElevated;
    public string ElevationStateText => !OperatingSystem.IsWindows() ? "" : IsElevatedNow ? "当前：已以管理员身份运行" : "当前：未以管理员身份运行";
    public bool CanMinimizeToTray => ShowTrayIcon;

    public event Action<bool>? CloseRequested;
    /// <summary>Set by the view: folder picker.</summary>
    public Func<string, string?, Task<string?>>? PickFolderAsync { get; set; }

    public PreferencesViewModel(AppPreferences prefs, string storePath)
    {
        _prefs = prefs;
        StorePath = storePath;
        _themeIndex = prefs.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _autoElevate = prefs.AutoElevate;
        _showTrayIcon = prefs.ShowTrayIcon;
        _minimizeToTray = prefs.MinimizeToTray && prefs.ShowTrayIcon;
        _showNotifications = prefs.ShowNotifications;
        var idx = Array.IndexOf(RefreshValues, prefs.EffectiveRefreshSeconds);
        _refreshIndex = idx < 0 ? 1 : idx;
        _isoFolder = prefs.EffectiveIsoFolder;
    }

    partial void OnShowTrayIconChanged(bool value)
    {
        if (!value) MinimizeToTray = false;
        OnPropertyChanged(nameof(CanMinimizeToTray));
    }

    public string ThemeValue => ThemeIndex switch { 1 => "light", 2 => "dark", _ => "system" };
    public int RefreshSecondsValue => RefreshValues[Math.Clamp(RefreshIndex, 0, RefreshValues.Length - 1)];

    /// <summary>Writes the edited values to the preferences and saves them.</summary>
    public void Apply()
    {
        _prefs.Theme = ThemeValue;
        _prefs.AutoElevate = AutoElevate;
        _prefs.ShowTrayIcon = ShowTrayIcon;
        _prefs.MinimizeToTray = ShowTrayIcon && MinimizeToTray;
        _prefs.ShowNotifications = ShowNotifications;
        _prefs.RefreshSeconds = RefreshSecondsValue;
        _prefs.IsoDownloadFolder = string.Equals(IsoFolder, IsoCatalog.DefaultIsoFolder, StringComparison.OrdinalIgnoreCase) ? null : IsoFolder;
        _prefs.Save();
    }

    [RelayCommand]
    private void Ok()
    {
        Apply();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    [RelayCommand]
    private async Task BrowseIsoFolderAsync()
    {
        if (PickFolderAsync == null) return;
        var p = await PickFolderAsync("选择镜像下载文件夹", IsoFolder);
        if (!string.IsNullOrWhiteSpace(p)) IsoFolder = p;
    }

    [RelayCommand]
    private void ResetIsoFolder() => IsoFolder = IsoCatalog.DefaultIsoFolder;

    [RelayCommand]
    private void OpenStoreFolder() => Elevation.OpenFolder(StorePath);

    [RelayCommand]
    private void OpenLogFolder() => Elevation.OpenFolder(LogFolder);
}
