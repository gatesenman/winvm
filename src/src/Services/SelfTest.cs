using System.Diagnostics;
using System.Text.Json;
using WinVM.Models;

namespace WinVM.Services;

public sealed class SelfTestStep
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "pending"; // pass | fail | skip | env (blocked by a host problem)
    public long Ms { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class SelfTestReport
{
    public string App { get; set; } = "WinVM " + HyperVService.AppVersion;
    public string Mode { get; set; } = "real";
    public string Machine { get; set; } = Environment.MachineName;
    public string User { get; set; } = Environment.UserName;
    public string Os { get; set; } = Environment.OSVersion.VersionString;
    public bool Elevated { get; set; } = Elevation.IsElevated;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime FinishedAt { get; set; }
    public bool HyperVAvailable { get; set; }
    public string HyperVMessage { get; set; } = "";
    public string TestVm { get; set; } = "";
    public bool CleanupVerified { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    /// <summary>Steps that could not run because of a problem with the host itself (not WinVM).</summary>
    public int EnvBlocked { get; set; }
    public List<string> EnvironmentIssues { get; set; } = new();
    public List<SelfTestStep> Steps { get; set; } = new();
}

/// <summary>
/// End-to-end harness (<c>WinVM.exe --selftest</c>): drives the real <see cref="HyperVService"/> with a
/// throw-away VM named WinVM-E2E-&lt;timestamp&gt;. Pre-existing VMs are never touched (all calls use the
/// test VM's Id). The test VM, its VHD and folder are always removed, even on failure.
/// </summary>
public sealed class SelfTest
{
    private readonly IHyperVService _hv;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _dirExists;
    private readonly IPsRunner? _rawPs;
    private readonly SelfTestReport _report = new();
    private readonly Action<string>? _progress;
    private string? _vmId;
    private string _vmName = "";
    /// <summary>Unique per-run name prefix; everything this run creates starts with it (cleanup relies on that).</summary>
    private string _prefix = "";
    private string _exportRoot = "";
    private bool IsReal => _rawPs != null;
    private bool _hvOk;
    private string? _powerBlocked;
    private static readonly HashSet<string> PowerSteps = new() { "cp.online", "vm.pause", "vm.resume", "vm.restart.soft", "vm.reset", "vm.stop.soft" };

    public SelfTest(IHyperVService hv, Func<string, bool>? fileExists = null, Func<string, bool>? dirExists = null,
        IPsRunner? rawPs = null, Action<string>? progress = null, string mode = "real")
    {
        _hv = hv;
        _fileExists = fileExists ?? File.Exists;
        _dirExists = dirExists ?? Directory.Exists;
        _rawPs = rawPs;
        _progress = progress;
        _report.Mode = mode;
    }

    public static string DefaultOutDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVM", "e2e");

    private async Task Step(string name, string title, Func<Task<string>> body, bool needsHv = true)
    {
        var s = new SelfTestStep { Name = name, Title = title };
        _report.Steps.Add(s);
        if (needsHv && !_hvOk)
        {
            s.Status = "skip";
            s.Detail = "Hyper-V 不可用：" + _report.HyperVMessage.Replace("\n", " ");
            _progress?.Invoke($"SKIP {name}");
            return;
        }
        if (_powerBlocked != null && PowerSteps.Contains(name))
        {
            s.Status = "env";
            s.Detail = "未执行：测试虚拟机无法启动（" + _powerBlocked + "）";
            _progress?.Invoke($"ENV {name}");
            return;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            s.Detail = await body();
            s.Status = "pass";
        }
        catch (EnvBlockedException ex)
        {
            s.Status = "env";
            s.Detail = "环境限制：" + ex.Message;
        }
        catch (Exception ex)
        {
            s.Status = "fail";
            s.Detail = ErrorMapper.Friendly(ex) + (ex is HyperVException h && h.Detail.Length > 0 ? " | raw: " + Trunc(h.Detail, 400) : "")
                       + (ex is HyperVException ? "" : " | " + ex.GetType().Name);
        }
        s.Ms = sw.ElapsedMilliseconds;
        _progress?.Invoke($"{s.Status.ToUpperInvariant()} {name} ({s.Ms} ms) {s.Detail}");
        Log.Info($"selftest {s.Status} {name} {s.Ms}ms {s.Detail}");
        if (s.Status == "fail" && name != "cleanup" && LooksLikeVmmsDown(s.Detail))
            await RecoverHyperVAsync(name);
    }

    internal static bool LooksLikeVmmsDown(string detail) =>
        detail.Contains("超时") || PsSession.IsStaleHyperVConnection(detail);

    /// <summary>The Hyper-V service (vmms.exe) crashed or stopped responding during a step: wait (max 6 min) until it
    /// answers again so the remaining steps test WinVM rather than a dead service, and report it as a host issue.</summary>
    private async Task RecoverHyperVAsync(string step)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(6))
        {
            try
            {
                _hv.RequestFresh();
                await _hv.ListVmsAsync();
                var issue = $"Hyper-V 虚拟机管理服务（vmms.exe）在步骤 {step} 期间无响应或崩溃，等待 {sw.Elapsed.TotalSeconds:0} 秒后恢复";
                _report.EnvironmentIssues.Add(issue);
                Log.Warn("selftest: " + issue);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Task.Delay(5000);
            }
        }
        _report.EnvironmentIssues.Add($"Hyper-V 虚拟机管理服务（vmms.exe）在步骤 {step} 后 6 分钟内未恢复");
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    private static void Assert(bool cond, string message)
    {
        if (!cond) throw new InvalidOperationException("断言失败：" + message);
    }

    private async Task<VmInfo?> FindAsync()
    {
        var list = await _hv.ListVmsAsync();
        return list.FirstOrDefault(v => v.Id == _vmId) ?? (_vmId == null ? list.FirstOrDefault(v => v.Name == _vmName) : null);
    }

    private async Task<VmInfo> RequireAsync()
    {
        var vm = await FindAsync();
        return vm ?? throw new InvalidOperationException("测试虚拟机不在列表中");
    }

    private string Ref => _vmId ?? _vmName;

    /// <summary>Pause between checkpoint operations on real Hyper-V (none for the in-memory demo).</summary>
    private TimeSpan Pace => _hv is HyperVService ? TimeSpan.FromSeconds(3) : TimeSpan.Zero;

    private async Task<VmInfo> WaitStateAsync(VmState expected, int seconds = 60)
    {
        var sw = Stopwatch.StartNew();
        VmInfo? vm = null;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            vm = await FindAsync();
            if (vm?.State == expected) return vm;
            await Task.Delay(1000);
        }
        throw new InvalidOperationException($"等待状态 {expected} 超时（当前 {vm?.State.ToString() ?? "不存在"}）");
    }

    private static async Task<string> ExpectErrorAsync(Func<Task> action, params HvErrorKind[] acceptable)
    {
        try
        {
            await action();
        }
        catch (HyperVException ex) when (acceptable.Length == 0 || acceptable.Contains(ex.Kind))
        {
            return $"按预期失败 [{ex.Kind}]：{ex.Message}";
        }
        catch (ArgumentException ex)
        {
            return $"按预期被校验拦截：{ex.Message}";
        }
        throw new InvalidOperationException("预期失败，但操作成功了");
    }

    public async Task<SelfTestReport> RunAsync()
    {
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _vmName = $"WinVM-E2E-{ts}";
        _prefix = _vmName;
        _exportRoot = Path.Combine(Path.GetTempPath(), "WinVM-E2E-export-" + ts);
        _report.TestVm = _vmName;
        string? vhdPath = null, vmFolder = null;

        // ---- environment checks (no Hyper-V needed)
        if (_rawPs != null)
        {
            await Step("ps.roundtrip", "PowerShell 常驻会话往返 + UTF-8 中文", async () =>
            {
                var r = await _rawPs.RunAsync("'中文✓ ' + $PSVersionTable.PSVersion.ToString()", TimeSpan.FromSeconds(60));
                Assert(r.Ok, "执行失败：" + r.Error);
                Assert(r.Output.StartsWith("中文✓ "), "编码不一致：" + r.Output);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 5; i++)
                {
                    var e = await _rawPs.RunAsync($"{i} + 1", TimeSpan.FromSeconds(30));
                    Assert(e.Ok && e.Output.Trim() == (i + 1).ToString(), "回显错误");
                }
                return $"{r.Output.Trim()}；5 次调用平均 {sw.ElapsedMilliseconds / 5} ms（常驻会话）";
            }, needsHv: false);
            await Step("ps.error", "PowerShell 异常捕获与会话自愈", async () =>
            {
                var r = await _rawPs.RunAsync("throw 'WINVM:测试异常'", TimeSpan.FromSeconds(30));
                Assert(!r.Ok, "应返回失败");
                var (kind, msg) = ErrorMapper.Map(r.Error);
                Assert(kind == HvErrorKind.Validation && msg == "测试异常", $"映射错误：{kind} {msg}");
                var after = await _rawPs.RunAsync("'still-alive'", TimeSpan.FromSeconds(30));
                Assert(after.Ok && after.Output.Trim() == "still-alive", "异常后会话不可用");
                return "异常被捕获并映射；会话继续可用";
            }, needsHv: false);
        }

        HyperVStatus status = new(false, Array.Empty<string>(), "", false, false);
        await Step("hyperv.check", "检测 Hyper-V（功能/模块/权限）", async () =>
        {
            status = await _hv.CheckHyperVAsync();
            _hvOk = status.Available;
            _report.HyperVAvailable = status.Available;
            _report.HyperVMessage = status.Message;
            return $"available={status.Available} elevated={status.IsElevated} codes=[{string.Join(",", status.Codes)}] {status.Message.Replace("\n", " ")}";
        }, needsHv: false);

        await Step("error.mapping", "真实错误映射（操作不存在的虚拟机）", () =>
            ExpectErrorAsync(() => _hv.StartAsync($"WinVM-E2E-missing-{ts}"),
                HvErrorKind.Validation, HvErrorKind.NotFound, HvErrorKind.AccessDenied, HvErrorKind.NotWindows), needsHv: false);

        if (!_hvOk)
        {
            await Step("ui.unavailable-page", "Hyper-V 不可用时的提示信息", () =>
            {
                Assert(!string.IsNullOrWhiteSpace(status.Message), "缺少原因说明");
                return Task.FromResult($"needsElevation={status.NeedsElevation}；提示：{status.Message.Replace("\n", " / ")}");
            }, needsHv: false);
        }

        try
        {
            await Step("perf.list", "批量列出虚拟机（单次管道）", async () =>
            {
                var sw = Stopwatch.StartNew();
                var list = await _hv.ListVmsAsync();
                var first = sw.ElapsedMilliseconds;
                sw.Restart();
                await _hv.ListVmsAsync();
                return $"{list.Count} 台现有虚拟机；首次 {first} ms，二次 {sw.ElapsedMilliseconds} ms（不修改任何现有虚拟机）";
            });

            var switches = new List<string>();
            await Step("switch.list", "列出虚拟交换机", async () =>
            {
                switches = (await _hv.ListSwitchesAsync()).ToList();
                return switches.Count == 0 ? "无虚拟交换机" : string.Join(", ", switches);
            });
            var sw0 = switches.Contains("Default Switch") ? "Default Switch" : "";

            await Step("vm.create", "创建测试虚拟机（Gen2 / 1 CPU / 512 MiB / 2 GiB / 无 ISO）", async () =>
            {
                await _hv.CreateVmAsync(new CreateVmRequest
                {
                    Name = _vmName, ProcessorCount = 1, MemoryMiB = 512, DiskGiB = 2, IsoPath = "", Generation = 2,
                    SwitchName = sw0, Os = OsKind.Linux
                });
                var vm = await RequireAsync();
                _vmId = vm.Id;
                vhdPath = vm.VhdPath;
                vmFolder = string.IsNullOrEmpty(vm.VhdPath) ? null : Path.GetDirectoryName(vm.VhdPath);
                return $"Id={vm.Id} VHD={vm.VhdPath} 交换机={(sw0.Length == 0 ? "无" : sw0)}";
            });

            await Step("vm.find", "在列表中找到并校验属性", async () =>
            {
                var vm = await RequireAsync();
                Assert(vm.State == VmState.Off, "状态应为 Off，实际 " + vm.State);
                Assert(vm.Generation == 2, "代数应为 2");
                Assert(vm.ProcessorCount == 1, "CPU 应为 1，实际 " + vm.ProcessorCount);
                Assert(vm.MemoryStartupBytes == 512L * 1024 * 1024, "内存应为 512 MiB，实际 " + vm.MemoryStartupBytes);
                Assert(vm.DiskSizeBytes == 2L * 1024 * 1024 * 1024, "磁盘应为 2 GiB，实际 " + vm.DiskSizeBytes);
                Assert(vm.Os == OsKind.Linux, "系统标签应为 Linux");
                Assert(vm.SwitchName == sw0, $"交换机应为「{sw0}」，实际「{vm.SwitchName}」");
                Assert(vm.CheckpointCount == 0, "不应有检查点（自动检查点应已关闭）");
                Assert(!string.IsNullOrEmpty(vhdPath) && _fileExists(vhdPath!), "VHDX 文件应存在");
                return $"{vm.StateText} · {vm.GenerationText} · {vm.CpuShort} · {vm.MemoryText} · {vm.DiskShort} · 安全启动 {vm.SecureBootText}";
            });

            await Step("vm.create.duplicate", "重名创建应被拒绝", () => ExpectErrorAsync(() => _hv.CreateVmAsync(new CreateVmRequest
            {
                Name = _vmName, ProcessorCount = 1, MemoryMiB = 512, DiskGiB = 1, SwitchName = ""
            }), HvErrorKind.Validation, HvErrorKind.AlreadyExists));

            await Step("vm.create.windows", "创建 Windows 类型虚拟机（安全启动 + vTPM）并删除", async () =>
            {
                var winName = _vmName + "-win";
                await _hv.CreateVmAsync(new CreateVmRequest
                {
                    Name = winName, ProcessorCount = 1, MemoryMiB = 512, DiskGiB = 1, IsoPath = "", Generation = 2,
                    SwitchName = "", Os = OsKind.Windows
                });
                var w = (await _hv.ListVmsAsync()).FirstOrDefault(v => v.Name == winName)
                        ?? throw new InvalidOperationException("Windows 测试虚拟机不在列表中");
                Assert(w.Os == OsKind.Windows, "系统标签应为 Windows");
                Assert(w.SecureBoot, "Windows 虚拟机应启用安全启动");
                var wVhd = w.VhdPath;
                var wDir = string.IsNullOrEmpty(wVhd) ? null : Path.GetDirectoryName(wVhd);
                await _hv.DeleteAsync(w.Id, deleteFiles: true);
                Assert((await _hv.ListVmsAsync()).All(v => v.Id != w.Id), "删除后仍在列表中");
                Assert(string.IsNullOrEmpty(wVhd) || !_fileExists(wVhd), "VHDX 未删除");
                Assert(wDir == null || !_dirExists(wDir), "文件夹未删除");
                return $"{w.GenerationText} · 安全启动 {w.SecureBootText} · 已创建并删除（含文件）";
            });

            await Step("vm.settings", "修改 CPU=2 / 内存=1 GiB", async () =>
            {
                await _hv.UpdateSettingsAsync(Ref, 2, 1024L * 1024 * 1024, null);
                var vm = await RequireAsync();
                Assert(vm.ProcessorCount == 2, "CPU 应为 2，实际 " + vm.ProcessorCount);
                Assert(vm.MemoryStartupBytes == 1024L * 1024 * 1024, "内存应为 1 GiB，实际 " + vm.MemoryStartupBytes);
                return $"{vm.CpuShort} · {vm.MemoryText}";
            });

            await Step("vm.rename", "重命名（含中文）", async () =>
            {
                var newName = _vmName + "-重命名";
                await _hv.RenameAsync(Ref, newName);
                var vm = await RequireAsync();
                Assert(vm.Name == newName, "名称未更新：" + vm.Name);
                _vmName = newName;
                _report.TestVm = newName;
                return "新名称：" + newName;
            });

            await Step("vhd.resize", "扩容 VHD 2 → 3 GiB", async () =>
            {
                await _hv.ResizeVhdAsync(Ref, 3);
                var vm = await RequireAsync();
                Assert(vm.DiskSizeBytes == 3L * 1024 * 1024 * 1024, "容量应为 3 GiB，实际 " + vm.DiskSizeBytes);
                return vm.DiskText;
            });

            await Step("vhd.resize.shrink", "缩小磁盘应被拒绝", () => ExpectErrorAsync(() => _hv.ResizeVhdAsync(Ref, 2),
                HvErrorKind.Validation, HvErrorKind.ResizeTooSmall));

            string? cpId = null;
            await Step("cp.create", "创建检查点（中文名）", async () =>
            {
                cpId = await _hv.CreateCheckpointAsync(Ref, "E2E 检查点 1");
                var vm = await RequireAsync();
                if (vm.CheckpointCount != 1 && _rawPs != null)
                {
                    var diag = await _rawPs.RunAsync($"$v = Get-VM -Id '{vm.Id}'; 'type=' + $v.CheckpointType + ' auto=' + $v.AutomaticCheckpointsEnabled + ' state=' + $v.State; " +
                        $"'snaps=' + ((@(Get-VMSnapshot -VM $v) | ForEach-Object {{ $_.Name + '/' + $_.SnapshotType + '/' + $_.Id }}) -join ',')", TimeSpan.FromSeconds(60));
                    Assert(false, $"检查点数应为 1，实际 {vm.CheckpointCount}（返回 Id={cpId}；诊断：{diag.Output.Replace('\n', ' ')} {diag.Error}）");
                }
                Assert(vm.CheckpointCount == 1, "检查点数应为 1，实际 " + vm.CheckpointCount);
                return "Id=" + cpId;
            });

            await Step("cp.list", "列出检查点", async () =>
            {
                var list = await _hv.ListCheckpointsAsync(Ref);
                Assert(list.Count == 1 && list[0].Name == "E2E 检查点 1", "列表不符：" + string.Join(",", list.Select(c => c.Name)));
                cpId ??= list[0].Id;
                Assert(list[0].Id == cpId, "Id 不一致");
                return $"{list[0].Name} · {list[0].CreationText} · {list[0].TypeText}";
            });

            await Step("vhd.resize.blocked", "有检查点时扩容应被拒绝", () => ExpectErrorAsync(() => _hv.ResizeVhdAsync(Ref, 4),
                HvErrorKind.Validation, HvErrorKind.CheckpointBlocksResize));

            await Step("cp.apply", "应用检查点", async () =>
            {
                Assert(cpId != null, "缺少检查点 Id");
                await _hv.ApplyCheckpointAsync(Ref, cpId!);
                var vm = await RequireAsync();
                return "应用后状态：" + vm.StateText;
            });

            await Step("cp.delete", "删除检查点", async () =>
            {
                Assert(cpId != null, "缺少检查点 Id");
                await _hv.DeleteCheckpointAsync(Ref, cpId!);
                var list = await _hv.ListCheckpointsAsync(Ref);
                Assert(list.Count == 0, "仍有检查点：" + list.Count);
                return "已删除，剩余 0";
            });

            // ---- 1.5: checkpoint tree (parent/child + 「当前位置」)
            // ---- 1.5: advanced settings (only on this run's own test VM, which is off here)
            VmAdvancedSettings? adv0 = null;
            await Step("adv.read", "读取高级设置", async () =>
            {
                adv0 = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(adv0.IsGen2 && adv0.IsOff, $"应为第 2 代且已关机（gen={adv0.Generation} state={adv0.State}）");
                return $"安全启动 {(adv0.SecureBoot ? "开" : "关")}（{adv0.SecureBootTemplate}）· TPM {(adv0.TpmEnabled ? "开" : "关")} · 动态内存 {(adv0.DynamicMemory ? "开" : "关")} · 启动项 {adv0.BootOrder.Count} · 自动 {adv0.AutoStartAction}/{adv0.AutoStopAction}";
            });

            await Step("adv.secureboot", "安全启动开关 + 模板切换", async () =>
            {
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { SecureBoot = true, SecureBootTemplate = SecureBootTemplates.UefiCa });
                var a = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(a.SecureBoot && a.SecureBootTemplate == SecureBootTemplates.UefiCa, $"应为开 + UEFI CA，实际 {a.SecureBoot}/{a.SecureBootTemplate}");
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { SecureBoot = false });
                var b = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(!b.SecureBoot, "关闭安全启动失败");
                return "开（MicrosoftUEFICertificateAuthority）→ 关 ✓";
            });

            await Step("adv.tpm", "启用 / 关闭虚拟 TPM", async () =>
            {
                try
                {
                    await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { Tpm = true });
                }
                catch (HyperVException ex) when (ex.Message.Contains("TPM"))
                {
                    throw new EnvBlockedException("主机无法为虚拟机启用 TPM：" + ex.Message);
                }
                var a = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(a.TpmEnabled, "TPM 未启用");
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { Tpm = false });
                var b = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(!b.TpmEnabled, "TPM 未关闭");
                return "启用（本地密钥保护器）→ 关闭 ✓";
            });

            await Step("adv.dynmem", "动态内存（最小/最大/缓冲区）", async () =>
            {
                const long min = 512L * 1024 * 1024, max = 2048L * 1024 * 1024;
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { DynamicMemory = true, MinimumBytes = min, MaximumBytes = max, MemoryBuffer = 25 });
                var a = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(a.DynamicMemory && a.MinimumBytes == min && a.MaximumBytes == max && a.MemoryBuffer == 25,
                    $"动态内存不符：{a.DynamicMemory} {a.MinimumBytes}/{a.MaximumBytes}/{a.MemoryBuffer}");
                var bad = await ExpectErrorAsync(() => _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { MinimumBytes = 4096L * 1024 * 1024 }));
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { DynamicMemory = false });
                Assert(!(await _hv.GetAdvancedSettingsAsync(Ref)).DynamicMemory, "关闭动态内存失败");
                return "512–2048 MiB / 25% ✓；最小 > 启动内存被拒绝（" + bad + "）；已关闭";
            });

            await Step("adv.nested", "嵌套虚拟化（与动态内存互斥）", async () =>
            {
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { NestedVirtualization = true });
                Assert((await _hv.GetAdvancedSettingsAsync(Ref)).NestedVirtualization, "嵌套虚拟化未启用");
                var bad = await ExpectErrorAsync(() => _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { DynamicMemory = true }));
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { NestedVirtualization = false });
                Assert(!(await _hv.GetAdvancedSettingsAsync(Ref)).NestedVirtualization, "嵌套虚拟化未关闭");
                return "启用 → 关闭 ✓；与动态内存同时启用被拒绝（" + bad + "）";
            });

            await Step("adv.bootorder", "调整启动顺序", async () =>
            {
                var a = await _hv.GetAdvancedSettingsAsync(Ref);
                if (a.BootOrder.Count < 2) return $"只有 {a.BootOrder.Count} 个启动项，无需调整";
                var before = a.BootOrder.Select(b => b.Kind).ToList();
                var rev = a.BootOrder.Select(b => b.Index).Reverse().ToList();
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { BootOrder = rev });
                var after = (await _hv.GetAdvancedSettingsAsync(Ref)).BootOrder.Select(b => b.Kind).ToList();
                Assert(after.SequenceEqual(Enumerable.Reverse(before)), $"顺序不符：{string.Join(",", before)} → {string.Join(",", after)}");
                return $"{string.Join(" > ", before)} → {string.Join(" > ", after)} ✓";
            });

            await Step("adv.notes", "备注（保留系统类型标记）", async () =>
            {
                const string text = "E2E 备注：中文 ✓\n第二行";
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { Notes = text });
                var a = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(a.Notes.Replace("\r", "") == text, "备注不符：" + a.Notes);
                var vm = await RequireAsync();
                Assert(vm.Os == OsKind.Linux, "系统类型标记丢失（应仍为 Linux）");
                return "已保存并读回；[WinVM os=linux] 标记保留 ✓";
            });

            await Step("adv.autoactions", "自动启动 / 停止操作", async () =>
            {
                await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { AutoStartAction = AutoStartActions.Nothing, AutoStartDelaySeconds = 5, AutoStopAction = AutoStopActions.TurnOff });
                var a = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(a.AutoStartAction == AutoStartActions.Nothing && a.AutoStartDelaySeconds == 5 && a.AutoStopAction == AutoStopActions.TurnOff,
                    $"不符：{a.AutoStartAction}/{a.AutoStartDelaySeconds}/{a.AutoStopAction}");
                return "Nothing / 5 秒 / TurnOff ✓";
            });

            await Step("adv.enhanced", "增强会话传输方式（HvSocket）", async () =>
            {
                try { await _hv.ApplyAdvancedSettingsAsync(Ref, new AdvancedSettingsChange { EnhancedSessionHvSocket = true }); }
                catch (HyperVException ex) when (ex.Message.Contains("增强会话")) { throw new EnvBlockedException(ex.Message); }
                var a = await _hv.GetAdvancedSettingsAsync(Ref);
                Assert(a.EnhancedSessionHvSocket, "未切换到 HvSocket");
                return $"HvSocket ✓（主机增强会话策略：{(a.HostEnhancedSessionEnabled ? "已允许" : "未启用")}，未修改主机设置）";
            });

            // ---- 1.5: virtual switch manager (Private switch named after this run only; never External)
            var swName = _prefix + "-SW";
            string? swId = null;
            await Step("sw.create", "创建专用（Private）虚拟交换机", async () =>
            {
                await _hv.CreateSwitchAsync(swName, SwitchKind.Private);
                var s = (await _hv.ListSwitchesDetailedAsync()).FirstOrDefault(x => x.Name == swName)
                        ?? throw new InvalidOperationException("新交换机不在列表中");
                Assert(s.Kind == SwitchKind.Private && !s.InUse, $"类型/占用不符：{s.Kind} {s.ConnectedVms.Count}");
                swId = s.Id;
                return $"{swName} · {s.KindText} · Id={s.Id}";
            });

            await Step("sw.rename", "重命名虚拟交换机", async () =>
            {
                Assert(swId != null, "缺少交换机 Id");
                var n = swName + "-改名";
                await _hv.RenameSwitchAsync(swId!, n);
                var s = (await _hv.ListSwitchesDetailedAsync()).FirstOrDefault(x => x.Id == swId);
                Assert(s?.Name == n, "名称未更新：" + s?.Name);
                swName = n;
                return "新名称：" + n;
            });

            await Step("sw.connect", "将测试虚拟机连接到该交换机", async () =>
            {
                await _hv.SetVmSwitchAsync(Ref, swName);
                var vm = await RequireAsync();
                Assert(vm.SwitchName == swName, "虚拟机交换机未更新：" + vm.SwitchName);
                var s = (await _hv.ListSwitchesDetailedAsync()).First(x => x.Id == swId);
                Assert(s.ConnectedVms.Contains(vm.Name), "交换机的已连接虚拟机列表不含测试虚拟机");
                return $"{vm.Name} → {swName}";
            });

            await Step("sw.delete.blocked", "使用中的交换机删除应被拒绝", async () =>
            {
                var msg = await ExpectErrorAsync(() => _hv.DeleteSwitchAsync(swId!));
                Assert(msg.Contains("正被"), "提示应说明交换机正被使用：" + msg);
                Assert((await _hv.ListSwitchesDetailedAsync()).Any(x => x.Id == swId), "交换机不应被删除");
                return msg;
            });

            await Step("sw.default.protected", "Default Switch 不可删除/重命名（客户端拦截）", async () =>
            {
                var a = await ExpectErrorAsync(() => _hv.DeleteSwitchAsync(VSwitchInfo.DefaultSwitchId), HvErrorKind.Validation);
                var b = await ExpectErrorAsync(() => _hv.RenameSwitchAsync(VSwitchInfo.DefaultSwitchId, "x"), HvErrorKind.Validation);
                return a;
            });

            await Step("sw.delete", "断开后删除虚拟交换机", async () =>
            {
                await _hv.SetVmSwitchAsync(Ref, sw0.Length == 0 ? null : sw0);
                await _hv.DeleteSwitchAsync(swId!);
                Assert((await _hv.ListSwitchesDetailedAsync()).All(x => x.Id != swId), "交换机仍存在");
                var vm = await RequireAsync();
                return $"已删除；测试虚拟机网络恢复为「{(string.IsNullOrEmpty(vm.SwitchName) ? "未连接" : vm.SwitchName)}」";
            });

            await Step("vm.create.win11", "Windows 11 预设（强制 TPM + 安全启动，4 GiB / 2 核）", async () =>
            {
                var name = _prefix + "-win11";
                try
                {
                    await _hv.CreateVmAsync(new CreateVmRequest
                    {
                        Name = name, ProcessorCount = 2, MemoryMiB = 4096, DiskGiB = 64, IsoPath = "", Generation = 2,
                        SwitchName = "", Os = OsKind.Windows, RequireTpm = true
                    });
                }
                catch (HyperVException ex) when (ex.Message.Contains("TPM") || ex.Message.Contains("安全启动"))
                {
                    var left = (await _hv.ListVmsAsync()).Any(v => v.Name == name);
                    Assert(!left, "创建失败后应回滚，不应留下虚拟机");
                    throw new EnvBlockedException("主机无法启用 TPM，已按预期拒绝创建并回滚：" + ex.Message);
                }
                var w = (await _hv.ListVmsAsync()).First(v => v.Name == name);
                var a = await _hv.GetAdvancedSettingsAsync(w.Id);
                var ok = a.TpmEnabled && a.SecureBoot && w.ProcessorCount == 2 && w.MemoryStartupBytes == 4096L * 1024 * 1024;
                await _hv.DeleteAsync(w.Id, deleteFiles: true);
                Assert(ok, $"TPM={a.TpmEnabled} 安全启动={a.SecureBoot} CPU={w.ProcessorCount} 内存={w.MemoryStartupBytes}");
                var low = await ExpectErrorAsync(() => _hv.CreateVmAsync(new CreateVmRequest { Name = name, ProcessorCount = 1, MemoryMiB = 2048, DiskGiB = 32, SwitchName = "", Os = OsKind.Windows, RequireTpm = true }));
                return "TPM ✓ 安全启动 ✓ 2 核 / 4 GiB ✓；低于最低配置被拒绝（" + low + "）";
            });

            // ---- export / import / clone (VM is off here, so this runs even when the host cannot power on VMs)
            string? exported = null;
            string? importedId = null, clonedId = null;
            // checkpoint tree runs after the advanced-settings/switch steps (it is the step that crashed vmms.exe on the test PC)
            await Step("cp.tree", "检查点树：父子关系与当前位置标记", async () =>
            {
                // paced: back-to-back checkpoint operations crashed vmms.exe on the 1.5 test PC (build 27902)
                var a = await _hv.CreateCheckpointAsync(Ref, "E2E 树-父");
                await Task.Delay(Pace);
                var b = await _hv.CreateCheckpointAsync(Ref, "E2E 树-子");
                await Task.Delay(Pace);
                var list = await _hv.ListCheckpointsAsync(Ref);
                var pa = list.FirstOrDefault(c => c.Name == "E2E 树-父");
                var ch = list.FirstOrDefault(c => c.Name == "E2E 树-子");
                Assert(pa != null && ch != null, "两个检查点应都在列表中：" + string.Join(",", list.Select(c => c.Name)));
                Assert(ch!.ParentId.Equals(pa!.Id, StringComparison.OrdinalIgnoreCase), $"子检查点的父 Id 应为 {pa.Id}，实际 '{ch.ParentId}'");
                Assert(ch.IsCurrent && !pa.IsCurrent, "当前位置应在子检查点下");
                var tree = CheckpointNode.Build(list);
                var root = tree.Single(n => !n.IsNowMarker);
                Assert(root.Checkpoint!.Id == pa.Id && root.Children.Any(n => n.Checkpoint?.Id == ch.Id), "树结构不符");
                Assert(root.Children.Single(n => n.Checkpoint?.Id == ch.Id).Children.Any(n => n.IsNowMarker), "当前位置标记应位于子检查点下");
                await _hv.ApplyCheckpointAsync(Ref, pa.Id);
                await Task.Delay(Pace);
                var after = await _hv.ListCheckpointsAsync(Ref);
                Assert(after.Single(c => c.Id == pa.Id).IsCurrent, "应用父检查点后当前位置应移到父检查点下");
                await _hv.DeleteCheckpointAsync(Ref, ch.Id);
                await Task.Delay(Pace);
                await _hv.DeleteCheckpointAsync(Ref, pa.Id);
                await Task.Delay(Pace);
                Assert((await _hv.ListCheckpointsAsync(Ref)).Count == 0, "清理检查点失败");
                return "父 → 子 → 当前位置 ✓；应用父检查点后标记移动 ✓；已删除";
            });

            await Step("vm.export.cancel", "导出：取消后不留下文件", async () =>
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                try
                {
                    await _hv.ExportVmAsync(Ref, _exportRoot, null, cts.Token);
                    throw new InvalidOperationException("已取消的导出不应成功");
                }
                catch (OperationCanceledException) { }
                var vm = await RequireAsync();
                Assert(!IsReal || !Directory.Exists(Path.Combine(_exportRoot, vm.Name)), "取消后仍残留导出文件夹");
                return "已取消，未留下导出文件";
            });

            await Step("vm.export", "导出到临时文件夹（含进度）", async () =>
            {
                var reports = new List<OpProgress>();
                var sw = Stopwatch.StartNew();
                exported = await _hv.ExportVmAsync(Ref, _exportRoot, new SyncProgress<OpProgress>(reports.Add));
                Assert(!string.IsNullOrEmpty(exported), "未返回导出路径");
                if (IsReal)
                {
                    Assert(Directory.Exists(exported!), "导出文件夹不存在：" + exported);
                    var vmcx = Directory.EnumerateFiles(exported!, "*.vmcx", SearchOption.AllDirectories).Count();
                    var vhdx = Directory.EnumerateFiles(exported!, "*.vhdx", SearchOption.AllDirectories).Count();
                    Assert(vmcx == 1, "应包含 1 个 .vmcx，实际 " + vmcx);
                    Assert(vhdx >= 1, "应包含 .vhdx");
                }
                Assert(reports.Count > 0 && reports[^1].Fraction >= 0.999, "未报告完成进度");
                var vm = await RequireAsync();
                Assert(vm.State == VmState.Off, "源虚拟机状态应保持 Off");
                return $"{exported}（{sw.ElapsedMilliseconds} ms，进度回调 {reports.Count} 次）";
            });

            await Step("vm.import", "从导出文件夹导入副本（新 ID、新名称）", async () =>
            {
                Assert(exported != null, "缺少导出结果");
                var name = _prefix + "-导入";
                var src = await RequireAsync();
                importedId = await _hv.ImportVmAsync(exported!, name);
                var vm = (await _hv.ListVmsAsync()).FirstOrDefault(v => v.Name == name)
                         ?? throw new InvalidOperationException("导入的虚拟机不在列表中");
                Assert(vm.Id != src.Id, "导入的虚拟机应有新的 ID");
                Assert(string.IsNullOrEmpty(importedId) || vm.Id.Equals(importedId, StringComparison.OrdinalIgnoreCase), "返回的 ID 不一致");
                importedId = vm.Id;
                Assert(vm.State == VmState.Off, "导入后应为 Off");
                Assert(vm.ProcessorCount == src.ProcessorCount && vm.MemoryStartupBytes == src.MemoryStartupBytes, "配置应与源一致");
                if (IsReal)
                {
                    Assert(vm.VhdPath.StartsWith(_hv.StoreRoot, StringComparison.OrdinalIgnoreCase), "磁盘应复制到存储目录：" + vm.VhdPath);
                    Assert(!vm.VhdPath.Equals(src.VhdPath, StringComparison.OrdinalIgnoreCase), "磁盘不应与源共用");
                    Assert(_fileExists(vm.VhdPath), "导入的 VHDX 不存在");
                }
                return $"{vm.Name} Id={vm.Id} VHD={vm.VhdPath}";
            });

            await Step("vm.import.duplicate", "重名导入应被拒绝且不留残留", async () =>
            {
                Assert(exported != null, "缺少导出结果");
                var before = (await _hv.ListVmsAsync()).Count;
                var msg = await ExpectErrorAsync(() => _hv.ImportVmAsync(exported!, _prefix + "-导入"), HvErrorKind.Validation, HvErrorKind.AlreadyExists);
                var after = (await _hv.ListVmsAsync()).Count;
                Assert(before == after, $"虚拟机数量变化 {before} → {after}");
                return msg;
            });

            await Step("vm.clone", "克隆（导出 + 导入副本，含进度）", async () =>
            {
                var name = _prefix + "-克隆";
                var src = await RequireAsync();
                var reports = new List<OpProgress>();
                var sw = Stopwatch.StartNew();
                clonedId = await _hv.CloneVmAsync(Ref, name, new SyncProgress<OpProgress>(reports.Add));
                var vm = (await _hv.ListVmsAsync()).FirstOrDefault(v => v.Name == name)
                         ?? throw new InvalidOperationException("克隆的虚拟机不在列表中");
                clonedId = vm.Id;
                Assert(vm.Id != src.Id, "克隆应有新的 ID");
                Assert(vm.Os == src.Os, "系统标签应保留");
                Assert(reports.Any(r => r.Fraction >= 0.5) && reports[^1].Fraction >= 0.999, "进度应覆盖两个阶段");
                if (IsReal)
                {
                    Assert(!vm.VhdPath.Equals(src.VhdPath, StringComparison.OrdinalIgnoreCase), "克隆不应共用源磁盘");
                    Assert(_fileExists(vm.VhdPath), "克隆的 VHDX 不存在");
                    var tmp = Path.Combine(_hv.StoreRoot, ".winvm-tmp");
                    Assert(!Directory.Exists(tmp) || !Directory.EnumerateFileSystemEntries(tmp).Any(), "临时导出文件夹未清理");
                }
                return $"{vm.Name} Id={vm.Id}（{sw.ElapsedMilliseconds} ms）";
            });

            await Step("vm.copies.delete", "删除导入与克隆的虚拟机（含文件）并清理导出文件夹", async () =>
            {
                var notes = new List<string>();
                foreach (var id in new[] { importedId, clonedId }.Where(i => !string.IsNullOrEmpty(i)))
                {
                    var vm = (await _hv.ListVmsAsync()).FirstOrDefault(v => v.Id == id);
                    if (vm == null) continue;
                    var dir = string.IsNullOrEmpty(vm.Path) ? null : vm.Path;
                    await _hv.DeleteAsync(vm.Id, deleteFiles: true);
                    Assert((await _hv.ListVmsAsync()).All(v => v.Id != id), "删除后仍在列表中：" + vm.Name);
                    Assert(!IsReal || !_fileExists(vm.VhdPath), "VHDX 未删除：" + vm.VhdPath);
                    Assert(!IsReal || dir == null || !_dirExists(dir), "文件夹未删除：" + dir);
                    notes.Add(vm.Name);
                }
                if (IsReal && Directory.Exists(_exportRoot)) Directory.Delete(_exportRoot, true);
                Assert(!IsReal || !Directory.Exists(_exportRoot), "导出文件夹未删除");
                return "已删除 " + string.Join("、", notes) + "；导出文件夹已删除";
            });

            await Step("vm.start", "启动", async () =>
            {
                try { await _hv.StartAsync(Ref); }
                catch (HyperVException ex) when (ex.Kind == HvErrorKind.HostCannotStart)
                {
                    _powerBlocked = "主机固件组件签名无效 0x80090006";
                    _report.EnvironmentIssues.Add("主机无法启动任何虚拟机：" + ex.Message + " 原始信息：" + Trunc(ex.Detail.Replace("\r", " ").Replace("\n", " "), 300));
                    throw new EnvBlockedException(ex.Message);
                }
                var vm = await WaitStateAsync(VmState.Running);
                await Task.Delay(2500);
                vm = await RequireAsync();
                Assert(vm.UptimeSeconds >= 1, "运行时间应 > 0");
                return $"{vm.StateText} · 运行时间 {vm.UptimeText} · 已分配 {vm.MemoryAssignedText}";
            });

            await Step("cp.online", "运行中创建并删除检查点", async () =>
            {
                var id = await _hv.CreateCheckpointAsync(Ref, "E2E 在线检查点");
                var list = await _hv.ListCheckpointsAsync(Ref);
                Assert(list.Any(c => c.Id == id), "未找到在线检查点");
                await _hv.DeleteCheckpointAsync(Ref, id);
                return "创建并删除成功";
            });

            await Step("vm.pause", "暂停", async () =>
            {
                await _hv.PauseAsync(Ref);
                var vm = await WaitStateAsync(VmState.Paused, 30);
                return vm.StateText;
            });

            await Step("vm.resume", "恢复", async () =>
            {
                await _hv.ResumeAsync(Ref);
                var vm = await WaitStateAsync(VmState.Running, 30);
                return vm.StateText;
            });

            await Step("vm.restart.soft", "软重启（无来宾系统时应给出友好提示）", async () =>
            {
                try
                {
                    await _hv.RestartAsync(Ref);
                    return "来宾接受了重启请求";
                }
                catch (HyperVException ex) when (ex.Kind is HvErrorKind.GuestNoResponse or HvErrorKind.InvalidState or HvErrorKind.Unknown)
                {
                    Assert(ex.Message.Length < 200, "错误信息过长");
                    return $"按预期失败 [{ex.Kind}]：{ex.Message}";
                }
            });

            await Step("vm.reset", "重置（硬重启）", async () =>
            {
                await _hv.ResetAsync(Ref);
                var vm = await WaitStateAsync(VmState.Running, 60);
                return vm.StateText;
            });

            await Step("vm.stop.soft", "正常关机（无来宾系统时应给出友好提示）", async () =>
            {
                try
                {
                    await _hv.StopAsync(Ref);
                    var vm = await WaitStateAsync(VmState.Off, 60);
                    return "来宾已关机：" + vm.StateText;
                }
                catch (HyperVException ex) when (ex.Kind is HvErrorKind.GuestNoResponse or HvErrorKind.InvalidState or HvErrorKind.Unknown or HvErrorKind.Timeout)
                {
                    return $"按预期失败 [{ex.Kind}]：{ex.Message}";
                }
            });

            await Step("vm.turnoff", "强制关闭", async () =>
            {
                var cur = await RequireAsync();
                if (cur.State != VmState.Off) await _hv.TurnOffAsync(Ref);
                var vm = await WaitStateAsync(VmState.Off, 60);
                return vm.StateText;
            });

            await Step("vm.delete", "删除虚拟机并删除文件", async () =>
            {
                await _hv.DeleteAsync(Ref, deleteFiles: true);
                var vm = await FindAsync();
                Assert(vm == null, "虚拟机仍在列表中");
                Assert(vhdPath == null || !_fileExists(vhdPath), "VHDX 未删除：" + vhdPath);
                Assert(vmFolder == null || !_dirExists(vmFolder), "文件夹未删除：" + vmFolder);
                _vmId = null;
                return $"已删除；VHD 与文件夹已清理（{vmFolder}）";
            });
        }
        finally
        {
            await CleanupAsync(vhdPath, vmFolder);
        }

        _report.FinishedAt = DateTime.Now;
        _report.Passed = _report.Steps.Count(s => s.Status == "pass");
        _report.Failed = _report.Steps.Count(s => s.Status == "fail");
        _report.Skipped = _report.Steps.Count(s => s.Status == "skip");
        _report.EnvBlocked = _report.Steps.Count(s => s.Status == "env");
        return _report;
    }

    private async Task CleanupAsync(string? vhdPath, string? vmFolder)
    {
        if (!_hvOk) { _report.CleanupVerified = true; return; }
        await Step("cleanup", "清理（无论成功失败都执行）", async () =>
        {
            var notes = new List<string>();
            // Only VMs created by this run: same Id, or a name starting with this run's unique WinVM-E2E-<timestamp>.
            var leftovers = (await _hv.ListVmsAsync())
                .Where(v => (_vmId != null && v.Id == _vmId) || v.Name.StartsWith(_prefix, StringComparison.Ordinal)).ToList();
            foreach (var vm in leftovers)
            {
                try { if (vm.State != VmState.Off) await _hv.TurnOffAsync(vm.Ref); } catch (Exception ex) { notes.Add("强制关闭：" + ex.Message); }
                await _hv.DeleteAsync(vm.Ref, true);
                notes.Add("已删除残留测试虚拟机 " + vm.Name);
            }
            if (vmFolder != null && _dirExists(vmFolder) && Path.GetFileName(vmFolder).StartsWith("WinVM-E2E-", StringComparison.Ordinal)
                && vmFolder.StartsWith(_hv.StoreRoot, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(vmFolder, true); notes.Add("已删除残留文件夹"); } catch (Exception ex) { notes.Add("文件夹删除失败：" + ex.Message); }
            }
            if (IsReal && Directory.Exists(_exportRoot))
            {
                try { Directory.Delete(_exportRoot, true); notes.Add("已删除残留导出文件夹"); } catch (Exception ex) { notes.Add("导出文件夹删除失败：" + ex.Message); }
            }
            // 1.5: switches created by this run only (name starts with this run's unique prefix; never the Default Switch)
            var swLeft = false;
            try
            {
                foreach (var s in (await _hv.ListSwitchesDetailedAsync()).Where(x => x.Name.StartsWith(_prefix, StringComparison.Ordinal) && !x.IsDefaultSwitch))
                {
                    try { await _hv.DeleteSwitchAsync(s.Id); notes.Add("已删除残留测试交换机 " + s.Name); }
                    catch (Exception ex) { notes.Add("交换机删除失败：" + ex.Message); }
                }
                swLeft = (await _hv.ListSwitchesDetailedAsync()).Any(x => x.Name.StartsWith(_prefix, StringComparison.Ordinal));
            }
            catch (Exception ex) { notes.Add("读取交换机失败：" + ex.Message); }
            var still = (await _hv.ListVmsAsync()).Any(v => v.Name.StartsWith(_prefix, StringComparison.Ordinal));
            var exportLeft = IsReal && Directory.Exists(_exportRoot);
            var vhdLeft = vhdPath != null && _fileExists(vhdPath);
            var dirLeft = vmFolder != null && _dirExists(vmFolder);
            _report.CleanupVerified = !still && !vhdLeft && !dirLeft && !exportLeft && !swLeft;
            Assert(_report.CleanupVerified, $"清理不完整 vm={still} vhd={vhdLeft} dir={dirLeft} export={exportLeft} switch={swLeft}");
            return notes.Count == 0 ? "无残留（已验证虚拟机、VHD、文件夹、测试交换机均不存在）" : string.Join("；", notes);
        });
    }

    public static string Save(SelfTestReport report, string? outPath)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Directory.CreateDirectory(DefaultOutDir);
        var stamped = Path.Combine(DefaultOutDir, $"selftest-{report.StartedAt:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(stamped, json);
        File.WriteAllText(Path.Combine(DefaultOutDir, "selftest-latest.json"), json);
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, json);
        }
        return stamped;
    }
}

internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) => _a = a;
    public void Report(T value) { lock (this) _a(value); }
}

internal sealed class EnvBlockedException : Exception
{
    public EnvBlockedException(string message) : base(message) { }
}
