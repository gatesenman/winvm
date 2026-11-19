using System.Diagnostics;
using System.Text.Json;
using WinVM.Models;

namespace WinVM.Services;

/// <summary>
/// Hyper-V access through PowerShell. All methods are async and never block the UI thread.
/// VM arguments accept either the VM Id (GUID, preferred – unambiguous and rename-safe) or the exact name.
/// </summary>
public sealed partial class HyperVService : IHyperVService, IDisposable
{
    public static string AppVersion =>
        typeof(HyperVService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.6.0";

    public static string DefaultStorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WinVM", "Machines");

    private readonly IPsRunner _ps;
    private readonly bool _isWindows;

    public HyperVService(IPsRunner? runner = null, bool? isWindows = null, string? storeRoot = null)
    {
        _ps = runner ?? new PowerShellRunner();
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        StoreRoot = storeRoot ?? DefaultStorePath;
    }

    public string StoreRoot { get; }

    public void Warmup()
    {
        if (_isWindows && _ps is PowerShellRunner r) r.Warmup();
    }

    public void RequestFresh() => _ps.RequestFresh();

    public void Dispose() => _ps.Dispose();

    internal static string Q(string? value) => "'" + (value ?? "").Replace("'", "''")
        .Replace("\u2018", "\u2018\u2018").Replace("\u2019", "\u2019\u2019") + "'";

    /// <summary>PowerShell helper that resolves a VM by Id or exact (non-wildcard) name.</summary>
    internal const string Prelude = """
function Resolve-WinVM([string]$key) {
  $g = [guid]::Empty
  if ([guid]::TryParse($key, [ref]$g)) {
    $v = Get-VM -Id $g -ErrorAction SilentlyContinue
    if ($v) { return $v }
  }
  $all = @(Get-VM | Where-Object { $_.Name -eq $key })
  if ($all.Count -eq 0) { throw "WINVM:找不到虚拟机「$key」，请刷新列表。" }
  if ($all.Count -gt 1) { throw "WINVM:存在多个名为「$key」的虚拟机，请先在 Hyper-V 管理器中重命名。" }
  return $all[0]
}
""";

    private async Task<string> RunAsync(string action, string script, TimeSpan timeout, PsLane lane, CancellationToken ct = default)
    {
        if (!_isWindows)
            throw new HyperVException(HvErrorKind.NotWindows, "WinVM 需要在 Windows（Hyper-V）上运行。");
        PsResult r;
        try
        {
            r = await _ps.RunAsync(Prelude + "\n" + script, timeout, lane, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn($"{action}: timeout after {timeout.TotalSeconds:0}s");
            throw new HyperVException(HvErrorKind.Timeout, $"{action}超时，请稍后刷新查看结果。");
        }
        if (!r.Ok)
        {
            var ex = ErrorMapper.ToException(action, r.Error);
            Log.Warn($"{action} failed [{ex.Kind}]: {r.Error.Trim()}");
            throw ex;
        }
        return r.Output;
    }

    // ---------------------------------------------------------------- status

    public async Task<HyperVStatus> CheckHyperVAsync(CancellationToken ct = default)
    {
        if (!_isWindows)
            return new HyperVStatus(false, new[] { "NOT_WINDOWS" }, "WinVM 需要在 Windows 10/11（专业版/企业版/教育版）上运行，并启用 Hyper-V。", false, false);

        const string script = """
$codes = New-Object System.Collections.Generic.List[string]
$elev = $false; $member = $false; $hvAdmin = $false
try {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $elev = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} catch {}
try {
  $g = (& whoami.exe /groups /fo csv 2>$null) -join "`n"
  $member = $g -match 'S-1-5-32-544'
  $hvAdmin = $g -match 'S-1-5-32-578'
} catch {}
$ok = $false; $probe = ''
if (Get-Command Get-VM -ErrorAction SilentlyContinue) {
  try { $null = Get-VM -ErrorAction Stop; $ok = $true } catch { $probe = $_.Exception.Message }
} else {
  if (Get-Module -ListAvailable Hyper-V -ErrorAction SilentlyContinue) { $codes.Add('CMDLET_BLOCKED') } else { $codes.Add('MISSING_MODULE') }
}
if (-not $ok) {
  $svc = Get-Service vmms -ErrorAction SilentlyContinue
  if (-not $svc) { $codes.Add('FEATURE_DISABLED') } elseif ($svc.Status -ne 'Running') { $codes.Add('VMMS_STOPPED') }
  if ($probe) {
    if ($probe -match 'permission|权限|access|拒绝|authoriz|授权') { $codes.Add('ACCESS_DENIED') } else { $codes.Add('PROBE_FAIL:' + $probe) }
  }
  if (-not $elev -and -not $hvAdmin) { if ($member) { $codes.Add('NOT_ELEVATED') } else { $codes.Add('NEED_PRIVILEGE') } }
}
ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ Ok = $ok; Codes = @($codes); Elevated = $elev; AdminMember = $member; HvAdmin = $hvAdmin })
""";
        try
        {
            var r = await _ps.RunAsync(script, TimeSpan.FromSeconds(60), PsLane.Query, ct).ConfigureAwait(false);
            if (!r.Ok)
                return new HyperVStatus(false, new[] { "PROBE_FAIL:" + r.Error }, InterpretCodes(new[] { "PROBE_FAIL:" + r.Error }), false, false);
            return ParseStatus(r.Output);
        }
        catch (HyperVException ex)
        {
            return new HyperVStatus(false, new[] { "POWERSHELL" }, ex.Message, false, false);
        }
        catch (TimeoutException)
        {
            return new HyperVStatus(false, new[] { "TIMEOUT" }, "检测 Hyper-V 超时。", false, false);
        }
    }

    internal static HyperVStatus ParseStatus(string json)
    {
        try
        {
            var start = json.IndexOf('{');
            using var doc = JsonDocument.Parse(start > 0 ? json[start..] : json);
            var r = doc.RootElement;
            var ok = r.TryGetProperty("Ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var codes = new List<string>();
            if (r.TryGetProperty("Codes", out var c))
            {
                if (c.ValueKind == JsonValueKind.Array)
                    codes.AddRange(c.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
                else if (c.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(c.GetString()))
                    codes.Add(c.GetString()!);
            }
            var elev = r.TryGetProperty("Elevated", out var e1) && e1.ValueKind == JsonValueKind.True;
            var member = r.TryGetProperty("AdminMember", out var e2) && e2.ValueKind == JsonValueKind.True;
            if (ok) return new HyperVStatus(true, codes, "Hyper-V 可用", elev, member);
            if (codes.Count == 0) codes.Add("UNKNOWN");
            return new HyperVStatus(false, codes, InterpretCodes(codes), elev, member);
        }
        catch (JsonException)
        {
            return new HyperVStatus(false, new[] { "UNKNOWN" }, "未能确认 Hyper-V 状态。", false, false);
        }
    }

    public static string InterpretCodes(IEnumerable<string> codes)
    {
        var tips = new List<string>();
        foreach (var p in codes)
        {
            if (p.StartsWith("PROBE_FAIL:", StringComparison.OrdinalIgnoreCase))
            {
                var (_, msg) = ErrorMapper.Map(p["PROBE_FAIL:".Length..]);
                tips.Add("Hyper-V 探测失败：" + msg);
                continue;
            }
            tips.Add(p switch
            {
                "MISSING_MODULE" => "未安装 Hyper-V PowerShell 模块（找不到 Get-VM）。",
                "CMDLET_BLOCKED" => "已安装 Hyper-V 模块，但无法加载 Get-VM（可能被组策略限制）。",
                "FEATURE_DISABLED" => "Windows 功能「Hyper-V」未启用（找不到 vmms 服务）。",
                "VMMS_STOPPED" => "Hyper-V 虚拟机管理服务（vmms）未运行。",
                "NEED_PRIVILEGE" => "当前用户既不是管理员，也不在「Hyper-V Administrators」组。",
                "NOT_ELEVATED" => "当前进程未以管理员身份运行（UAC 已过滤管理员权限）。",
                "ACCESS_DENIED" => "没有权限访问 Hyper-V（拒绝访问）。",
                "NOT_WINDOWS" => "WinVM 需要在 Windows 上运行。",
                _ => "未能确认 Hyper-V 状态。"
            });
        }
        return string.Join("\n", tips.Distinct());
    }

    public static string EnableGuidance =>
        """
        1. 确认系统为 Windows 10/11 专业版、企业版或教育版，并在 BIOS 中开启虚拟化（VT-x / AMD-V）。
        2. 以管理员身份打开 PowerShell，执行：
           Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All
        3. 按提示重启计算机。
        4. 以管理员身份运行 WinVM；或将当前用户加入「Hyper-V Administrators」组后注销再登录：
           Add-LocalGroupMember -SID S-1-5-32-578 -Member $env:USERNAME
        """;

    /// <summary>Fix steps tailored to the detected problem (a permission problem does not need the feature steps).</summary>
    public static string GuidanceFor(IReadOnlyList<string>? codes)
    {
        var c = codes ?? Array.Empty<string>();
        bool Has(string k) => c.Any(x => x.StartsWith(k, StringComparison.Ordinal));
        var installIssue = Has("MISSING_MODULE") || Has("FEATURE_DISABLED") || Has("CMDLET_BLOCKED");
        if (!installIssue && Has("VMMS_STOPPED"))
            return """
                1. Hyper-V 虚拟机管理服务（vmms）未运行。以管理员身份打开 PowerShell，执行：
                   Start-Service vmms
                2. 如果服务无法启动，请重启计算机后重试，并确认 BIOS 中已开启虚拟化（VT-x / AMD-V）。
                3. 然后点击「重新检测」。
                """;
        if (!installIssue && (Has("ACCESS_DENIED") || Has("NOT_ELEVATED") || Has("NEED_PRIVILEGE")))
            return """
                Hyper-V 已安装，只是当前没有权限访问。任选其一：
                1. 点击下方「以管理员身份重新启动」（推荐）。勾选「以后启动时自动请求管理员权限」后，以后每次启动都会自动请求。
                2. 或者把当前用户加入「Hyper-V Administrators」组，然后注销并重新登录（之后无需管理员权限）：
                   Add-LocalGroupMember -SID S-1-5-32-578 -Member $env:USERNAME
                """;
        return EnableGuidance;
    }

    public const string EnableCommand = "Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All";

    // ---------------------------------------------------------------- queries

    internal const string ListScript = """
$vms = @(Get-VM)
if ($vms.Count -eq 0) { return '[]' }
$hdd = @{}; $nic = @{}; $ip = @{}; $dvd = @{}; $cps = @{}; $fw = @{}; $vhd = @{}
foreach ($d in @(Get-VMHardDiskDrive -VM $vms -ErrorAction SilentlyContinue)) {
  $k = [string]$d.VMId; if ($d.Path -and -not $hdd.ContainsKey($k)) { $hdd[$k] = [string]$d.Path } }
foreach ($n in @(Get-VMNetworkAdapter -VM $vms -ErrorAction SilentlyContinue)) {
  $k = [string]$n.VMId
  if (-not $nic.ContainsKey($k)) { $nic[$k] = [string]$n.SwitchName }
  if (-not $ip.ContainsKey($k)) {
    $all = @($n.IPAddresses | Where-Object { $_ -and [string]$_.Length -gt 0 })
    $v4 = @($all | Where-Object { $_ -notmatch ':' })
    $pick = if ($v4.Count -gt 0) { $v4 } else { $all }
    if ($pick.Count -gt 0) { $ip[$k] = [string]($pick -join ',') }
  }
}
foreach ($d in @(Get-VMDvdDrive -VM $vms -ErrorAction SilentlyContinue)) {
  $k = [string]$d.VMId; if ($d.Path -and -not $dvd.ContainsKey($k)) { $dvd[$k] = [string]$d.Path } }
foreach ($s in @(Get-VMSnapshot -VM $vms -ErrorAction SilentlyContinue)) {
  $k = [string]$s.VMId; $cps[$k] = 1 + [int]$cps[$k] }
$g2 = @($vms | Where-Object { $_.Generation -eq 2 })
if ($g2.Count -gt 0) {
  foreach ($f in @(Get-VMFirmware -VM $g2 -ErrorAction SilentlyContinue)) { $fw[[string]$f.VMId] = ([string]$f.SecureBoot -eq 'On') } }
$paths = @($hdd.Values | Sort-Object -Unique)
if ($paths.Count -gt 0) {
  foreach ($h in @(Get-VHD -Path $paths -ErrorAction SilentlyContinue)) { $vhd[[string]$h.Path] = $h } }
$result = foreach ($v in $vms) {
  $k = [string]$v.Id
  $p = [string]$hdd[$k]
  $h = $null; if ($p) { $h = $vhd[$p] }
  [pscustomobject]@{
    Name = [string]$v.Name
    Id = $k
    State = [string]$v.State
    ProcessorCount = [int]$v.ProcessorCount
    MemoryStartup = [long]$v.MemoryStartup
    MemoryAssigned = [long]$v.MemoryAssigned
    DynamicMemory = [bool]$v.DynamicMemoryEnabled
    CpuUsage = [int]$v.CPUUsage
    UptimeSeconds = [long]$v.Uptime.TotalSeconds
    Generation = [int]$v.Generation
    Version = [string]$v.Version
    Path = [string]$v.Path
    Notes = [string]$v.Notes
    CreationTime = $v.CreationTime.ToString('o')
    SwitchName = [string]$nic[$k]
    IpAddresses = [string]$ip[$k]
    DvdPath = [string]$dvd[$k]
    VhdPath = $p
    DiskSizeBytes = $(if ($h) { [long]$h.Size } else { [long]0 })
    DiskFileBytes = $(if ($h) { [long]$h.FileSize } else { [long]0 })
    CheckpointCount = [int]$cps[$k]
    SecureBoot = [bool]$fw[$k]
  }
}
ConvertTo-Json -InputObject @($result) -Compress -Depth 3
""";

    public async Task<IReadOnlyList<VmInfo>> ListVmsAsync(CancellationToken ct = default)
    {
        if (!_isWindows) return Array.Empty<VmInfo>();
        var json = await RunAsync("读取虚拟机列表", ListScript, TimeSpan.FromSeconds(60), PsLane.Query, ct).ConfigureAwait(false);
        return VmParser.ParseVmList(json);
    }

    public async Task<IReadOnlyList<string>> ListSwitchesAsync(CancellationToken ct = default)
    {
        if (!_isWindows) return Array.Empty<string>();
        var json = await RunAsync("读取虚拟交换机", "ConvertTo-Json -Compress -InputObject @(Get-VMSwitch | ForEach-Object { [string]$_.Name })",
            TimeSpan.FromSeconds(30), PsLane.Query, ct).ConfigureAwait(false);
        return VmParser.ParseStringList(json);
    }

    public async Task<bool> VmNameExistsAsync(string name, CancellationToken ct = default)
    {
        if (!_isWindows || string.IsNullOrWhiteSpace(name)) return false;
        var o = await RunAsync("检查名称", $"if (@(Get-VM | Where-Object {{ $_.Name -eq {Q(name.Trim())} }}).Count -gt 0) {{ 'YES' }} else {{ 'NO' }}",
            TimeSpan.FromSeconds(30), PsLane.Query, ct).ConfigureAwait(false);
        return o.Trim().Equals("YES", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<CheckpointInfo>> ListCheckpointsAsync(string vm, CancellationToken ct = default)
    {
        if (!_isWindows) return Array.Empty<CheckpointInfo>();
        var script = $$"""
$vm = Resolve-WinVM {{Q(vm)}}
$cur = [string]$vm.ParentCheckpointId
$result = foreach ($c in @(Get-VMSnapshot -VM $vm -ErrorAction SilentlyContinue)) {
  [pscustomobject]@{
    Name = [string]$c.Name
    Id = [string]$c.Id
    SnapshotType = [string]$c.SnapshotType
    CreationTime = $c.CreationTime.ToString('o')
    ParentCheckpointName = [string]$c.ParentCheckpointName
    ParentId = [string]$c.ParentCheckpointId
    IsCurrent = [bool]($cur -and [string]$c.Id -eq $cur)
  }
}
ConvertTo-Json -InputObject @($result) -Compress -Depth 3
""";
        var json = await RunAsync("读取检查点", script, TimeSpan.FromSeconds(45), PsLane.Query, ct).ConfigureAwait(false);
        return VmParser.ParseCheckpoints(json);
    }

    // ---------------------------------------------------------------- create

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var s = new string(name.Trim().Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray()).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(s) ? "VM" : s;
    }

    public static void ValidateCreateRequest(CreateVmRequest req, bool checkIsoExists = true)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("请填写虚拟机名称。");
        if (req.Name.Trim().Length > 100) throw new ArgumentException("名称过长（最多 100 个字符）。");
        if (req.ProcessorCount < 1) throw new ArgumentException("CPU 至少为 1 个。");
        if (req.ProcessorCount > Math.Max(1, Environment.ProcessorCount))
            throw new ArgumentException($"CPU 不能超过主机逻辑处理器数（{Environment.ProcessorCount}）。");
        if (req.MemoryMiB < 512) throw new ArgumentException("内存至少为 512 MiB。");
        if (req.MemoryMiB > 256 * 1024) throw new ArgumentException("内存过大（上限 256 GiB）。");
        if (req.DiskGiB < 1) throw new ArgumentException("磁盘至少为 1 GiB。");
        if (req.DiskGiB > 4096) throw new ArgumentException("磁盘过大（上限 4096 GiB）。");
        if (req.Generation is not (1 or 2)) throw new ArgumentException("代数只能为 1 或 2。");
        if (req.RequireTpm)
        {
            if (req.Generation != 2) throw new ArgumentException("Windows 11 需要第 2 代虚拟机（UEFI、安全启动与 TPM）。");
            if (req.MemoryMiB < 4096) throw new ArgumentException("Windows 11 至少需要 4 GiB 内存。");
            if (req.ProcessorCount < 2) throw new ArgumentException("Windows 11 至少需要 2 个 CPU。");
            if (req.DiskGiB < 64) throw new ArgumentException("Windows 11 至少需要 64 GiB 磁盘。");
        }
        if (!string.IsNullOrWhiteSpace(req.IsoPath))
        {
            if (!req.IsoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择 .iso 镜像文件。");
            if (checkIsoExists && !File.Exists(req.IsoPath))
                throw new ArgumentException("ISO 文件不存在。");
        }
    }

    public async Task CreateVmAsync(CreateVmRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ValidateCreateRequest(req, checkIsoExists: _isWindows);
        var root = string.IsNullOrWhiteSpace(req.StoreRoot) ? StoreRoot : req.StoreRoot!;
        var name = req.Name.Trim();
        var vmDir = Path.Combine(root, SanitizeName(name));
        var vhdPath = Path.Combine(vmDir, SanitizeName(name) + ".vhdx");
        var memBytes = (long)(req.MemoryMiB / 2 * 2) * 1024L * 1024L;
        var diskBytes = (long)req.DiskGiB * 1024L * 1024L * 1024L;
        var os = req.Os == OsKind.Windows ? "windows" : "linux";
        progress?.Report("正在创建虚拟磁盘与虚拟机…");

        var script = $$"""
$name = {{Q(name)}}
$path = {{Q(vmDir)}}
$vhd = {{Q(vhdPath)}}
$iso = {{Q(req.IsoPath?.Trim())}}
$switch = {{Q(req.SwitchName?.Trim())}}
$mem = [long]{{memBytes}}
$cpu = {{req.ProcessorCount}}
$disk = [long]{{diskBytes}}
$gen = {{(req.Generation == 1 ? 1 : 2)}}
$os = '{{os}}'
$requireTpm = {{(req.RequireTpm ? "$true" : "$false")}}
$notes = {{Q(OsDetector.NotesTag(req.Os))}}
if (@(Get-VM | Where-Object { $_.Name -eq $name }).Count -gt 0) { throw "WINVM:已存在同名虚拟机「$name」。" }
if ($iso -and -not (Test-Path -LiteralPath $iso)) { throw 'WINVM:ISO 文件不存在。' }
if ($switch -and -not (Get-VMSwitch -Name $switch -ErrorAction SilentlyContinue)) { throw "WINVM:找不到虚拟交换机「$switch」。" }
if (Test-Path -LiteralPath $vhd) { throw "WINVM:磁盘文件已存在：$vhd" }
$dirExisted = Test-Path -LiteralPath $path
$vm = $null
try {
  New-Item -ItemType Directory -Force -Path $path | Out-Null
  New-VHD -Path $vhd -SizeBytes $disk -Dynamic | Out-Null
  $p = @{ Name = $name; Generation = $gen; MemoryStartupBytes = $mem; VHDPath = $vhd; Path = $path }
  if ($switch) { $p.SwitchName = $switch }
  $vm = New-VM @p
  Set-VMProcessor -VM $vm -Count $cpu
  Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
  Set-VM -VM $vm -Notes $notes
  try { Set-VM -VM $vm -AutomaticCheckpointsEnabled $false } catch {}
  if ($gen -eq 2) {
    if ($os -eq 'windows') {
      # Some hosts report no matching template; fall back to the Gen2 default template instead of failing creation.
      try { Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows } catch { try { Set-VMFirmware -VM $vm -EnableSecureBoot On } catch {} }
      try { Set-VMKeyProtector -VM $vm -NewLocalKeyProtector -ErrorAction Stop; Enable-VMTPM -VM $vm -ErrorAction Stop } catch {
        if ($requireTpm) { throw "WINVM:无法为 Windows 11 启用虚拟 TPM（主机不支持本地密钥保护器）：$($_.Exception.Message)" }
      }
      if ($requireTpm -and [string](Get-VMFirmware -VM $vm).SecureBoot -ne 'On') { throw 'WINVM:无法为 Windows 11 启用安全启动。' }
    } else {
      Set-VMFirmware -VM $vm -EnableSecureBoot Off
    }
  }
  if ($iso) {
    $d = Get-VMDvdDrive -VM $vm | Select-Object -First 1
    if ($d) { Set-VMDvdDrive -VMDvdDrive $d -Path $iso; $d = Get-VMDvdDrive -VM $vm | Select-Object -First 1 }
    else { $d = Add-VMDvdDrive -VM $vm -Path $iso -Passthru }
    if ($gen -eq 2 -and $d) { Set-VMFirmware -VM $vm -FirstBootDevice $d }
  }
} catch {
  $err = $_
  if ($vm) { Remove-VM -VM $vm -Force -ErrorAction SilentlyContinue }
  if (Test-Path -LiteralPath $vhd) { Remove-Item -LiteralPath $vhd -Force -ErrorAction SilentlyContinue }
  if (-not $dirExisted -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
  throw $err
}
'CREATED:' + [string]$vm.Id
""";
        await RunAsync("创建虚拟机", script, TimeSpan.FromMinutes(5), PsLane.Action, ct).ConfigureAwait(false);
        progress?.Report("创建完成");
    }

    // ---------------------------------------------------------------- power

    private Task VmCmd(string action, string vm, string body, TimeSpan? timeout = null) =>
        RunAsync(action, $"$vm = Resolve-WinVM {Q(vm)}\n{body}\n'OK'", timeout ?? TimeSpan.FromMinutes(3), PsLane.Action);

    public Task StartAsync(string vm) => VmCmd("启动", vm, "Start-VM -VM $vm");
    /// <summary>Graceful guest shutdown via the shutdown integration service.</summary>
    public Task StopAsync(string vm) => VmCmd("关机", vm, "Stop-VM -VM $vm -Force");
    /// <summary>Power off immediately (like pulling the plug).</summary>
    public Task TurnOffAsync(string vm) => VmCmd("强制关闭", vm, "Stop-VM -VM $vm -TurnOff -Force");
    public Task PauseAsync(string vm) => VmCmd("暂停", vm, "Suspend-VM -VM $vm");
    public Task ResumeAsync(string vm) => VmCmd("恢复", vm, "Resume-VM -VM $vm");
    /// <summary>Guest-initiated reboot (needs integration services).</summary>
    public Task RestartAsync(string vm) => VmCmd("重启", vm, "Restart-VM -VM $vm -Type Reboot -Force");
    /// <summary>Hard reset (like pressing the reset button).</summary>
    public Task ResetAsync(string vm) => VmCmd("重置", vm, "Restart-VM -VM $vm -Type Reset -Force");

    public Task DeleteAsync(string vm, bool deleteFiles)
    {
        var body = $$"""
$store = {{Q(StoreRoot.TrimEnd('\\'))}}
$deleteFiles = ${{(deleteFiles ? "true" : "false")}}
$disks = @(Get-VMHardDiskDrive -VM $vm -ErrorAction SilentlyContinue | Where-Object { $_.Path } | ForEach-Object { [string]$_.Path })
$chain = New-Object System.Collections.Generic.List[string]
foreach ($d in $disks) {
  $p = $d; $guard = 0
  while ($p -and $guard -lt 64) {
    $chain.Add($p); $guard++
    try { $p = [string](Get-VHD -Path $p -ErrorAction Stop).ParentPath } catch { $p = $null }
  }
}
if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force -ErrorAction SilentlyContinue }
$vmPath = [string]$vm.Path
Remove-VM -VM $vm -Force
if ($deleteFiles) {
  foreach ($f in $chain) {
    for ($i = 0; $i -lt 10 -and (Test-Path -LiteralPath $f); $i++) {
      try { Remove-Item -LiteralPath $f -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
    }
  }
  $owned = $null
  foreach ($p in @($vmPath) + $disks) {
    if ($p -and $p.StartsWith($store + '\', [StringComparison]::OrdinalIgnoreCase)) {
      $first = $p.Substring($store.Length + 1).Split('\')[0]
      if ($first) { $owned = Join-Path $store $first; break }
    }
  }
  if ($owned -and (Test-Path -LiteralPath $owned)) {
    $others = @(Get-VM | Where-Object { $_.Path -and ($_.Path + '\').StartsWith($owned + '\', [StringComparison]::OrdinalIgnoreCase) })
    if ($others.Count -eq 0) {
      for ($i = 0; $i -lt 10 -and (Test-Path -LiteralPath $owned); $i++) {
        try { Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
      }
    }
  }
}
""";
        return VmCmd("删除虚拟机", vm, body, TimeSpan.FromMinutes(4));
    }

    // ---------------------------------------------------------------- configuration

    public Task UpdateSettingsAsync(string vm, int? cpu, long? memoryBytes, string? isoPath, string? switchName = null)
    {
        var lines = new List<string> { "if ($vm.State -ne 'Off') { throw 'WINVM:请先关机后再修改设置。' }" };
        if (cpu is int c)
        {
            if (c < 1) throw new ArgumentException("CPU 至少为 1 个。");
            lines.Add($"Set-VMProcessor -VM $vm -Count {c}");
        }
        if (memoryBytes is long m)
        {
            if (m < 512L * 1024 * 1024) throw new ArgumentException("内存至少为 512 MiB。");
            const long twoMiB = 2L * 1024 * 1024;
            m = (m + twoMiB / 2) / twoMiB * twoMiB; // Hyper-V requires multiples of 2 MiB
            // 1.5: keep the dynamic memory mode chosen in 高级设置; widen its range if the new startup value is outside it
            lines.Add($"$mb = [long]{m}");
            lines.Add("$mm = Get-VMMemory -VM $vm");
            lines.Add("if ($mm.DynamicMemoryEnabled) { if ($mb -gt $mm.Maximum) { Set-VMMemory -VM $vm -MaximumBytes $mb }; if ($mb -lt $mm.Minimum) { Set-VMMemory -VM $vm -MinimumBytes $mb } }");
            lines.Add("Set-VMMemory -VM $vm -StartupBytes $mb");
        }
        if (isoPath != null)
        {
            if (isoPath.Length == 0)
            {
                lines.Add("Get-VMDvdDrive -VM $vm | ForEach-Object { Set-VMDvdDrive -VMDvdDrive $_ -Path $null }");
            }
            else
            {
                if (_isWindows && !File.Exists(isoPath)) throw new ArgumentException("ISO 文件不存在。");
                lines.Add($"$iso = {Q(isoPath)}");
                lines.Add("$d = Get-VMDvdDrive -VM $vm | Select-Object -First 1");
                lines.Add("if ($d) { Set-VMDvdDrive -VMDvdDrive $d -Path $iso } else { Add-VMDvdDrive -VM $vm -Path $iso }");
            }
        }
        if (switchName != null)
        {
            lines.Add("$nic = Get-VMNetworkAdapter -VM $vm | Select-Object -First 1");
            if (switchName.Length == 0)
                lines.Add("if ($nic) { Disconnect-VMNetworkAdapter -VMNetworkAdapter $nic }");
            else
            {
                lines.Add($"$sw = {Q(switchName)}");
                lines.Add("if (-not (Get-VMSwitch -Name $sw -ErrorAction SilentlyContinue)) { throw \"WINVM:找不到虚拟交换机「$sw」。\" }");
                lines.Add("if ($nic) { Connect-VMNetworkAdapter -VMNetworkAdapter $nic -SwitchName $sw } else { Add-VMNetworkAdapter -VM $vm -SwitchName $sw }");
            }
        }
        return VmCmd("保存设置", vm, string.Join("\n", lines), TimeSpan.FromMinutes(1));
    }

    public async Task RenameAsync(string vm, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("请填写新名称。");
        newName = newName.Trim();
        if (newName.Length > 100) throw new ArgumentException("名称过长（最多 100 个字符）。");
        var body = $$"""
$new = {{Q(newName)}}
if ($vm.Name -ceq $new) { throw 'WINVM:新名称与当前名称相同。' }
if (@(Get-VM | Where-Object { $_.Name -eq $new -and $_.Id -ne $vm.Id }).Count -gt 0) { throw "WINVM:已存在同名虚拟机「$new」。" }
Rename-VM -VM $vm -NewName $new
""";
        await VmCmd("重命名", vm, body, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
    }

    public Task ResizeVhdAsync(string vm, int newSizeGiB)
    {
        if (newSizeGiB < 1) throw new ArgumentException("磁盘至少为 1 GiB。");
        if (newSizeGiB > 4096) throw new ArgumentException("磁盘过大（上限 4096 GiB）。");
        var newBytes = (long)newSizeGiB * 1024L * 1024L * 1024L;
        var body = $$"""
if ($vm.State -ne 'Off') { throw 'WINVM:请先关机后再扩容磁盘。' }
if (@(Get-VMSnapshot -VM $vm -ErrorAction SilentlyContinue).Count -gt 0) { throw 'WINVM:存在检查点时无法扩容磁盘，请先删除检查点。' }
$drive = Get-VMHardDiskDrive -VM $vm | Where-Object { $_.Path } | Select-Object -First 1
if (-not $drive) { throw 'WINVM:未找到虚拟硬盘。' }
$cur = [long](Get-VHD -Path $drive.Path).Size
$new = [long]{{newBytes}}
if ($new -le $cur) { throw ("WINVM:新容量必须大于当前容量（当前 {0:0.##} GiB）。" -f ($cur / 1GB)) }
Resize-VHD -Path $drive.Path -SizeBytes $new
""";
        return VmCmd("扩容磁盘", vm, body, TimeSpan.FromMinutes(5));
    }

    // ---------------------------------------------------------------- checkpoints

    public async Task<string> CreateCheckpointAsync(string vm, string? checkpointName)
    {
        var nameArg = string.IsNullOrWhiteSpace(checkpointName) ? "" : $" -SnapshotName {Q(checkpointName.Trim())}";
        var o = await RunAsync("创建检查点",
            $$"""
$vm = Resolve-WinVM {{Q(vm)}}
{{WaitMergeScript}}
$before = @(Get-VMSnapshot -VM $vm | ForEach-Object { [string]$_.Id })
$wv = @()
$cp = Checkpoint-VM -VM $vm{{nameArg}} -Passthru -WarningVariable +wv -WarningAction SilentlyContinue
if (-not $cp) {
  $cp = Get-VMSnapshot -VMName $vm.Name | Where-Object { $before -notcontains [string]$_.Id -and [string]$_.VMId -eq [string]$vm.Id } |
        Sort-Object CreationTime | Select-Object -Last 1
}
if (-not $cp) { throw ('WINVM:Hyper-V 没有创建检查点（未返回错误）。' + (($wv | ForEach-Object { [string]$_ }) -join ' ')) }
[string]$cp.Id
""",
            TimeSpan.FromMinutes(5), PsLane.Action).ConfigureAwait(false);
        return o.Trim();
    }

    /// <summary>Waits (max 2 min) for a background disk merge left by a checkpoint delete/apply to finish.
    /// Creating a checkpoint while Hyper-V is still merging crashed vmms.exe on Windows build 27902 (1.5 PC e2e).</summary>
    internal const string WaitMergeScript = """
$mergeEnd = (Get-Date).AddMinutes(2)
while ((Get-Date) -lt $mergeEnd -and ((@($vm.OperationalStatus) | ForEach-Object { [string]$_ }) -contains 'MergingDisks')) {
  Start-Sleep -Milliseconds 500; $vm = Get-VM -Id $vm.Id
}
""";

    private static string FindCheckpoint(string id) => $$"""
$cp = Get-VMSnapshot -VM $vm | Where-Object { [string]$_.Id -eq {{Q(id)}} -or $_.Name -eq {{Q(id)}} } | Select-Object -First 1
if (-not $cp) { throw 'WINVM:找不到该检查点，请刷新后重试。' }
""";

    public Task ApplyCheckpointAsync(string vm, string checkpointId) =>
        VmCmd("应用检查点", vm, WaitMergeScript + FindCheckpoint(checkpointId) + "Restore-VMSnapshot -VMSnapshot $cp -Confirm:$false", TimeSpan.FromMinutes(5));

    public Task DeleteCheckpointAsync(string vm, string checkpointId) =>
        VmCmd("删除检查点", vm, WaitMergeScript + FindCheckpoint(checkpointId) + "Remove-VMSnapshot -VMSnapshot $cp -Confirm:$false", TimeSpan.FromMinutes(5));

    // ---------------------------------------------------------------- console

    public Task OpenConsoleAsync(string vm)
    {
        if (!_isWindows) throw new HyperVException(HvErrorKind.NotWindows, "仅 Windows 可打开虚拟机控制台。");
        var exe = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe"),
            @"C:\Program Files\Hyper-V\vmconnect.exe"
        }.FirstOrDefault(File.Exists);
        if (exe == null)
            throw new HyperVException(HvErrorKind.NotFound, "未找到 vmconnect.exe。请在「Windows 功能」中启用 Hyper-V 管理工具。");
        var args = Guid.TryParse(vm, out _)
            ? $"localhost -G {vm}"
            : "localhost \"" + vm.Replace("\"", "\\\"") + "\"";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new HyperVException(HvErrorKind.AccessDenied, "已取消：打开控制台需要管理员权限。");
        }
        Log.Info($"vmconnect {args}");
        return Task.CompletedTask;
    }

    public Task OpenRdpAsync(string host, int port = 3389)
    {
        if (!_isWindows) throw new HyperVException(HvErrorKind.NotWindows, "仅 Windows 可启动远程桌面。");
        if (string.IsNullOrWhiteSpace(host))
            throw new HyperVException(HvErrorKind.Validation, "来宾尚未报告 IP，无法用 RDP 连接。请先启动虚拟机并等待网络就绪，或使用「连接控制台」。");
        var mstsc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");
        if (!File.Exists(mstsc))
            throw new HyperVException(HvErrorKind.NotFound, "未找到 mstsc.exe。");
        var arg = port == 3389 ? host : $"{host}:{port}";
        Process.Start(new ProcessStartInfo { FileName = mstsc, Arguments = $"/v:{arg}", UseShellExecute = true });
        Log.Info($"mstsc /v:{arg}");
        return Task.CompletedTask;
    }

    public Task OpenSshAsync(string host, string? user = null, int port = 22)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new HyperVException(HvErrorKind.Validation, "来宾尚未报告 IP，无法用 SSH 连接。");
        var target = string.IsNullOrWhiteSpace(user) ? host : $"{user}@{host}";
        var args = port == 22 ? target : $"-p {port} {target}";
        // Prefer Windows OpenSSH client; fall back to launching a console with the command for copy.
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        if (!File.Exists(ssh))
            ssh = "ssh";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ssh,
                Arguments = args,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            throw new HyperVException(HvErrorKind.NotFound, "无法启动 ssh。请安装 OpenSSH 客户端，或复制连接信息后自行连接。", ex.Message);
        }
        Log.Info($"ssh {args}");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- export / import / clone

    /// <summary>Helpers for long operations run in an isolated PowerShell process. Progress markers go straight to
    /// stdout (<c>@@TOTAL bytes</c>, <c>@@PHASE name folder</c>); cancellation is a flag file polled by the script,
    /// which then stops the Hyper-V job and removes everything it created.</summary>
    internal const string LongOpPrelude = """
function Emit([string]$s) { [Console]::Out.WriteLine($s); [Console]::Out.Flush() }
function Test-WinVMCancel { if (Test-Path -LiteralPath $flag) { throw 'WINVM_CANCELED' } }
function Wait-WinVMJob($job) {
  while ($job.State -eq 'NotStarted' -or $job.State -eq 'Running') {
    if (Test-Path -LiteralPath $flag) {
      try { Stop-Job -Job $job } catch {}
      for ($i = 0; $i -lt 600 -and ($job.State -eq 'Running' -or $job.State -eq 'Stopping' -or $job.State -eq 'NotStarted'); $i++) { Start-Sleep -Milliseconds 200 }
      throw 'WINVM_CANCELED'
    }
    Start-Sleep -Milliseconds 300
  }
  if ($job.State -eq 'Failed') {
    $reason = $job.ChildJobs | ForEach-Object { $_.JobStateInfo.Reason } | Where-Object { $_ } | Select-Object -First 1
    if (-not $reason) { $reason = $job.JobStateInfo.Reason }
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    if ($reason) { throw $reason } else { throw 'WINVM:操作失败（Hyper-V 作业失败）。' }
  }
  $res = Receive-Job -Job $job -ErrorAction Stop
  Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
  return $res
}
function Get-WinVMBytes($v) {
  $t = [long]0
  foreach ($d in @(Get-VMHardDiskDrive -VM $v -ErrorAction SilentlyContinue | Where-Object { $_.Path })) {
    $p = [string]$d.Path; $g = 0
    while ($p -and $g -lt 64) {
      if (Test-Path -LiteralPath $p) { $t += (Get-Item -LiteralPath $p).Length }
      $g++
      try { $p = [string](Get-VHD -Path $p -ErrorAction Stop).ParentPath } catch { $p = $null }
    }
  }
  if ($t -le 0) { $t = [long]1MB }
  return $t
}
function Assert-WinVMSpace([string]$path, [long]$need) {
  try {
    $root = [System.IO.Path]::GetPathRoot($path)
    $drv = New-Object System.IO.DriveInfo($root)
    if ($drv.AvailableFreeSpace -lt $need) {
      throw ("WINVM:磁盘空间不足：需要约 {0:0.#} GiB，{1} 仅剩 {2:0.#} GiB。" -f ($need / 1GB), $root, ($drv.AvailableFreeSpace / 1GB))
    }
  } catch [System.ArgumentException] {}
}
function Import-WinVMCopy([string]$src, [string]$dest, [string]$newName) {
  $vmcx = @(Get-ChildItem -LiteralPath $src -Recurse -Filter '*.vmcx' -File -ErrorAction SilentlyContinue | Where-Object { $_.Directory.Name -eq 'Virtual Machines' })
  if ($vmcx.Count -eq 0) { throw 'WINVM:所选文件夹中没有找到导出的虚拟机（缺少 Virtual Machines\*.vmcx）。请选择导出生成的虚拟机文件夹。' }
  if ($vmcx.Count -gt 1) { throw "WINVM:所选文件夹中包含 $($vmcx.Count) 台虚拟机，请选择其中一台虚拟机的文件夹。" }
  if (@(Get-VM | Where-Object { $_.Name -eq $newName }).Count -gt 0) { throw "WINVM:已存在同名虚拟机「$newName」。" }
  if (Test-Path -LiteralPath $dest) { throw "WINVM:目标文件夹已存在：$dest" }
  $size = [long](Get-ChildItem -LiteralPath $src -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
  if ($size -le 0) { $size = [long]1MB }
  Assert-WinVMSpace $dest ([long]($size * 1.05))
  $vhdDir = Join-Path $dest 'Virtual Hard Disks'
  New-Item -ItemType Directory -Force -Path $vhdDir | Out-Null
  try {
    Test-WinVMCancel
    $rep = Compare-VM -Path $vmcx[0].FullName -Copy -GenerateNewId -VirtualMachinePath $dest -VhdDestinationPath $vhdDir -SnapshotFilePath $dest -SmartPagingFilePath $dest
    $fixed = $false
    foreach ($inc in @($rep.Incompatibilities)) {
      # 33012: the virtual switch does not exist on this host -> import disconnected instead of failing.
      if ($inc.MessageId -eq 33012) { try { $inc.Source | Disconnect-VMNetworkAdapter; $fixed = $true } catch {} }
    }
    if ($fixed) { $rep = Compare-VM -CompatibilityReport $rep }
    if (@($rep.Incompatibilities).Count -gt 0) {
      $msgs = (@($rep.Incompatibilities) | ForEach-Object { [string]$_.Message }) -join '；'
      throw "WINVM:该虚拟机与此主机不兼容：$msgs"
    }
    Emit ('@@TOTAL ' + $size)
    Emit ('@@PHASE import ' + $dest)
    $job = Import-VM -CompatibilityReport $rep -AsJob
    $res = Wait-WinVMJob $job
    $new = @($res) | Where-Object { $_ -and $_.GetType().Name -eq 'VirtualMachine' } | Select-Object -First 1
    if (-not $new) { $new = Get-VM -Id $rep.VM.Id -ErrorAction SilentlyContinue }
    if (-not $new) { throw 'WINVM:导入后未找到新虚拟机，请刷新列表。' }
    Rename-VM -VM $new -NewName $newName
    return [string]$new.Id
  } catch {
    $err = $_
    # Roll back: only VMs whose configuration lives inside the folder created above (never pre-existing ones).
    $prefix = $dest.TrimEnd('\') + '\'
    foreach ($v in @(Get-VM | Where-Object { $_.Path -and ($_.Path.TrimEnd('\') + '\').StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })) {
      try { Stop-VM -VM $v -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -VM $v -Force -ErrorAction Stop } catch {}
    }
    for ($i = 0; $i -lt 10 -and (Test-Path -LiteralPath $dest); $i++) {
      try { Remove-Item -LiteralPath $dest -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
    }
    throw $err
  }
}
""";

    /// <summary>Unique destination folder for an imported/cloned VM inside the store.</summary>
    internal static string UniqueFolder(string root, string name, Func<string, bool>? exists = null)
    {
        exists ??= p => Directory.Exists(p) || File.Exists(p);
        var baseName = SanitizeName(name);
        var path = Path.Combine(root, baseName);
        for (var i = 2; exists(path) && i < 1000; i++) path = Path.Combine(root, $"{baseName} ({i})");
        return path;
    }

    internal static string CancelFlagPath() =>
        Path.Combine(Path.GetTempPath(), "WinVM", "cancel-" + Guid.NewGuid().ToString("N") + ".flag");

    /// <summary>Per-op mapping from script phases to (start, span) of the overall progress and the stage text.</summary>
    internal sealed record Phase(double Start, double Span, string Stage);

    internal static long DirectorySize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long t = 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { t += f.Length; } catch { }
            }
            return t;
        }
        catch { return 0; }
    }

