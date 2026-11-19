namespace WinVM.Models;

public enum BootDeviceKind { Disk, Dvd, Network, File, Other }

/// <summary>One firmware boot entry. <see cref="Index"/> is its position in the order read from Hyper-V
/// (the apply script checks the list did not change meanwhile).</summary>
public sealed record BootEntry(int Index, BootDeviceKind Kind, string Description)
{
    public string KindText => Kind switch
    {
        BootDeviceKind.Disk => "硬盘",
        BootDeviceKind.Dvd => "DVD 驱动器",
        BootDeviceKind.Network => "网络适配器（PXE）",
        BootDeviceKind.File => "启动文件",
        _ => "其他"
    };
    public string Display => string.IsNullOrWhiteSpace(Description) ? KindText : $"{KindText} · {Description}";
}

public static class AutoStartActions
{
    public const string Nothing = "Nothing", StartIfRunning = "StartIfRunning", Start = "Start";
    public static readonly string[] All = { Nothing, StartIfRunning, Start };
    public static readonly string[] Names = { "不执行任何操作", "如果服务停止时正在运行，则自动启动", "始终自动启动" };
}

public static class AutoStopActions
{
    public const string TurnOff = "TurnOff", Save = "Save", ShutDown = "ShutDown";
    public static readonly string[] All = { Save, TurnOff, ShutDown };
    public static readonly string[] Names = { "保存虚拟机状态", "关闭虚拟机（断电）", "关闭来宾操作系统" };
}

public static class SecureBootTemplates
{
    public const string Windows = "MicrosoftWindows", UefiCa = "MicrosoftUEFICertificateAuthority", Shielded = "OpenSourceShieldedVM";
    public static readonly string[] All = { Windows, UefiCa, Shielded };
    public static readonly string[] Names = { "Microsoft Windows", "Microsoft UEFI 证书颁发机构（大多数 Linux）", "开源屏蔽虚拟机" };
}

/// <summary>Advanced configuration of one VM as read from Hyper-V.</summary>
public sealed record VmAdvancedSettings
{
    public int Generation { get; init; } = 2;
    public VmState State { get; init; } = VmState.Off;
    public bool SecureBoot { get; init; }
    public string SecureBootTemplate { get; init; } = SecureBootTemplates.Windows;
    public bool TpmEnabled { get; init; }
    public bool DynamicMemory { get; init; }
    public long StartupBytes { get; init; }
    public long MinimumBytes { get; init; }
    public long MaximumBytes { get; init; }
    public int MemoryBuffer { get; init; } = 20;
    public IReadOnlyList<BootEntry> BootOrder { get; init; } = Array.Empty<BootEntry>();
    public bool NestedVirtualization { get; init; }
    public string AutoStartAction { get; init; } = AutoStartActions.StartIfRunning;
    public int AutoStartDelaySeconds { get; init; }
    public string AutoStopAction { get; init; } = AutoStopActions.Save;
    /// <summary>User notes (WinVM's own "[WinVM os=…]" tag is stripped and preserved separately).</summary>
    public string Notes { get; init; } = "";
    /// <summary>Per-VM enhanced session transport is HvSocket (needed for Linux xrdp enhanced session).</summary>
    public bool EnhancedSessionHvSocket { get; init; }
    /// <summary>Host policy (read-only here; WinVM never changes host settings).</summary>
    public bool HostEnhancedSessionEnabled { get; init; }

    public bool IsGen2 => Generation == 2;
    public bool IsOff => State == VmState.Off;
}

/// <summary>Only the fields to change (null = unchanged).</summary>
public sealed record AdvancedSettingsChange
{
    public bool? SecureBoot { get; init; }
    public string? SecureBootTemplate { get; init; }
    public bool? Tpm { get; init; }
    public bool? DynamicMemory { get; init; }
    public long? MinimumBytes { get; init; }
    public long? MaximumBytes { get; init; }
    public int? MemoryBuffer { get; init; }
    /// <summary>New order as <see cref="BootEntry.Index"/> values.</summary>
    public IReadOnlyList<int>? BootOrder { get; init; }
    public bool? NestedVirtualization { get; init; }
    public string? AutoStartAction { get; init; }
    public int? AutoStartDelaySeconds { get; init; }
    public string? AutoStopAction { get; init; }
    public string? Notes { get; init; }
    public bool? EnhancedSessionHvSocket { get; init; }

    /// <summary>Hyper-V only accepts these while the VM is off.</summary>
    public bool RequiresOff => SecureBoot != null || SecureBootTemplate != null || Tpm != null || DynamicMemory != null ||
                               MinimumBytes != null || MaximumBytes != null || MemoryBuffer != null || BootOrder != null ||
                               NestedVirtualization != null || EnhancedSessionHvSocket != null;

    public bool IsEmpty => !RequiresOff && AutoStartAction == null && AutoStartDelaySeconds == null && AutoStopAction == null && Notes == null;
}

public enum SwitchKind { External, Internal, Private }

public sealed class VSwitchInfo
{
    public const string DefaultSwitchId = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public SwitchKind Kind { get; init; }
    public string AdapterDescription { get; init; } = "";
    public bool AllowManagementOs { get; init; }
    public IReadOnlyList<string> ConnectedVms { get; init; } = Array.Empty<string>();

    public bool IsDefaultSwitch => Id.Equals(DefaultSwitchId, StringComparison.OrdinalIgnoreCase);
    public bool InUse => ConnectedVms.Count > 0;
    public bool CanModify => !IsDefaultSwitch;
    public string KindText => IsDefaultSwitch ? "默认（NAT）" : Kind switch
    {
        SwitchKind.External => "外部",
        SwitchKind.Internal => "内部",
        _ => "专用"
    };
    public string KindDescription => IsDefaultSwitch ? "Windows 自动创建的 NAT 网络，虚拟机可访问外网；由 Windows 管理，无法修改或删除。" : Kind switch
    {
        SwitchKind.External => "桥接到物理网卡：虚拟机与主机处于同一局域网。" + (string.IsNullOrEmpty(AdapterDescription) ? "" : $"网卡：{AdapterDescription}"),
        SwitchKind.Internal => "主机与虚拟机之间互通，不直接连接外网。",
        _ => "仅虚拟机之间互通，主机无法访问。"
    };
    public string UsageText => ConnectedVms.Count == 0 ? "未被使用" : $"{ConnectedVms.Count} 台虚拟机：{string.Join("、", ConnectedVms)}";
}

public sealed class NetAdapterInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "";
    /// <summary>Already bound to an external switch (cannot be used for another one).</summary>
    public bool BoundToSwitch { get; init; }
    public string Display => $"{Name} — {Description}{(Status.Equals("Up", StringComparison.OrdinalIgnoreCase) ? "" : $"（{Status}）")}{(BoundToSwitch ? "（已被外部交换机使用）" : "")}";
}
