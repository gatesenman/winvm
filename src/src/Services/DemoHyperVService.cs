using WinVM.Models;

namespace WinVM.Services;

/// <summary>In-memory Hyper-V used by <c>--demo</c> (try the UI without Hyper-V), headless screenshot
/// rendering and the self-test harness validation. Behaves like the real service, including errors.</summary>
public sealed partial class DemoHyperVService : IHyperVService
{
    private readonly object _gate = new();
    private readonly List<VmInfo> _vms = new();
    private readonly Dictionary<string, List<CheckpointInfo>> _cps = new();
    private readonly Dictionary<string, string> _current = new();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _delay;
    private readonly HyperVStatus? _forcedStatus;

    public string StoreRoot { get; } = @"C:\Users\Demo\WinVM\Machines";
    /// <summary>Vary CPU/memory of running VMs on every list call (demo/screenshots).</summary>
    public bool SimulateLoad { get; set; } = true;
    public IReadOnlySet<string> Files => _files;

    public DemoHyperVService(bool seed = true, TimeSpan? delay = null, HyperVStatus? forcedStatus = null)
    {
        _delay = delay ?? TimeSpan.FromMilliseconds(250);
        _forcedStatus = forcedStatus;
        if (!seed) return;
        var now = new DateTime(2026, 10, 19, 10, 30, 0);
        Add("Ubuntu 24.04 LTS", VmState.Running, 4, 8, 128, OsKind.Linux, "Default Switch", @"D:\ISO\ubuntu-24.04.1-desktop-amd64.iso", 1, now.AddDays(-12), 5 * 3600 + 17 * 60, 7);
        Add("Windows 11 开发机", VmState.Off, 6, 16, 256, OsKind.Windows, "Default Switch", @"D:\ISO\Win11_24H2_Chinese_Simplified_x64.iso", 2, now.AddDays(-30), 0, 0, secureBoot: true);
        Add("Debian 12 服务器", VmState.Paused, 2, 2, 40, OsKind.Linux, "外部网络", null, 0, now.AddDays(-3), 3600 * 26, 0);
        Add("Fedora 41 Workstation", VmState.Saved, 4, 6, 80, OsKind.Linux, "Default Switch", null, 0, now.AddDays(-7), 0, 0);
        Add("Windows Server 2022", VmState.Off, 4, 8, 127, OsKind.Windows, "", null, 0, now.AddDays(-60), 0, 0, secureBoot: true);
        var fresh = Guid.NewGuid().ToString();
        var driver = Guid.NewGuid().ToString();
        var vs = Guid.NewGuid().ToString();
        _cps[_vms[1].Id] = new List<CheckpointInfo>
        {
            new() { Name = "全新安装", Id = fresh, SnapshotType = "Standard", CreationTime = now.AddDays(-29) },
            new() { Name = "测试驱动签名（分支）", Id = driver, ParentId = fresh, SnapshotType = "Standard", CreationTime = now.AddDays(-20) },
            new() { Name = "安装 Visual Studio 之后", Id = vs, ParentId = fresh, SnapshotType = "Standard", CreationTime = now.AddDays(-2).AddHours(-3) },
            new() { Name = "配置 WSL 与 Docker", Id = Guid.NewGuid().ToString(), ParentId = vs, SnapshotType = "Production", CreationTime = now.AddDays(-1) }
        };
        _current[_vms[1].Id] = vs;
        var ub = Guid.NewGuid().ToString();
        _cps[_vms[0].Id] = new List<CheckpointInfo>
        {
            new() { Name = "更新到 24.04.1 前", Id = ub, SnapshotType = "Standard", CreationTime = now.AddDays(-5) }
        };
        _current[_vms[0].Id] = ub;
        _vms[1].CheckpointCount = 4;
    }

    private void Add(string name, VmState state, int cpu, int memGiB, int diskGiB, OsKind os, string sw, string? iso,
        int cps, DateTime created, long uptime, int cpuUsage, bool secureBoot = false)
    {
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(StoreRoot, name);
        var vhd = Path.Combine(dir, name + ".vhdx");
        _files.Add(vhd);
        _vms.Add(new VmInfo
        {
            Name = name, Id = id, State = state, ProcessorCount = cpu,
            MemoryStartupBytes = memGiB * 1024L * 1024 * 1024,
            MemoryAssignedBytes = state is VmState.Running or VmState.Paused ? memGiB * 1024L * 1024 * 1024 : 0,
            CpuUsage = state == VmState.Running ? cpuUsage : 0, UptimeSeconds = uptime, Generation = 2, Version = "12.0",
            Path = dir, DvdPath = iso, Notes = OsDetector.NotesTag(os), CreationTime = created, SwitchName = sw,
            DiskSizeBytes = diskGiB * 1024L * 1024 * 1024, DiskFileBytes = diskGiB * 1024L * 1024 * 1024 / 3,
            VhdPath = vhd, CheckpointCount = cps, SecureBoot = secureBoot, Os = os
        });
    }

