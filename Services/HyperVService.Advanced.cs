using WinVM.Models;

namespace WinVM.Services;

/// <summary>1.5: advanced VM settings and virtual switch management.</summary>
public sealed partial class HyperVService
{
    private const long TwoMiB = 2L * 1024 * 1024;

    internal static long AlignMem(long bytes) => (bytes + TwoMiB / 2) / TwoMiB * TwoMiB;

    /// <summary>Validates a change against the current settings. Throws <see cref="ArgumentException"/> with a Chinese message.</summary>
    public static void ValidateAdvanced(VmAdvancedSettings cur, AdvancedSettingsChange ch)
    {
        if (!cur.IsGen2)
        {
            if (ch.SecureBoot != null || ch.SecureBootTemplate != null) throw new ArgumentException("第 1 代虚拟机不支持安全启动。");
            if (ch.Tpm != null) throw new ArgumentException("第 1 代虚拟机不支持虚拟 TPM。");
            if (ch.BootOrder != null) throw new ArgumentException("第 1 代虚拟机的启动顺序请在 Hyper-V 管理器中调整。");
        }
        if (ch.RequiresOff && !cur.IsOff)
            throw new ArgumentException("安全启动、TPM、内存、启动顺序、嵌套虚拟化与增强会话需在虚拟机关机后修改。");
        if (ch.SecureBootTemplate != null && !SecureBootTemplates.All.Contains(ch.SecureBootTemplate))
            throw new ArgumentException("未知的安全启动模板。");

        var dyn = ch.DynamicMemory ?? cur.DynamicMemory;
        var nested = ch.NestedVirtualization ?? cur.NestedVirtualization;
        if (dyn && nested) throw new ArgumentException("嵌套虚拟化要求关闭动态内存，请二选一。");
        if (dyn && (ch.DynamicMemory != null || ch.MinimumBytes != null || ch.MaximumBytes != null || ch.MemoryBuffer != null))
        {
            var min = AlignMem(ch.MinimumBytes ?? cur.MinimumBytes);
            var max = AlignMem(ch.MaximumBytes ?? cur.MaximumBytes);
            var start = cur.StartupBytes;
            if (min < 32L * 1024 * 1024) throw new ArgumentException("最小内存至少为 32 MiB。");
            if (max > 256L * 1024 * 1024 * 1024) throw new ArgumentException("最大内存过大（上限 256 GiB）。");
            if (min > max) throw new ArgumentException("最小内存不能大于最大内存。");
            if (start > 0 && (start < min || start > max))
                throw new ArgumentException($"启动内存（{start / 1024 / 1024} MiB）必须介于最小与最大内存之间。");
            var buf = ch.MemoryBuffer ?? cur.MemoryBuffer;
            if (buf is < 5 or > 2000) throw new ArgumentException("内存缓冲区必须在 5% 到 2000% 之间。");
        }
        if (ch.BootOrder != null)
        {
            var n = cur.BootOrder.Count;
            if (n == 0 || ch.BootOrder.Count != n || ch.BootOrder.Distinct().Count() != n || ch.BootOrder.Any(i => i < 0 || i >= n))
                throw new ArgumentException("启动顺序无效：必须包含每个启动设备且不能重复。");
        }
        if (ch.AutoStartAction != null && !AutoStartActions.All.Contains(ch.AutoStartAction)) throw new ArgumentException("未知的自动启动操作。");
        if (ch.AutoStopAction != null && !AutoStopActions.All.Contains(ch.AutoStopAction)) throw new ArgumentException("未知的自动停止操作。");
        if (ch.AutoStartDelaySeconds is < 0 or > 86400) throw new ArgumentException("自动启动延迟必须在 0 到 86400 秒之间。");
        if (ch.Notes != null && ch.Notes.Length > 4000) throw new ArgumentException("备注过长（最多 4000 个字符）。");
    }

