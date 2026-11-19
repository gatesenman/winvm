using System.Text.Json;

namespace WinVM.Services;

/// <summary>Persists UI preferences under %LOCALAPPDATA%\WinVM\preferences.json.</summary>
public class AppPreferences
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string? LastIsoFolder { get; set; }
    public List<string> RecentIsoPaths { get; set; } = new();

    // Window state
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 740;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }
    public double SidebarWidth { get; set; } = 290;

    // View
    public string SortMode { get; set; } = "name";   // name | state
    public string Theme { get; set; } = "system";    // system | light | dark
    public bool AutoElevate { get; set; }

    // Behaviour (1.4)
    /// <summary>Show the notification-area (tray) icon with quick VM actions.</summary>
    public bool ShowTrayIcon { get; set; } = true;
    /// <summary>Closing the main window hides it to the tray instead of exiting (requires the tray icon).</summary>
    public bool MinimizeToTray { get; set; }
    /// <summary>In-app notifications when long operations finish.</summary>
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Auto-refresh interval in seconds (2–60).</summary>
    public int RefreshSeconds { get; set; } = 4;
    /// <summary>Where gallery downloads are saved; null = %USERPROFILE%\WinVM\ISO.</summary>
    public string? IsoDownloadFolder { get; set; }
    /// <summary>Last folder used for export/import.</summary>
    public string? LastExportFolder { get; set; }

    // Guest connect / first-boot (1.6) — never store passwords here
    /// <summary>Last username offered for RDP/SSH / agent connect-info.</summary>
    public string? LastGuestUser { get; set; }
    public string? LastGuestHostname { get; set; }
    public bool PreferAgentReadyProfile { get; set; }

    public string EffectiveIsoFolder => string.IsNullOrWhiteSpace(IsoDownloadFolder) ? IsoCatalog.DefaultIsoFolder : IsoDownloadFolder!;
    public int EffectiveRefreshSeconds => Math.Clamp(RefreshSeconds, 2, 60);

    // Defaults for the new-VM dialog
    public string? LastSwitch { get; set; }
    public int LastCpu { get; set; } = 2;
    public int LastMemoryGiB { get; set; } = 4;
    public int LastDiskGiB { get; set; } = 64;

    public static string StoreDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVM");

    private static string StorePath => Path.Combine(StoreDir, "preferences.json");

    private static AppPreferences? _shared;
    /// <summary>Process-wide instance (loaded once).</summary>
    public static AppPreferences Shared => _shared ??= Load();

    public static AppPreferences Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(StorePath)) ?? new AppPreferences();
        }
        catch (Exception ex)
        {
            Log.Warn("preferences.json unreadable, using defaults: " + ex.Message);
        }
        return new AppPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Saving preferences failed: " + ex.Message);
        }
    }

    public void RememberIso(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) LastIsoFolder = dir;
        RecentIsoPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentIsoPaths.Insert(0, path);
        if (RecentIsoPaths.Count > 8) RecentIsoPaths = RecentIsoPaths.Take(8).ToList();
        Save();
    }
}