    private Task Delay() => _delay > TimeSpan.Zero ? Task.Delay(_delay) : Task.CompletedTask;

    private VmInfo Find(string key)
    {
        var vm = _vms.FirstOrDefault(v => v.Id == key) ?? _vms.FirstOrDefault(v => v.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        return vm ?? throw new HyperVException(HvErrorKind.NotFound, $"找不到虚拟机「{key}」，请刷新列表。");
    }

    private static VmInfo Clone(VmInfo v)
    {
        var c = new VmInfo();
        c.UpdateFrom(v);
        return c;
    }

    private static void Require(bool ok, string action)
    {
        if (!ok) throw new HyperVException(HvErrorKind.InvalidState, $"{action}失败：虚拟机当前状态不允许此操作。请刷新后重试。");
    }

    public Task<HyperVStatus> CheckHyperVAsync(CancellationToken ct = default) =>
        Task.FromResult(_forcedStatus ?? new HyperVStatus(true, Array.Empty<string>(), "Hyper-V 可用（演示模式）", true, true));

    public async Task<IReadOnlyList<VmInfo>> ListVmsAsync(CancellationToken ct = default)
    {
        await Delay();
        if (_forcedStatus is { Available: false })
            throw new HyperVException(HvErrorKind.AccessDenied, "读取虚拟机列表失败：没有权限访问 Hyper-V。");
        lock (_gate)
        {
            foreach (var v in _vms.Where(v => v.State == VmState.Running)) v.UptimeSeconds += 4;
            if (SimulateLoad) Jitter();
            return _vms.Select(Clone).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public Task<IReadOnlyList<string>> ListSwitchesAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<string>>(_switches.Select(x => x.Name).ToList());
    }

    public Task<bool> VmNameExistsAsync(string name, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_vms.Any(v => v.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public async Task CreateVmAsync(CreateVmRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        HyperVService.ValidateCreateRequest(req, checkIsoExists: false);
        progress?.Report("正在创建虚拟磁盘与虚拟机…");
        await Delay();
        lock (_gate)
        {
            if (_vms.Any(v => v.Name.Equals(req.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new HyperVException(HvErrorKind.Validation, $"创建虚拟机失败：已存在同名虚拟机「{req.Name.Trim()}」。");
            if (req.RequireTpm && TpmUnsupported)
                throw new HyperVException(HvErrorKind.Unknown, "创建虚拟机失败：无法为 Windows 11 启用虚拟 TPM（主机不支持本地密钥保护器）。");
            if (!string.IsNullOrEmpty(req.SwitchName) && !_switches.Any(x => x.Name == req.SwitchName))
                throw new HyperVException(HvErrorKind.Validation, $"创建虚拟机失败：找不到虚拟交换机「{req.SwitchName}」。");
            Add(req.Name.Trim(), VmState.Off, req.ProcessorCount, 1, req.DiskGiB, req.Os, req.SwitchName ?? "",
                string.IsNullOrEmpty(req.IsoPath) ? null : req.IsoPath, 0, DateTime.Now, 0, 0, req.Os == OsKind.Windows);
            _vms[^1].MemoryStartupBytes = req.MemoryMiB / 2 * 2 * 1024L * 1024;
            _vms[^1].DiskFileBytes = 4 * 1024 * 1024;
            if (req.Generation == 1) _vms[^1].Generation = 1;
            _adv[_vms[^1].Id] = new DemoAdv { Tpm = req.Os == OsKind.Windows && req.Generation == 2,
                Template = req.Os == OsKind.Windows ? SecureBootTemplates.Windows : SecureBootTemplates.UefiCa };
            if (req.Generation == 2 && req.Os == OsKind.Linux) _vms[^1].SecureBoot = false;
        }
    }

    private async Task Mutate(string key, string action, Func<VmInfo, bool> guard, Action<VmInfo> apply)
    {
        await Delay();
        lock (_gate)
        {
            var vm = Find(key);
            Require(guard(vm), action);
            apply(vm);
        }
    }

    /// <summary>Test hook: when set, StartAsync throws this (simulates a host that cannot power on VMs).</summary>
    public HyperVException? StartFailure { get; set; }

    public async Task StartAsync(string name)
    {
        if (StartFailure is { } f)
        {
            lock (_gate) Find(name); // unknown VM still reports "not found"
            throw f;
        }
        await Mutate(name, "启动", v => v.CanStart, v => { v.State = VmState.Running; v.UptimeSeconds = 1; v.CpuUsage = 3; v.MemoryAssignedBytes = v.MemoryStartupBytes; });
    }
    public Task StopAsync(string name) => Mutate(name, "关机", v => v.CanStop, v =>
    {
        if (v.Name.StartsWith("WinVM-E2E", StringComparison.Ordinal))
            throw new HyperVException(HvErrorKind.GuestNoResponse, "关机失败：来宾操作系统未响应（集成服务不可用）。可改用「强制关闭」或「重置」。");
        Off(v);
    });
    public Task TurnOffAsync(string name) => Mutate(name, "强制关闭", v => v.CanTurnOff, Off);
    public Task PauseAsync(string name) => Mutate(name, "暂停", v => v.CanPause, v => v.State = VmState.Paused);
    public Task ResumeAsync(string name) => Mutate(name, "恢复", v => v.CanResume, v => v.State = VmState.Running);
    public Task RestartAsync(string name) => Mutate(name, "重启", v => v.CanRestart, v =>
    {
        if (v.Name.StartsWith("WinVM-E2E", StringComparison.Ordinal))
            throw new HyperVException(HvErrorKind.GuestNoResponse, "重启失败：来宾操作系统未响应（集成服务不可用）。可改用「强制关闭」或「重置」。");
        v.UptimeSeconds = 1;
    });
    public Task ResetAsync(string name) => Mutate(name, "重置", v => v.CanReset, v => { v.State = VmState.Running; v.UptimeSeconds = 1; });

    private static void Off(VmInfo v)
    {
        v.State = VmState.Off; v.UptimeSeconds = 0; v.CpuUsage = 0; v.MemoryAssignedBytes = 0;
    }

    public async Task DeleteAsync(string name, bool deleteFiles)
    {
        await Delay();
        lock (_gate)
        {
            var vm = Find(name);
            _vms.Remove(vm);
            _cps.Remove(vm.Id);
            _current.Remove(vm.Id);
            _adv.Remove(vm.Id);
            if (deleteFiles) _files.Remove(vm.VhdPath);
        }
    }

    public Task UpdateSettingsAsync(string name, int? cpu, long? memoryBytes, string? isoPath, string? switchName = null) =>
        Mutate(name, "保存设置", v => v.State == VmState.Off, v =>
        {
            if (cpu is int c) v.ProcessorCount = c;
            if (memoryBytes is long m) v.MemoryStartupBytes = m;
            if (isoPath != null) v.DvdPath = isoPath.Length == 0 ? null : isoPath;
            if (switchName != null) v.SwitchName = switchName;
        });

    public async Task RenameAsync(string oldName, string newName)
    {
        await Delay();
        lock (_gate)
        {
            var vm = Find(oldName);
            newName = newName.Trim();
            if (newName.Length == 0) throw new ArgumentException("请填写新名称。");
            if (_vms.Any(v => v != vm && v.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                throw new HyperVException(HvErrorKind.Validation, $"重命名失败：已存在同名虚拟机「{newName}」。");
            vm.Name = newName;
        }
    }

    public Task ResizeVhdAsync(string name, int newSizeGiB) =>
        Mutate(name, "扩容磁盘", v => v.State == VmState.Off, v =>
        {
            if (v.CheckpointCount > 0)
                throw new HyperVException(HvErrorKind.Validation, "扩容磁盘失败：存在检查点时无法扩容磁盘，请先删除检查点。");
            var nb = newSizeGiB * 1024L * 1024 * 1024;
            if (nb <= v.DiskSizeBytes)
                throw new HyperVException(HvErrorKind.Validation, $"扩容磁盘失败：新容量必须大于当前容量（当前 {v.DiskSizeBytes / 1073741824.0:0.##} GiB）。");
            v.DiskSizeBytes = nb;
        });

    public async Task<IReadOnlyList<CheckpointInfo>> ListCheckpointsAsync(string vmName, CancellationToken ct = default)
    {
        await Delay();
        lock (_gate)
        {
            var vm = Find(vmName);
            if (!_cps.TryGetValue(vm.Id, out var l)) return new List<CheckpointInfo>();
            _current.TryGetValue(vm.Id, out var cur);
            return l.OrderByDescending(c => c.CreationTime).Select(c => new CheckpointInfo
            {
                Name = c.Name, Id = c.Id, SnapshotType = c.SnapshotType, CreationTime = c.CreationTime, ParentId = c.ParentId,
                ParentCheckpointName = l.FirstOrDefault(p => p.Id == c.ParentId)?.Name ?? "", IsCurrent = c.Id == cur
            }).ToList();
        }
    }

    public async Task<string> CreateCheckpointAsync(string vmName, string? checkpointName)
    {
        await Delay();
        lock (_gate)
        {
            var vm = Find(vmName);
            Require(vm.CanCreateCheckpoint, "创建检查点");
            if (!_cps.TryGetValue(vm.Id, out var l)) _cps[vm.Id] = l = new List<CheckpointInfo>();
            var cp = new CheckpointInfo
            {
                Name = string.IsNullOrWhiteSpace(checkpointName) ? $"{vm.Name} - ({DateTime.Now:yyyy/MM/dd - HH:mm:ss})" : checkpointName.Trim(),
                Id = Guid.NewGuid().ToString(), SnapshotType = "Standard", CreationTime = DateTime.Now,
                ParentId = _current.TryGetValue(vm.Id, out var parent) ? parent : ""
            };
            l.Add(cp);
            _current[vm.Id] = cp.Id;
            vm.CheckpointCount = l.Count;
            return cp.Id;
        }
    }

    public async Task ApplyCheckpointAsync(string vmName, string checkpointId)
    {
        await Delay();
        lock (_gate)
        {
            var vm = Find(vmName);
            if (!_cps.TryGetValue(vm.Id, out var l) || l.All(c => c.Id != checkpointId))
                throw new HyperVException(HvErrorKind.Validation, "应用检查点失败：找不到该检查点，请刷新后重试。");
            _current[vm.Id] = checkpointId;
            Off(vm);
        }
    }

    public async Task DeleteCheckpointAsync(string vmName, string checkpointId)
    {
        await Delay();
        lock (_gate)
        {
            var vm = Find(vmName);
            var gone = _cps.TryGetValue(vm.Id, out var l) ? l.FirstOrDefault(c => c.Id == checkpointId) : null;
            if (l == null || gone == null)
                throw new HyperVException(HvErrorKind.Validation, "删除检查点失败：找不到该检查点，请刷新后重试。");
            l.Remove(gone);
            // like Hyper-V: children move up to the deleted checkpoint's parent (its changes are merged)
            for (var i = 0; i < l.Count; i++)
                if (l[i].ParentId == gone.Id)
                    l[i] = new CheckpointInfo { Name = l[i].Name, Id = l[i].Id, SnapshotType = l[i].SnapshotType, CreationTime = l[i].CreationTime, ParentId = gone.ParentId };
            if (_current.TryGetValue(vm.Id, out var cur) && cur == gone.Id)
            {
                if (string.IsNullOrEmpty(gone.ParentId)) _current.Remove(vm.Id); else _current[vm.Id] = gone.ParentId;
            }
            vm.CheckpointCount = l.Count;
        }
    }

    public Task OpenConsoleAsync(string name) => Task.CompletedTask;
    public Task OpenRdpAsync(string host, int port = 3389) => Task.CompletedTask;
    public Task OpenSshAsync(string host, string? user = null, int port = 22) => Task.CompletedTask;

    // ---------------------------------------------------------------- export / import / clone (simulated)

    /// <summary>Simulated duration of export/import/clone (progress is reported in 20 steps).</summary>
    public TimeSpan LongOpDuration { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Folders produced by <see cref="ExportVmAsync"/> (path → source VM name).</summary>
    public Dictionary<string, string> Exports { get; } = new(StringComparer.OrdinalIgnoreCase);

    private async Task Simulate(IProgress<OpProgress>? progress, CancellationToken ct, params (double Start, double Span, string Stage)[] phases)
    {
        var step = TimeSpan.FromTicks(Math.Max(1, LongOpDuration.Ticks / (20 * Math.Max(1, phases.Length))));
        foreach (var (start, span, stage) in phases)
        {
            for (var i = 0; i <= 20; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new OpProgress(start + span * i / 20.0, stage));
                if (i < 20 && step > TimeSpan.Zero) await Task.Delay(step, ct);
            }
        }
    }

    public async Task<string> ExportVmAsync(string vm, string parentFolder, IProgress<OpProgress>? progress = null, CancellationToken ct = default)
    {
        string name;
        lock (_gate)
        {
            var v = Find(vm);
            if (v.State != VmState.Off) throw new HyperVException(HvErrorKind.Validation, "导出虚拟机失败：请先关闭虚拟机再导出。");
            name = v.Name;
        }
        var target = Path.Combine(parentFolder, name);
        if (Exports.ContainsKey(target)) throw new HyperVException(HvErrorKind.Validation, $"导出虚拟机失败：导出位置已存在同名文件夹：{target}");
        try { await Simulate(progress, ct, (0, 1, "正在导出…")); }
        catch (OperationCanceledException) { throw new OperationCanceledException("导出虚拟机已取消。"); }
        Exports[target] = name;
        return target;
    }

    private string AddCopy(VmInfo src, string newName)
    {
        if (_vms.Any(v => v.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            throw new HyperVException(HvErrorKind.Validation, $"已存在同名虚拟机「{newName}」。");
        var c = Clone(src);
        c.Id = Guid.NewGuid().ToString();
        c.Name = newName;
        c.State = VmState.Off; c.UptimeSeconds = 0; c.CpuUsage = 0; c.MemoryAssignedBytes = 0;
        c.CreationTime = DateTime.Now;
        c.Path = HyperVService.UniqueFolder(StoreRoot, newName, p => _vms.Any(v => v.Path.Equals(p, StringComparison.OrdinalIgnoreCase)));
        c.VhdPath = Path.Combine(c.Path, "Virtual Hard Disks", Path.GetFileName(src.VhdPath));
        c.CheckpointCount = 0;
        _files.Add(c.VhdPath);
        _vms.Add(c);
        return c.Id;
    }

    public async Task<string> ImportVmAsync(string sourceFolder, string newName, IProgress<OpProgress>? progress = null, CancellationToken ct = default)
    {
        newName = HyperVService.ValidateNewName(newName);
        string? srcName;
        lock (_gate)
        {
            if (!Exports.TryGetValue(sourceFolder.TrimEnd('\\', '/'), out srcName))
                throw new HyperVException(HvErrorKind.Validation, "导入虚拟机失败：所选文件夹中没有找到导出的虚拟机（缺少 Virtual Machines\\*.vmcx）。请选择导出生成的虚拟机文件夹。");
            if (_vms.Any(v => v.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                throw new HyperVException(HvErrorKind.Validation, $"导入虚拟机失败：已存在同名虚拟机「{newName}」。");
        }
        try { await Simulate(progress, ct, (0, 1, "正在复制虚拟机文件…")); }
        catch (OperationCanceledException) { throw new OperationCanceledException("导入虚拟机已取消。"); }
        lock (_gate)
        {
            var src = _vms.FirstOrDefault(v => v.Name == srcName) ?? new VmInfo { Name = srcName!, ProcessorCount = 2, MemoryStartupBytes = 2L << 30, Generation = 2, Os = OsKind.Linux, VhdPath = srcName + ".vhdx" };
            try { return AddCopy(src, newName); }
            catch (HyperVException e) { throw new HyperVException(e.Kind, "导入虚拟机失败：" + e.Message); }
        }
    }

    public async Task<string> CloneVmAsync(string vm, string newName, IProgress<OpProgress>? progress = null, CancellationToken ct = default)
    {
        newName = HyperVService.ValidateNewName(newName);
        lock (_gate)
        {
            var v = Find(vm);
            if (v.State != VmState.Off) throw new HyperVException(HvErrorKind.Validation, "克隆虚拟机失败：请先关闭虚拟机再克隆。");
            if (_vms.Any(x => x.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                throw new HyperVException(HvErrorKind.Validation, $"克隆虚拟机失败：已存在同名虚拟机「{newName}」。");
        }
        try { await Simulate(progress, ct, (0, 0.5, "正在复制源虚拟机（1/2）…"), (0.5, 0.5, "正在生成克隆（2/2）…")); }
        catch (OperationCanceledException) { throw new OperationCanceledException("克隆虚拟机已取消。"); }
        lock (_gate) return AddCopy(Find(vm), newName);
    }

    /// <summary>Demo performance: running VMs get a gently varying CPU load and memory demand.</summary>
    private readonly Random _rng = new(42);
    private void Jitter()
    {
        foreach (var v in _vms.Where(v => v.State == VmState.Running))
        {
            var target = 8 + 30 * (0.5 + 0.5 * Math.Sin(v.UptimeSeconds / 40.0)) + _rng.Next(0, 12);
            v.CpuUsage = (int)Math.Clamp(target, 0, 100);
            if (v.MemoryStartupBytes > 0)
                v.MemoryAssignedBytes = (long)(v.MemoryStartupBytes * (0.55 + 0.25 * (0.5 + 0.5 * Math.Sin(v.UptimeSeconds / 90.0))));
        }
    }
}
