using CommunityToolkit.Mvvm.ComponentModel;

namespace WinVM.Models;

public enum VmState
{
    Unknown,
    Off,
    Running,
    Paused,
    Saved,
    Starting,
    Stopping,
    Saving,
    Pausing,
    Resuming,
    Resetting,
    Other
}

public enum OsKind
{
    Unknown,
    Windows,
    Linux
}

/// <summary>One Hyper-V virtual machine as shown in the library. Updated in place on refresh.</summary>
public partial class VmInfo : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private VmState _state = VmState.Unknown;
    [ObservableProperty] private int _processorCount;
    [ObservableProperty] private long _memoryStartupBytes;
    [ObservableProperty] private long _memoryAssignedBytes;
    [ObservableProperty] private bool _dynamicMemory;
    [ObservableProperty] private int _cpuUsage;
    [ObservableProperty] private long _uptimeSeconds;
    [ObservableProperty] private int _generation = 2;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string? _dvdPath;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private DateTime? _creationTime;
    [ObservableProperty] private string _switchName = "";
    [ObservableProperty] private string _guestIp = "";
    [ObservableProperty] private long _diskSizeBytes;
    [ObservableProperty] private long _diskFileBytes;
    [ObservableProperty] private string _vhdPath = "";
    [ObservableProperty] private int _checkpointCount;
    [ObservableProperty] private bool _secureBoot;
    [ObservableProperty] private OsKind _os = OsKind.Unknown;

    /// <summary>Live performance history (kept across refreshes; not part of <see cref="UpdateFrom"/>).</summary>
    public PerfHistory Perf { get; } = new();

    /// <summary>Stable identity across refreshes (survives rename).</summary>
    public string Key => string.IsNullOrEmpty(Id) ? "name:" + Name : Id;

    /// <summary>Argument passed to the Hyper-V service: the Id when known, otherwise the exact name.</summary>
    public string Ref => string.IsNullOrEmpty(Id) ? Name : Id;

    public string StateText => State switch
    {
        VmState.Off => "已关机",
        VmState.Running => "运行中",
        VmState.Paused => "已暂停",
        VmState.Saved => "已保存",
        VmState.Starting => "正在启动",
        VmState.Stopping => "正在关机",
        VmState.Saving => "正在保存",
        VmState.Pausing => "正在暂停",
        VmState.Resuming => "正在恢复",
        VmState.Resetting => "正在重置",
        VmState.Other => "其他",
        _ => "未知"
    };

    /// <summary>Running first, then Paused, Saved, transitional, Off.</summary>
    public int StateSortOrder => State switch
    {
        VmState.Running => 0,
        VmState.Paused => 1,
        VmState.Saved => 2,
        VmState.Off => 4,
        _ => 3
    };

    public bool IsTransient => State is VmState.Starting or VmState.Stopping or VmState.Saving
        or VmState.Pausing or VmState.Resuming or VmState.Resetting or VmState.Other or VmState.Unknown;

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        const double GiB = 1024.0 * 1024 * 1024;
        const double MiB = 1024.0 * 1024;
        return bytes >= GiB ? $"{bytes / GiB:0.##} GiB" : $"{bytes / MiB:0} MiB";
    }

    public static string FormatUptime(long seconds)
    {
        if (seconds <= 0) return "—";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} 天 {t.Hours} 小时";
        if (t.TotalHours >= 1) return $"{t.Hours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} 分 {t.Seconds} 秒";
        return $"{t.Seconds} 秒";
    }

    public string MemoryText => FormatBytes(MemoryStartupBytes) + (DynamicMemory ? "（动态）" : "");
    public string MemoryAssignedText => State is VmState.Running or VmState.Paused ? FormatBytes(MemoryAssignedBytes) : "—";
    public string CpuText => ProcessorCount > 0 ? $"{ProcessorCount} 个虚拟处理器" : "—";
    public string CpuShort => $"{ProcessorCount} 核";
    public string CpuUsageText => State == VmState.Running ? $"{CpuUsage}%" : "—";
    public string UptimeText => State is VmState.Running or VmState.Paused ? FormatUptime(UptimeSeconds) : "—";
    public string GenerationText => $"第 {Generation} 代" + (Generation == 2 ? "（UEFI）" : "（BIOS）");
    public string DiskText => DiskSizeBytes > 0
        ? (DiskFileBytes > 0 ? $"{FormatBytes(DiskSizeBytes)}（已占用 {FormatBytes(DiskFileBytes)}）" : FormatBytes(DiskSizeBytes))
        : "—";
    public string DiskShort => FormatBytes(DiskSizeBytes);
    public string SwitchDisplay => string.IsNullOrWhiteSpace(SwitchName) ? "未连接" : SwitchName;
    /// <summary>Comma-separated guest IPs reported by Hyper-V (empty until IC/KVP reports them).</summary>
    public string GuestIpDisplay => string.IsNullOrWhiteSpace(GuestIp) ? "—" : GuestIp.Split(',')[0].Trim();
    public bool HasGuestIp => !string.IsNullOrWhiteSpace(GuestIp);
    public string PrimaryGuestIp
    {
        get
        {
            if (string.IsNullOrWhiteSpace(GuestIp)) return "";
            foreach (var p in GuestIp.Split(','))
            {
                var s = p.Trim();
                if (s.Length > 0 && s.IndexOf(':') < 0) return s; // prefer IPv4
            }
            return GuestIp.Split(',')[0].Trim();
        }
    }
    public bool CanOpenRdp => HasGuestIp && State == VmState.Running;
    public bool CanOpenSsh => HasGuestIp && State == VmState.Running;
    public string IsoDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DvdPath)) return "未挂载";
            try { return System.IO.Path.GetFileName(DvdPath); }
            catch { return DvdPath!; }
        }
    }
    public string CheckpointText => CheckpointCount > 0 ? $"{CheckpointCount} 个" : "无";
    public string CreationText => CreationTime?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string OsText => Os switch { OsKind.Windows => "Windows", OsKind.Linux => "Linux", _ => "未知系统" };
    public string SecureBootText => Generation == 2 ? (SecureBoot ? "已启用" : "已关闭") : "不适用";

    /// <summary>Second line in the sidebar list.</summary>
    public string ListSubtitle => State is VmState.Running
        ? (HasGuestIp
            ? $"{StateText} · {GuestIpDisplay} · CPU {CpuUsage}%"
            : $"{StateText} · CPU {CpuUsage}% · {UptimeText}")
        : $"{StateText} · {CpuShort} · {FormatBytes(MemoryStartupBytes)}";

    public string SpecsLine => $"{OsText}  ·  {GenerationText}  ·  {CpuShort}  ·  {FormatBytes(MemoryStartupBytes)}  ·  {DiskShort}";

    public bool IsWindowsOs => Os == OsKind.Windows;
    public bool IsLinuxOs => Os == OsKind.Linux;
    public bool IsUnknownOs => Os == OsKind.Unknown;

    public bool IsRunningChip => State == VmState.Running;
    public bool IsPausedChip => State == VmState.Paused;
    public bool IsSavedChip => State == VmState.Saved;
    public bool IsOffChip => State == VmState.Off;
    public bool IsTransientChip => IsTransient;

    // Command availability (pure functions of state; unit-tested)
    public bool CanStart => State is VmState.Off or VmState.Saved;
    public bool CanStop => State is VmState.Running;
    public bool CanRestart => State == VmState.Running;
    public bool CanReset => State is VmState.Running or VmState.Paused;
    public bool CanPause => State == VmState.Running;
    public bool CanResume => State == VmState.Paused;
    public bool CanTurnOff => State is VmState.Running or VmState.Paused or VmState.Starting or VmState.Stopping or VmState.Saved;
    public bool CanDelete => !IsTransient;
    public bool CanEditSettings => State == VmState.Off;
    public bool CanRename => !IsTransient;
    public bool CanResizeDisk => State == VmState.Off && !string.IsNullOrEmpty(VhdPath);
    public bool CanCreateCheckpoint => State is VmState.Off or VmState.Running or VmState.Paused or VmState.Saved;
    public bool CanApplyCheckpoint => State is VmState.Off or VmState.Saved or VmState.Running or VmState.Paused;
    public bool CanOpenConsole => State != VmState.Unknown;
    public bool CanExport => State == VmState.Off;
    public bool CanClone => State == VmState.Off;
    public bool HasLivePerf => State is VmState.Running or VmState.Paused;
    public string MemoryAssignedShort => State is VmState.Running or VmState.Paused && MemoryAssignedBytes > 0 ? FormatBytes(MemoryAssignedBytes) : "—";
    public string MemoryUsageSub => State is VmState.Running or VmState.Paused && MemoryStartupBytes > 0
        ? $"已分配 / 配置 {FormatBytes(MemoryStartupBytes)}" : "虚拟机未运行";
    public string CpuUsageSub => State == VmState.Running ? $"{ProcessorCount} 个虚拟处理器" : "虚拟机未运行";
    public string UptimeSub => State is VmState.Running or VmState.Paused && UptimeSeconds > 0
        ? "启动于 " + DateTime.Now.AddSeconds(-UptimeSeconds).ToString("MM-dd HH:mm") : "虚拟机未运行";

    private static readonly string[] DerivedNames =
    {
        nameof(Key), nameof(Ref), nameof(StateText), nameof(StateSortOrder), nameof(IsTransient), nameof(MemoryText),
        nameof(MemoryAssignedText), nameof(CpuText), nameof(CpuShort), nameof(CpuUsageText), nameof(UptimeText),
        nameof(GenerationText), nameof(DiskText), nameof(DiskShort), nameof(SwitchDisplay), nameof(GuestIpDisplay), nameof(HasGuestIp), nameof(PrimaryGuestIp), nameof(CanOpenRdp), nameof(CanOpenSsh), nameof(IsoDisplay),
        nameof(CheckpointText), nameof(CreationText), nameof(OsText), nameof(SecureBootText), nameof(ListSubtitle),
        nameof(SpecsLine), nameof(IsWindowsOs), nameof(IsLinuxOs), nameof(IsUnknownOs), nameof(IsRunningChip),
        nameof(IsPausedChip), nameof(IsSavedChip), nameof(IsOffChip), nameof(IsTransientChip), nameof(CanStart),
        nameof(CanStop), nameof(CanRestart), nameof(CanReset), nameof(CanPause), nameof(CanResume), nameof(CanTurnOff),
        nameof(CanDelete), nameof(CanEditSettings), nameof(CanRename), nameof(CanResizeDisk), nameof(CanCreateCheckpoint),
        nameof(CanApplyCheckpoint), nameof(CanOpenConsole), nameof(CanExport), nameof(CanClone), nameof(HasLivePerf),
        nameof(MemoryAssignedShort), nameof(MemoryUsageSub), nameof(CpuUsageSub), nameof(UptimeSub)
    };

    private int _suspendNotify;

    public void NotifyDerived()
    {
        if (_suspendNotify > 0) return;
        foreach (var n in DerivedNames) OnPropertyChanged(n);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_suspendNotify == 0 && Array.IndexOf(DerivedNames, e.PropertyName) < 0)
            NotifyDerived();
    }

    /// <summary>Copy all values from a freshly parsed instance. Returns true when anything changed.
    /// Derived-property notifications are raised once, only if needed.</summary>
    public bool UpdateFrom(VmInfo o)
    {
        _suspendNotify++;
        var changed = false;
        try
        {
            changed |= Set(Name, o.Name, v => Name = v);
            changed |= Set(Id, o.Id, v => Id = v);
            changed |= Set(State, o.State, v => State = v);
            changed |= Set(ProcessorCount, o.ProcessorCount, v => ProcessorCount = v);
            changed |= Set(MemoryStartupBytes, o.MemoryStartupBytes, v => MemoryStartupBytes = v);
            changed |= Set(MemoryAssignedBytes, o.MemoryAssignedBytes, v => MemoryAssignedBytes = v);
            changed |= Set(DynamicMemory, o.DynamicMemory, v => DynamicMemory = v);
            changed |= Set(CpuUsage, o.CpuUsage, v => CpuUsage = v);
            changed |= Set(UptimeSeconds, o.UptimeSeconds, v => UptimeSeconds = v);
            changed |= Set(Generation, o.Generation, v => Generation = v);
            changed |= Set(Version, o.Version, v => Version = v);
            changed |= Set(Path, o.Path, v => Path = v);
            changed |= Set(DvdPath, o.DvdPath, v => DvdPath = v);
            changed |= Set(Notes, o.Notes, v => Notes = v);
            changed |= Set(CreationTime, o.CreationTime, v => CreationTime = v);
            changed |= Set(SwitchName, o.SwitchName, v => SwitchName = v);
            changed |= Set(GuestIp, o.GuestIp, v => GuestIp = v);
            changed |= Set(DiskSizeBytes, o.DiskSizeBytes, v => DiskSizeBytes = v);
            changed |= Set(DiskFileBytes, o.DiskFileBytes, v => DiskFileBytes = v);
            changed |= Set(VhdPath, o.VhdPath, v => VhdPath = v);
            changed |= Set(CheckpointCount, o.CheckpointCount, v => CheckpointCount = v);
            changed |= Set(SecureBoot, o.SecureBoot, v => SecureBoot = v);
            changed |= Set(Os, o.Os, v => Os = v);
        }
        finally
        {
            _suspendNotify--;
        }
        if (changed) NotifyDerived();
        return changed;
    }

    private static bool Set<T>(T current, T value, Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        apply(value);
        return true;
    }
}