    internal static string AdvancedReadScript(string vm) => $$"""
$vm = Resolve-WinVM {{Q(vm)}}
$boot = @(); $sb = 'Off'; $tpl = ''
if ($vm.Generation -eq 2) {
  $fw = Get-VMFirmware -VM $vm
  $sb = [string]$fw.SecureBoot; $tpl = [string]$fw.SecureBootTemplate
  $i = 0
  foreach ($b in @($fw.BootOrder)) {
    $t = [string]$b.BootType
    $k = 'Other'
    if ($t -eq 'Network') { $k = 'Network' }
    elseif ($t -eq 'File') { $k = 'File' }
    elseif ($t -eq 'Drive') { if ($b.Device -and $b.Device.GetType().Name -match 'Dvd') { $k = 'Dvd' } else { $k = 'Disk' } }
    $d = [string]$b.Description
    try {
      if ($b.Device -and $b.Device.Path) { $d = [System.IO.Path]::GetFileName([string]$b.Device.Path) }
      elseif ($b.Device -and $b.Device.SwitchName) { $d = [string]$b.Device.SwitchName }
    } catch {}
    $boot += [pscustomobject]@{ Index = $i; Kind = $k; Description = $d }
    $i++
  }
}
$tpm = $false
try { $tpm = [bool](Get-VMSecurity -VM $vm -ErrorAction Stop).TpmEnabled } catch {}
$m = Get-VMMemory -VM $vm
$p = Get-VMProcessor -VM $vm
$transport = ''
try { $transport = [string]$vm.EnhancedSessionTransportType } catch {}
$hostEnh = $false
try { $hostEnh = [bool](Get-VMHost).EnableEnhancedSessionMode } catch {}
[pscustomobject]@{
  Generation = [int]$vm.Generation
  State = [string]$vm.State
  SecureBoot = $sb
  SecureBootTemplate = $tpl
  Tpm = $tpm
  DynamicMemory = [bool]$m.DynamicMemoryEnabled
  Startup = [long]$m.Startup
  Minimum = [long]$m.Minimum
  Maximum = [long]$m.Maximum
  Buffer = [int]$m.Buffer
  Boot = @($boot)
  Nested = [bool]$p.ExposeVirtualizationExtensions
  StartAction = [string]$vm.AutomaticStartAction
  StartDelay = [int]$vm.AutomaticStartDelay
  StopAction = [string]$vm.AutomaticStopAction
  Notes = [string]$vm.Notes
  Transport = $transport
  HostEnhanced = $hostEnh
} | ConvertTo-Json -Compress -Depth 4
""";

    public async Task<VmAdvancedSettings> GetAdvancedSettingsAsync(string vm, CancellationToken ct = default)
    {
        var json = await RunAsync("读取高级设置", AdvancedReadScript(vm), TimeSpan.FromSeconds(45), PsLane.Query, ct).ConfigureAwait(false);
        return VmParser.ParseAdvanced(json);
    }

