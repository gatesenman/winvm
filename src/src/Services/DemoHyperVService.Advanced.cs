using WinVM.Models;

namespace WinVM.Services;

/// <summary>1.5 demo: advanced settings and virtual switches (in memory, same validation as the real service).</summary>
public sealed partial class DemoHyperVService
{
    private sealed class DemoAdv
    {
        public string Template = SecureBootTemplates.Windows;
        public bool Tpm;
        public long Min = 512L * 1024 * 1024, Max = 1024L * 1024 * 1024 * 1024;
        public int Buffer = 20;
        public List<BootEntry> Boot = new() { new(0, BootDeviceKind.Dvd, ""), new(1, BootDeviceKind.Disk, ""), new(2, BootDeviceKind.Network, "") };
        public bool Nested;
        public string StartAction = AutoStartActions.StartIfRunning, StopAction = AutoStopActions.Save;
        public int StartDelay;
        public bool HvSocket;
    }

    private sealed class DemoSwitch
    {
        public string Id = Guid.NewGuid().ToString();
        public string Name = "";
        public SwitchKind Kind;
        public string Adapter = "";
        public bool AllowMgmt = true;
    }

    private readonly Dictionary<string, DemoAdv> _adv = new();
    private readonly List<DemoSwitch> _switches = new()
    {
        new() { Id = VSwitchInfo.DefaultSwitchId, Name = "Default Switch", Kind = SwitchKind.Internal },
        new() { Name = "外部网络", Kind = SwitchKind.External, Adapter = "Intel(R) Ethernet Connection (17) I219-LM" },
    };

    /// <summary>Simulates a host without local key protector support (TPM cannot be enabled).</summary>
    public bool TpmUnsupported { get; set; }

    private DemoAdv Adv(VmInfo vm)
    {
        if (!_adv.TryGetValue(vm.Id, out var a))
        {
            a = new DemoAdv { Tpm = vm.Os == OsKind.Windows && vm.Generation == 2, Template = vm.Os == OsKind.Windows ? SecureBootTemplates.Windows : SecureBootTemplates.UefiCa };
            if (string.IsNullOrEmpty(vm.DvdPath)) a.Boot = new() { new(0, BootDeviceKind.Disk, Path.GetFileName(vm.VhdPath)), new(1, BootDeviceKind.Network, vm.SwitchName) };
            else a.Boot = new() { new(0, BootDeviceKind.Dvd, Path.GetFileName(vm.DvdPath)), new(1, BootDeviceKind.Disk, Path.GetFileName(vm.VhdPath)), new(2, BootDeviceKind.Network, vm.SwitchName) };
            _adv[vm.Id] = a;
        }
        return a;
    }

    private VmAdvancedSettings Snapshot(VmInfo vm)
    {
        var a = Adv(vm);
        return new VmAdvancedSettings
        {
            Generation = vm.Generation, State = vm.State, SecureBoot = vm.SecureBoot, SecureBootTemplate = a.Template,
            TpmEnabled = a.Tpm, DynamicMemory = vm.DynamicMemory, StartupBytes = vm.MemoryStartupBytes,
            MinimumBytes = a.Min, MaximumBytes = a.Max, MemoryBuffer = a.Buffer,
            BootOrder = vm.Generation == 2 ? a.Boot.Select((b, i) => b with { Index = i }).ToList() : Array.Empty<BootEntry>(),
            NestedVirtualization = a.Nested, AutoStartAction = a.StartAction, AutoStartDelaySeconds = a.StartDelay, AutoStopAction = a.StopAction,
            Notes = OsDetector.UserNotes(vm.Notes), EnhancedSessionHvSocket = a.HvSocket, HostEnhancedSessionEnabled = true
        };
    }

    public async Task<VmAdvancedSettings> GetAdvancedSettingsAsync(string vm, CancellationToken ct = default)
    {
        await Delay();
        lock (_gate) return Snapshot(Find(vm));
    }

    public async Task ApplyAdvancedSettingsAsync(string vm, AdvancedSettingsChange ch)
    {
        if (ch.IsEmpty) return;
        await Delay();
        lock (_gate)
        {
            var v = Find(vm);
            var cur = Snapshot(v);
            HyperVService.ValidateAdvanced(cur, ch);
            var a = Adv(v);
            if (ch.Tpm == true && TpmUnsupported)
                throw new HyperVException(HvErrorKind.Unknown, "保存高级设置失败：无法启用虚拟 TPM（主机可能不支持本地密钥保护器）。");
            if (ch.SecureBoot is bool sb) v.SecureBoot = sb;
            if (ch.SecureBootTemplate != null) a.Template = ch.SecureBootTemplate;
            if (ch.Tpm is bool t) a.Tpm = t;
            if (ch.DynamicMemory is bool d) v.DynamicMemory = d;
            if (ch.MinimumBytes is long mn) a.Min = HyperVService.AlignMem(mn);
            if (ch.MaximumBytes is long mx) a.Max = HyperVService.AlignMem(mx);
            if (ch.MemoryBuffer is int b) a.Buffer = b;
            if (ch.BootOrder != null) a.Boot = ch.BootOrder.Select(i => cur.BootOrder[i]).ToList();
            if (ch.NestedVirtualization is bool n) a.Nested = n;
            if (ch.AutoStartAction != null) a.StartAction = ch.AutoStartAction;
            if (ch.AutoStartDelaySeconds is int dl) a.StartDelay = dl;
            if (ch.AutoStopAction != null) a.StopAction = ch.AutoStopAction;
            if (ch.EnhancedSessionHvSocket is bool hv) a.HvSocket = hv;
            if (ch.Notes != null) v.Notes = OsDetector.JoinNotes(v.Notes, ch.Notes);
        }
    }