    private async Task<string> RunLongAsync(string action, string body, string flag, IReadOnlyDictionary<string, Phase> phases,
        string resultPrefix, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        if (!_isWindows)
            throw new HyperVException(HvErrorKind.NotWindows, "WinVM 需要在 Windows（Hyper-V）上运行。");
        Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
        var state = new LongOpState();
        void OnLine(string line)
        {
            if (line.StartsWith("@@TOTAL ", StringComparison.Ordinal) && long.TryParse(line[8..].Trim(), out var t)) state.Total = t;
            else if (line.StartsWith("@@PHASE ", StringComparison.Ordinal))
            {
                var rest = line[8..];
                var sp = rest.IndexOf(' ');
                var name = sp < 0 ? rest : rest[..sp];
                if (phases.TryGetValue(name, out var ph))
                {
                    state.Phase = ph;
                    state.Dir = sp < 0 ? null : rest[(sp + 1)..].Trim();
                    progress?.Report(new OpProgress(ph.Start, ph.Stage));
                }
            }
        }
        using var killCts = new CancellationTokenSource();
        using var reg = ct.Register(() =>
        {
            try { File.WriteAllText(flag, "cancel"); } catch { }
            progress?.Report(new OpProgress(-1, "正在取消并清理…"));
            try { killCts.CancelAfter(TimeSpan.FromSeconds(120)); } catch (ObjectDisposedException) { }
        });
        var poller = Task.Run(async () =>
        {
            while (!state.Done)
            {
                try { await Task.Delay(500, killCts.Token).ConfigureAwait(false); } catch { }
                if (state.Done || ct.IsCancellationRequested) continue;
                if (state.Phase is { } ph && state.Dir is { } d && state.Total > 0)
                {
                    var f = Math.Clamp(DirectorySize(d) / (double)state.Total, 0, 0.99);
                    progress?.Report(new OpProgress(ph.Start + ph.Span * f, ph.Stage));
                }
            }
        });
        PsResult r;
        var sw = Stopwatch.StartNew();
        try
        {
            r = await _ps.RunIsolatedAsync(Prelude + "\n$flag = " + Q(flag) + "\n" + LongOpPrelude + "\n" + body,
                TimeSpan.FromHours(12), OnLine, killCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"{action}: process killed after cancel grace period");
            throw new OperationCanceledException($"{action}已取消。");
        }
        catch (TimeoutException)
        {
            throw new HyperVException(HvErrorKind.Timeout, $"{action}超时。");
        }
        finally
        {
            state.Done = true;
            try { File.Delete(flag); } catch { }
        }
        Log.Info($"{action}: {(r.Ok ? "ok" : "failed")} in {sw.ElapsedMilliseconds} ms");
        if (!r.Ok)
        {
            if (r.Error.Contains("WINVM_CANCELED", StringComparison.Ordinal) || ct.IsCancellationRequested)
                throw new OperationCanceledException($"{action}已取消。");
            var ex = ErrorMapper.ToException(action, r.Error);
            Log.Warn($"{action} failed [{ex.Kind}]: {r.Error.Trim()}");
            throw ex;
        }
        ct.ThrowIfCancellationRequested();
        var result = r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(resultPrefix, StringComparison.Ordinal));
        progress?.Report(new OpProgress(1, "完成"));
        return result == null ? "" : result[resultPrefix.Length..].Trim();
    }

    private sealed class LongOpState
    {
        public volatile bool Done;
        public long Total;
        public Phase? Phase;
        public string? Dir;
    }

    public Task<string> ExportVmAsync(string vm, string parentFolder, IProgress<OpProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parentFolder)) throw new ArgumentException("请选择导出位置。");
        var body = $$"""
$vm = Resolve-WinVM {{Q(vm)}}
$parent = {{Q(parentFolder.TrimEnd('\\'))}}
if ($vm.State -ne 'Off') { throw 'WINVM:请先关闭虚拟机再导出。' }
$target = Join-Path $parent $vm.Name
if (Test-Path -LiteralPath $target) { throw "WINVM:导出位置已存在同名文件夹：$target" }
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$bytes = Get-WinVMBytes $vm
Assert-WinVMSpace $parent ([long]($bytes * 1.05))
Test-WinVMCancel
Emit ('@@TOTAL ' + $bytes)
Emit ('@@PHASE export ' + $target)
try {
  $job = Export-VM -VM $vm -Path $parent -AsJob
  Wait-WinVMJob $job | Out-Null
} catch {
  $err = $_
  for ($i = 0; $i -lt 10 -and (Test-Path -LiteralPath $target); $i++) {
    try { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
  }
  throw $err
}
'EXPORTED:' + $target
""";
        return RunLongAsync("导出虚拟机", body, CancelFlagPath(),
            new Dictionary<string, Phase> { ["export"] = new(0, 1, "正在导出…") }, "EXPORTED:", progress, ct);
    }

    public async Task<string> ImportVmAsync(string sourceFolder, string newName, IProgress<OpProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder)) throw new ArgumentException("请选择要导入的文件夹。");
        newName = ValidateNewName(newName);
        var dest = UniqueFolder(StoreRoot, newName);
        var body = $$"""
$src = {{Q(sourceFolder.TrimEnd('\\'))}}
if (-not (Test-Path -LiteralPath $src)) { throw 'WINVM:所选文件夹不存在。' }
$id = Import-WinVMCopy $src {{Q(dest)}} {{Q(newName)}}
'IMPORTED:' + $id
""";
        return await RunLongAsync("导入虚拟机", body, CancelFlagPath(),
            new Dictionary<string, Phase> { ["import"] = new(0, 1, "正在复制虚拟机文件…") }, "IMPORTED:", progress, ct).ConfigureAwait(false);
    }

    public async Task<string> CloneVmAsync(string vm, string newName, IProgress<OpProgress>? progress = null, CancellationToken ct = default)
    {
        newName = ValidateNewName(newName);
        var dest = UniqueFolder(StoreRoot, newName);
        var tmp = Path.Combine(StoreRoot, ".winvm-tmp", Guid.NewGuid().ToString("N"));
        var body = $$"""
$vm = Resolve-WinVM {{Q(vm)}}
$newName = {{Q(newName)}}
$tmpRoot = {{Q(Path.Combine(StoreRoot, ".winvm-tmp"))}}
$tmp = {{Q(tmp)}}
if ($vm.State -ne 'Off') { throw 'WINVM:请先关闭虚拟机再克隆。' }
if (@(Get-VM | Where-Object { $_.Name -eq $newName }).Count -gt 0) { throw "WINVM:已存在同名虚拟机「$newName」。" }
$bytes = Get-WinVMBytes $vm
Assert-WinVMSpace $tmpRoot ([long]($bytes * 2.1))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
  Test-WinVMCancel
  $target = Join-Path $tmp $vm.Name
  Emit ('@@TOTAL ' + $bytes)
  Emit ('@@PHASE export ' + $target)
  $job = Export-VM -VM $vm -Path $tmp -AsJob
  Wait-WinVMJob $job | Out-Null
  Test-WinVMCancel
  $id = Import-WinVMCopy $target {{Q(dest)}} $newName
  'CLONED:' + $id
} finally {
  for ($i = 0; $i -lt 10 -and (Test-Path -LiteralPath $tmp); $i++) {
    try { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
  }
  if ((Test-Path -LiteralPath $tmpRoot) -and -not (Get-ChildItem -LiteralPath $tmpRoot -Force)) { Remove-Item -LiteralPath $tmpRoot -Force -ErrorAction SilentlyContinue }
}
""";
        return await RunLongAsync("克隆虚拟机", body, CancelFlagPath(), new Dictionary<string, Phase>
        {
            ["export"] = new(0, 0.5, "正在复制源虚拟机（1/2）…"),
            ["import"] = new(0.5, 0.5, "正在生成克隆（2/2）…")
        }, "CLONED:", progress, ct).ConfigureAwait(false);
    }

    /// <summary>Diagnostics only (<c>--debug-inflight</c>): occupies the shared PowerShell host.</summary>
    internal Task DebugSleepAsync(int seconds) =>
        RunAsync("诊断", $"Start-Sleep -Seconds {Math.Clamp(seconds, 1, 600)}; 'OK'", TimeSpan.FromSeconds(seconds + 30), PsLane.Query);

    internal static string ValidateNewName(string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("请填写虚拟机名称。");
        newName = newName.Trim();
        if (newName.Length > 100) throw new ArgumentException("名称过长（最多 100 个字符）。");
        return newName;
    }
}