    /// <summary>PowerShell body (after Resolve-WinVM) applying the change. Re-checks state and invariants host-side.</summary>
    internal static string AdvancedApplyBody(AdvancedSettingsChange ch, int bootCount)
    {
        var l = new List<string>();
        if (ch.RequiresOff) l.Add("if ($vm.State -ne 'Off') { throw 'WINVM:请先关机后再修改这些设置。' }");
        if (ch.SecureBoot != null || ch.SecureBootTemplate != null || ch.Tpm != null || ch.BootOrder != null)
            l.Add("if ($vm.Generation -ne 2) { throw 'WINVM:第 1 代虚拟机不支持安全启动、TPM 与启动顺序设置。' }");

        if (ch.DynamicMemory != null || ch.NestedVirtualization != null || ch.MinimumBytes != null || ch.MaximumBytes != null || ch.MemoryBuffer != null)
        {
            // nested virtualization and dynamic memory are mutually exclusive: disable first, enable last
            l.Add("$m0 = Get-VMMemory -VM $vm; $p0 = Get-VMProcessor -VM $vm");
            l.Add($"$dynAfter = {(ch.DynamicMemory is bool d ? (d ? "$true" : "$false") : "[bool]$m0.DynamicMemoryEnabled")}");
            l.Add($"$nestAfter = {(ch.NestedVirtualization is bool n ? (n ? "$true" : "$false") : "[bool]$p0.ExposeVirtualizationExtensions")}");
            l.Add("if ($dynAfter -and $nestAfter) { throw 'WINVM:嵌套虚拟化要求关闭动态内存，请二选一。' }");
            if (ch.NestedVirtualization == false) l.Add("Set-VMProcessor -VM $vm -ExposeVirtualizationExtensions $false");
            if (ch.DynamicMemory == false)
                l.Add("Set-VMMemory -VM $vm -DynamicMemoryEnabled $false");
            else if (ch.DynamicMemory == true || ch.MinimumBytes != null || ch.MaximumBytes != null || ch.MemoryBuffer != null)
            {
                var parts = new List<string> { "-VM $vm" };
                if (ch.DynamicMemory == true) parts.Add("-DynamicMemoryEnabled $true");
                if (ch.MinimumBytes is long mn) parts.Add($"-MinimumBytes {AlignMem(mn)}");
                if (ch.MaximumBytes is long mx) parts.Add($"-MaximumBytes {AlignMem(mx)}");
                if (ch.MemoryBuffer is int bf) parts.Add($"-Buffer {bf}");
                l.Add("if ($dynAfter) { Set-VMMemory " + string.Join(" ", parts) + " }");
            }
            if (ch.NestedVirtualization == true) l.Add("Set-VMProcessor -VM $vm -ExposeVirtualizationExtensions $true");
        }

        if (ch.SecureBoot == false) l.Add("Set-VMFirmware -VM $vm -EnableSecureBoot Off");
        else if (ch.SecureBoot == true || ch.SecureBootTemplate != null)
        {
            var tpl = ch.SecureBootTemplate != null ? $" -SecureBootTemplate {Q(ch.SecureBootTemplate)}" : "";
            l.Add($"try {{ Set-VMFirmware -VM $vm -EnableSecureBoot On{tpl} -ErrorAction Stop }} catch {{ throw \"WINVM:无法启用安全启动：$($_.Exception.Message)\" }}");
        }

        if (ch.Tpm == true)
        {
            l.Add("""
try {
  $kp = Get-VMKeyProtector -VM $vm -ErrorAction Stop
  if (-not $kp -or @($kp).Length -le 4) { Set-VMKeyProtector -VM $vm -NewLocalKeyProtector -ErrorAction Stop }
  Enable-VMTPM -VM $vm -ErrorAction Stop
} catch { throw "WINVM:无法启用虚拟 TPM（主机可能不支持本地密钥保护器）：$($_.Exception.Message)" }
""");
        }
        else if (ch.Tpm == false) l.Add("Disable-VMTPM -VM $vm");

        if (ch.BootOrder != null)
        {
            l.Add("$o = @((Get-VMFirmware -VM $vm).BootOrder)");
            l.Add($"if ($o.Count -ne {bootCount}) {{ throw 'WINVM:启动设备已在别处被修改，请重新打开高级设置。' }}");
            l.Add("Set-VMFirmware -VM $vm -BootOrder @(" + string.Join(", ", ch.BootOrder.Select(i => $"$o[{i}]")) + ")");
        }

        if (ch.EnhancedSessionHvSocket is bool hv)
            l.Add($"try {{ Set-VM -VM $vm -EnhancedSessionTransportType {(hv ? "HvSocket" : "VMBus")} -ErrorAction Stop }} catch {{ throw \"WINVM:此主机不支持设置增强会话传输方式：$($_.Exception.Message)\" }}");

        var auto = new List<string>();
        if (ch.AutoStartAction != null) auto.Add($"-AutomaticStartAction {ch.AutoStartAction}");
        if (ch.AutoStartDelaySeconds is int dl) auto.Add($"-AutomaticStartDelay {dl}");
        if (ch.AutoStopAction != null) auto.Add($"-AutomaticStopAction {ch.AutoStopAction}");
        if (auto.Count > 0) l.Add("Set-VM -VM $vm " + string.Join(" ", auto));

        if (ch.Notes != null)
        {
            l.Add($"$user = {Q(ch.Notes.Trim())}");
            l.Add("""
$tag = [regex]::Match([string]$vm.Notes, '\[WinVM os=(windows|linux)\]', 'IgnoreCase')
$n = $user
if ($tag.Success) { if ($user) { $n = $tag.Value + "`n" + $user } else { $n = $tag.Value } }
Set-VM -VM $vm -Notes $n
""");
        }
        return string.Join("\n", l);
    }