    private VSwitchInfo Info(DemoSwitch s) => new()
    {
        Id = s.Id, Name = s.Name, Kind = s.Kind, AdapterDescription = s.Adapter, AllowManagementOs = s.AllowMgmt,
        ConnectedVms = _vms.Where(v => v.SwitchName == s.Name).Select(v => v.Name).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList()
    };

    public async Task<IReadOnlyList<VSwitchInfo>> ListSwitchesDetailedAsync(CancellationToken ct = default)
    {
        await Delay();
        lock (_gate) return _switches.Select(Info).OrderBy(x => x.IsDefaultSwitch ? 0 : 1).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<IReadOnlyList<NetAdapterInfo>> ListPhysicalAdaptersAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<NetAdapterInfo>>(new List<NetAdapterInfo>
            {
                new() { Name = "以太网", Description = "Intel(R) Ethernet Connection (17) I219-LM", Status = "Up", BoundToSwitch = _switches.Any(s => s.Adapter.StartsWith("Intel")) },
                new() { Name = "WLAN", Description = "Intel(R) Wi-Fi 6E AX211 160MHz", Status = "Up", BoundToSwitch = _switches.Any(s => s.Adapter.StartsWith("Intel(R) Wi-Fi")) },
            });
    }

    private DemoSwitch FindSwitch(string id, string action)
    {
        var s = _switches.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new HyperVException(HvErrorKind.NotFound, $"{action}失败：找不到该虚拟交换机，请刷新列表。");
        if (s.Id == VSwitchInfo.DefaultSwitchId)
            throw new HyperVException(HvErrorKind.Validation, $"{action}失败：「Default Switch」由 Windows 管理，无法重命名或删除。");
        return s;
    }

    public async Task CreateSwitchAsync(string name, SwitchKind kind, string? adapterName = null, bool allowManagementOs = true)
    {
        var n = HyperVService.ValidateSwitchName(name);
        if (kind == SwitchKind.External && string.IsNullOrWhiteSpace(adapterName)) throw new ArgumentException("外部交换机需要选择一个物理网卡。");
        await Delay();
        lock (_gate)
        {
            if (_switches.Any(x => x.Name == n)) throw new HyperVException(HvErrorKind.Validation, $"创建虚拟交换机失败：已存在名为「{n}」的虚拟交换机。");
            var desc = kind == SwitchKind.External ? (adapterName == "WLAN" ? "Intel(R) Wi-Fi 6E AX211 160MHz" : "Intel(R) Ethernet Connection (17) I219-LM") : "";
            if (kind == SwitchKind.External && _switches.Any(x => x.Adapter == desc))
                throw new HyperVException(HvErrorKind.Validation, "创建虚拟交换机失败：该网卡已被另一个外部交换机使用。");
            _switches.Add(new DemoSwitch { Name = n, Kind = kind, Adapter = desc, AllowMgmt = allowManagementOs });
        }
    }

    public async Task RenameSwitchAsync(string switchId, string newName)
    {
        var n = HyperVService.ValidateSwitchName(newName);
        await Delay();
        lock (_gate)
        {
            var s = FindSwitch(switchId, "重命名虚拟交换机");
            if (s.Name == n) return;
            if (_switches.Any(x => x.Name == n)) throw new HyperVException(HvErrorKind.Validation, $"重命名虚拟交换机失败：已存在名为「{n}」的虚拟交换机。");
            foreach (var v in _vms.Where(v => v.SwitchName == s.Name)) v.SwitchName = n;
            s.Name = n;
        }
    }

    public async Task DeleteSwitchAsync(string switchId)
    {
        await Delay();
        lock (_gate)
        {
            var s = FindSwitch(switchId, "删除虚拟交换机");
            var users = _vms.Where(v => v.SwitchName == s.Name).Select(v => v.Name).ToList();
            if (users.Count > 0)
                throw new HyperVException(HvErrorKind.Validation, $"删除虚拟交换机失败：交换机「{s.Name}」正被以下虚拟机使用：{string.Join("、", users)}。请先为它们更换或断开网络。");
            _switches.Remove(s);
        }
    }

    public async Task SetVmSwitchAsync(string vm, string? switchName)
    {
        await Delay();
        lock (_gate)
        {
            var v = Find(vm);
            if (!string.IsNullOrEmpty(switchName) && !_switches.Any(x => x.Name == switchName))
                throw new HyperVException(HvErrorKind.Validation, $"更换虚拟交换机失败：找不到虚拟交换机「{switchName}」。");
            v.SwitchName = switchName ?? "";
        }
    }
}