    public async Task ApplyAdvancedSettingsAsync(string vm, AdvancedSettingsChange change)
    {
        if (change.IsEmpty) return;
        var cur = await GetAdvancedSettingsAsync(vm).ConfigureAwait(false);
        ValidateAdvanced(cur, change);
        await VmCmd("保存高级设置", vm, AdvancedApplyBody(change, cur.BootOrder.Count), TimeSpan.FromMinutes(2)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- virtual switches

    internal const string SwitchListScript = """
$nics = @(Get-VM | Get-VMNetworkAdapter -ErrorAction SilentlyContinue)
$r = foreach ($s in @(Get-VMSwitch)) {
  [pscustomobject]@{
    Id = [string]$s.Id
    Name = [string]$s.Name
    Type = [string]$s.SwitchType
    Adapter = [string]$s.NetAdapterInterfaceDescription
    AllowManagementOS = [bool]$s.AllowManagementOS
    Vms = @($nics | Where-Object { [string]$_.SwitchId -eq [string]$s.Id } | ForEach-Object { [string]$_.VMName } | Select-Object -Unique)
  }
}
ConvertTo-Json -InputObject @($r) -Compress -Depth 4
""";

    public async Task<IReadOnlyList<VSwitchInfo>> ListSwitchesDetailedAsync(CancellationToken ct = default)
    {
        if (!_isWindows) return Array.Empty<VSwitchInfo>();
        var json = await RunAsync("读取虚拟交换机", SwitchListScript, TimeSpan.FromSeconds(45), PsLane.Query, ct).ConfigureAwait(false);
        return VmParser.ParseSwitches(json);
    }

    public async Task<IReadOnlyList<NetAdapterInfo>> ListPhysicalAdaptersAsync(CancellationToken ct = default)
    {
        if (!_isWindows) return Array.Empty<NetAdapterInfo>();
        const string script = """
$bound = @(Get-VMSwitch -SwitchType External -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.NetAdapterInterfaceDescription })
$r = foreach ($a in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
  [pscustomobject]@{ Name = [string]$a.Name; Description = [string]$a.InterfaceDescription; Status = [string]$a.Status; Bound = ($bound -contains [string]$a.InterfaceDescription) }
}
ConvertTo-Json -InputObject @($r) -Compress -Depth 3
""";
        var json = await RunAsync("读取网络适配器", script, TimeSpan.FromSeconds(45), PsLane.Query, ct).ConfigureAwait(false);
        return VmParser.ParseAdapters(json);
    }

    public static string ValidateSwitchName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("请填写交换机名称。");
        if (n.Length > 100) throw new ArgumentException("交换机名称过长（最多 100 个字符）。");
        if (n.IndexOfAny(new[] { '*', '?', '[', ']' }) >= 0) throw new ArgumentException("交换机名称不能包含 * ? [ ] 字符。");
        return n;
    }

    internal const string SwitchPrelude = """
function Resolve-WinVMSwitch([string]$id) {
  $g = [guid]::Empty
  if (-not [guid]::TryParse($id, [ref]$g)) { throw 'WINVM:无效的交换机标识。' }
  $s = Get-VMSwitch -Id $g -ErrorAction SilentlyContinue
  if (-not $s) { throw 'WINVM:找不到该虚拟交换机，请刷新列表。' }
  if ([string]$s.Id -eq 'c08cb7b8-9b3c-408e-8e30-5e16a3aeb444') { throw 'WINVM:「Default Switch」由 Windows 管理，无法重命名或删除。' }
  return $s
}
function Test-WinVMSwitchName([string]$n) {
  if (@(Get-VMSwitch | Where-Object { $_.Name -eq $n }).Count -gt 0) { throw "WINVM:已存在名为「$n」的虚拟交换机。" }
}
""";

    public async Task CreateSwitchAsync(string name, SwitchKind kind, string? adapterName = null, bool allowManagementOs = true)
    {
        var n = ValidateSwitchName(name);
        if (kind == SwitchKind.External && string.IsNullOrWhiteSpace(adapterName)) throw new ArgumentException("外部交换机需要选择一个物理网卡。");
        var create = kind switch
        {
            SwitchKind.External => $"New-VMSwitch -Name $n -NetAdapterName {Q(adapterName)} -AllowManagementOS {(allowManagementOs ? "$true" : "$false")} -ErrorAction Stop",
            SwitchKind.Internal => "New-VMSwitch -Name $n -SwitchType Internal -ErrorAction Stop",
            _ => "New-VMSwitch -Name $n -SwitchType Private -ErrorAction Stop"
        };
        var script = $"{SwitchPrelude}\n$n = {Q(n)}\nTest-WinVMSwitchName $n\n$s = {create}\n'CREATED:' + [string]$s.Id";
        await RunAsync("创建虚拟交换机", script, TimeSpan.FromMinutes(2), PsLane.Action).ConfigureAwait(false);
    }

    /// <summary>Client-side guard: the Default Switch is never sent to Rename-/Remove-VMSwitch (the script checks again).</summary>
    internal static void GuardNotDefault(string switchId)
    {
        if (string.Equals(switchId?.Trim('{', '}', ' '), VSwitchInfo.DefaultSwitchId, StringComparison.OrdinalIgnoreCase))
            throw new HyperVException(HvErrorKind.Validation, "「Default Switch」由 Windows 管理，无法重命名或删除。");
    }

    public async Task RenameSwitchAsync(string switchId, string newName)
    {
        GuardNotDefault(switchId);
        var n = ValidateSwitchName(newName);
        var script = $"{SwitchPrelude}\n$s = Resolve-WinVMSwitch {Q(switchId)}\n$n = {Q(n)}\nif ($s.Name -ne $n) {{ Test-WinVMSwitchName $n; Rename-VMSwitch -VMSwitch $s -NewName $n }}\n'OK'";
        await RunAsync("重命名虚拟交换机", script, TimeSpan.FromMinutes(1), PsLane.Action).ConfigureAwait(false);
    }

    public async Task DeleteSwitchAsync(string switchId)
    {
        GuardNotDefault(switchId);
        var script = $$"""
{{SwitchPrelude}}
$s = Resolve-WinVMSwitch {{Q(switchId)}}
$users = @(Get-VM | Get-VMNetworkAdapter -ErrorAction SilentlyContinue | Where-Object { [string]$_.SwitchId -eq [string]$s.Id } | ForEach-Object { [string]$_.VMName } | Select-Object -Unique)
if ($users.Count -gt 0) { throw ("WINVM:交换机「" + $s.Name + "」正被以下虚拟机使用：" + ($users -join '、') + "。请先为它们更换或断开网络。") }
Remove-VMSwitch -VMSwitch $s -Force
'OK'
""";
        await RunAsync("删除虚拟交换机", script, TimeSpan.FromMinutes(2), PsLane.Action).ConfigureAwait(false);
    }

    public Task SetVmSwitchAsync(string vm, string? switchName)
    {
        string body;
        if (string.IsNullOrEmpty(switchName))
            body = "$nic = Get-VMNetworkAdapter -VM $vm | Select-Object -First 1\nif ($nic) { Disconnect-VMNetworkAdapter -VMNetworkAdapter $nic }";
        else
            body = $$"""
$sw = {{Q(switchName)}}
if (-not (Get-VMSwitch | Where-Object { $_.Name -eq $sw })) { throw "WINVM:找不到虚拟交换机「$sw」。" }
$nic = Get-VMNetworkAdapter -VM $vm | Select-Object -First 1
if ($nic) { Connect-VMNetworkAdapter -VMNetworkAdapter $nic -SwitchName $sw }
elseif ($vm.State -eq 'Off') { Add-VMNetworkAdapter -VM $vm -SwitchName $sw }
else { throw 'WINVM:该虚拟机没有网络适配器，请关机后再连接交换机。' }
""";
        return VmCmd("更换虚拟交换机", vm, body, TimeSpan.FromMinutes(1));
    }
}
