using WinVM.Models;
using WinVM.Services;
using Xunit;

namespace WinVM.Tests;

public class AdvancedScriptTests
{
    [Fact]
    public void Apply_body_orders_nested_and_memory_and_quotes_notes()
    {
        var body = HyperVService.AdvancedApplyBody(new AdvancedSettingsChange
        {
            DynamicMemory = false, NestedVirtualization = true, Tpm = true, SecureBoot = true, SecureBootTemplate = SecureBootTemplates.Windows,
            BootOrder = new[] { 2, 0, 1 }, AutoStartAction = AutoStartActions.Nothing, AutoStopAction = AutoStopActions.ShutDown, Notes = "it's 中文",
            EnhancedSessionHvSocket = true
        }, 3);
        Assert.Contains("if ($vm.State -ne 'Off')", body);
        Assert.True(body.IndexOf("-DynamicMemoryEnabled $false", StringComparison.Ordinal) < body.IndexOf("-ExposeVirtualizationExtensions $true", StringComparison.Ordinal),
            "dynamic memory must be disabled before nested virtualization is enabled");
        Assert.Contains("Set-VMKeyProtector -VM $vm -NewLocalKeyProtector", body);
        Assert.Contains("@($kp).Length -le 4", body);
        Assert.Contains("Enable-VMTPM", body);
        Assert.Contains("-SecureBootTemplate 'MicrosoftWindows'", body);
        Assert.Contains("if ($o.Count -ne 3)", body);
        Assert.Contains("-BootOrder @($o[2], $o[0], $o[1])", body);
        Assert.Contains("-AutomaticStartAction Nothing", body);
        Assert.Contains("-AutomaticStopAction ShutDown", body);
        Assert.Contains("$user = 'it''s 中文'", body);
        Assert.Contains(@"\[WinVM os=(windows|linux)\]", body);
        Assert.Contains("-EnhancedSessionTransportType HvSocket", body);
    }

    [Fact]
    public void Apply_body_for_notes_only_has_no_off_check_and_no_memory_changes()
    {
        var body = HyperVService.AdvancedApplyBody(new AdvancedSettingsChange { Notes = "x" }, 0);
        Assert.DoesNotContain("State -ne 'Off'", body);
        Assert.DoesNotContain("Set-VMMemory", body);
        Assert.DoesNotContain("Set-VMProcessor", body);
        var off = HyperVService.AdvancedApplyBody(new AdvancedSettingsChange { Tpm = false, SecureBoot = false }, 0);
        Assert.Contains("Disable-VMTPM", off);
        Assert.Contains("-EnableSecureBoot Off", off);
    }

    [Fact]
    public async Task Real_service_reads_and_applies_via_powershell()
    {
        var ps = new FakePsRunner();
        ps.Responder = s => s.Contains("Get-VMSecurity")
            ? new PsResult(true, """{"Generation":2,"State":"Off","SecureBoot":"Off","Boot":[{"Index":0,"Kind":"Disk"},{"Index":1,"Kind":"Network"}],"Startup":1073741824,"Minimum":536870912,"Maximum":1099511627776,"Buffer":20}""", "")
            : new PsResult(true, "OK", "");
        var hv = new HyperVService(ps, isWindows: true, storeRoot: @"C:\VMs");
        var a = await hv.GetAdvancedSettingsAsync("vm1");
        Assert.Equal(2, a.BootOrder.Count);
        await hv.ApplyAdvancedSettingsAsync("vm1", new AdvancedSettingsChange { BootOrder = new[] { 1, 0 } });
        Assert.Contains(ps.Calls, c => c.Script.Contains("-BootOrder @($o[1], $o[0])") && c.Lane == PsLane.Action);
        var n = ps.Calls.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => hv.ApplyAdvancedSettingsAsync("vm1", new AdvancedSettingsChange { BootOrder = new[] { 0, 0 } }));
        Assert.Equal(n + 1, ps.Calls.Count); // only the read; the invalid change never reaches PowerShell
        await hv.ApplyAdvancedSettingsAsync("vm1", new AdvancedSettingsChange());
        Assert.Equal(n + 1, ps.Calls.Count);
    }

    [Fact]
    public async Task Switch_scripts_and_default_switch_guard()
    {
        var ps = new FakePsRunner();
        var hv = new HyperVService(ps, isWindows: true, storeRoot: @"C:\VMs");
        await hv.CreateSwitchAsync("Lab 内网", SwitchKind.Private);
        Assert.Contains("New-VMSwitch -Name $n -SwitchType Private", ps.Calls[^1].Script);
        Assert.Contains("Test-WinVMSwitchName $n", ps.Calls[^1].Script);
        await hv.CreateSwitchAsync("Ext", SwitchKind.External, "以太网", allowManagementOs: false);
        Assert.Contains("-NetAdapterName '以太网' -AllowManagementOS $false", ps.Calls[^1].Script);
        await Assert.ThrowsAsync<ArgumentException>(() => hv.CreateSwitchAsync("Ext", SwitchKind.External, null));
        await Assert.ThrowsAsync<ArgumentException>(() => hv.CreateSwitchAsync("  ", SwitchKind.Private));
        await Assert.ThrowsAsync<ArgumentException>(() => hv.CreateSwitchAsync("a*b", SwitchKind.Private));
        await hv.RenameSwitchAsync("11111111-1111-1111-1111-111111111111", "New");
        Assert.Contains("Rename-VMSwitch -VMSwitch $s -NewName $n", ps.Calls[^1].Script);
        await hv.DeleteSwitchAsync("11111111-1111-1111-1111-111111111111");
        var del = ps.Calls[^1].Script;
        Assert.Contains("正被以下虚拟机使用", del);
        Assert.True(del.IndexOf("$users.Count -gt 0", StringComparison.Ordinal) < del.IndexOf("Remove-VMSwitch", StringComparison.Ordinal));
        var before = ps.Calls.Count;
        await Assert.ThrowsAsync<HyperVException>(() => hv.DeleteSwitchAsync(VSwitchInfo.DefaultSwitchId));
        await Assert.ThrowsAsync<HyperVException>(() => hv.DeleteSwitchAsync("{C08CB7B8-9B3C-408E-8E30-5E16A3AEB444}"));
        await Assert.ThrowsAsync<HyperVException>(() => hv.RenameSwitchAsync(VSwitchInfo.DefaultSwitchId, "x"));
        Assert.Equal(before, ps.Calls.Count); // the Default Switch is never sent to PowerShell
        await hv.SetVmSwitchAsync("vm1", "Lab");
        Assert.Contains("Connect-VMNetworkAdapter -VMNetworkAdapter $nic -SwitchName $sw", ps.Calls[^1].Script);
        await hv.SetVmSwitchAsync("vm1", null);
        Assert.Contains("Disconnect-VMNetworkAdapter", ps.Calls[^1].Script);
    }

    [Fact]
    public async Task Create_script_enforces_tpm_for_windows11_and_validation_minimums()
    {
        var ps = new FakePsRunner();
        var hv = new HyperVService(ps, isWindows: true, storeRoot: @"C:\VMs");
        await hv.CreateVmAsync(new CreateVmRequest { Name = "w11", ProcessorCount = Math.Min(2, Environment.ProcessorCount), MemoryMiB = 4096, DiskGiB = 64, SwitchName = "", Os = OsKind.Windows, RequireTpm = Environment.ProcessorCount >= 2 });
        var s = ps.Calls.Last().Script;
        if (Environment.ProcessorCount >= 2) Assert.Contains("$requireTpm = $true", s);
        Assert.Contains("if ($requireTpm) { throw", s);
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "w", ProcessorCount = 2, MemoryMiB = 2048, DiskGiB = 64, RequireTpm = true }, false));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "w", ProcessorCount = 1, MemoryMiB = 4096, DiskGiB = 64, RequireTpm = true }, false));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "w", ProcessorCount = 2, MemoryMiB = 4096, DiskGiB = 32, RequireTpm = true }, false));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "w", ProcessorCount = 2, MemoryMiB = 4096, DiskGiB = 64, Generation = 1, RequireTpm = true }, false));
    }

    [Fact]
    public async Task Update_settings_keeps_dynamic_memory_mode()
    {
        var ps = new FakePsRunner();
        var hv = new HyperVService(ps, isWindows: true, storeRoot: @"C:\VMs");
        await hv.UpdateSettingsAsync("vm1", null, 2048L << 20, null);
        var s = ps.Calls.Last().Script;
        Assert.DoesNotContain("-DynamicMemoryEnabled $false", s);
        Assert.Contains("Set-VMMemory -VM $vm -StartupBytes $mb", s);
        Assert.Contains("if ($mm.DynamicMemoryEnabled)", s);
    }

    [Fact]
    public async Task Checkpoint_list_script_reports_parent_and_current()
    {
        var ps = new FakePsRunner { Responder = _ => new PsResult(true, "[]", "") };
        var hv = new HyperVService(ps, isWindows: true, storeRoot: @"C:\VMs");
        await hv.ListCheckpointsAsync("vm1");
        Assert.Contains("ParentId = [string]$c.ParentCheckpointId", ps.Calls[^1].Script);
        Assert.Contains("$cur = [string]$vm.ParentCheckpointId", ps.Calls[^1].Script);
    }

    [Fact]
    public async Task Checkpoint_create_delete_apply_wait_for_running_disk_merge()
    {
        var ps = new FakePsRunner { Responder = _ => new PsResult(true, "id1", "") };
        var hv = new HyperVService(ps, isWindows: true, storeRoot: @"C:\VMs");
        await hv.CreateCheckpointAsync("vm1", "a");
        Assert.Contains("-contains 'MergingDisks'", ps.Calls[^1].Script);
        Assert.True(ps.Calls[^1].Script.IndexOf("MergingDisks") < ps.Calls[^1].Script.IndexOf("Checkpoint-VM"));
        await hv.DeleteCheckpointAsync("vm1", "id1");
        Assert.True(ps.Calls[^1].Script.IndexOf("MergingDisks") < ps.Calls[^1].Script.IndexOf("Remove-VMSnapshot"));
        await hv.ApplyCheckpointAsync("vm1", "id1");
        Assert.True(ps.Calls[^1].Script.IndexOf("MergingDisks") < ps.Calls[^1].Script.IndexOf("Restore-VMSnapshot"));
    }

    [Fact]
    public void Stale_hyperv_connection_error_is_recognised()
    {
        Assert.True(PsSession.IsStaleHyperVConnection("由于找不到某个对象，Hyper-V 尝试在计算机“PC”上访问该对象时遇到错误。请验证该计算机上的虚拟机管理服务是否正在运行。"));
        Assert.True(PsSession.IsStaleHyperVConnection("Hyper-V encountered an error trying to access an object on computer 'PC' because the object was not found. The object might have been deleted. Verify that the Virtual Machine Management service on the computer is running."));
        Assert.False(PsSession.IsStaleHyperVConnection("WINVM:找不到虚拟机「x」，请刷新列表。"));
        Assert.False(PsSession.IsStaleHyperVConnection(null));
    }
}

public class DemoV15Tests
{
    [Fact]
    public async Task Demo_checkpoint_tree_tracks_current_through_create_apply_delete()
    {
        var hv = new DemoHyperVService(seed: false, delay: TimeSpan.Zero);
        await hv.CreateVmAsync(new CreateVmRequest { Name = "t", ProcessorCount = 1, MemoryMiB = 1024, DiskGiB = 8, SwitchName = "" });
        var a = await hv.CreateCheckpointAsync("t", "a");
        var b = await hv.CreateCheckpointAsync("t", "b");
        var l = await hv.ListCheckpointsAsync("t");
        Assert.Equal(a, l.Single(c => c.Id == b).ParentId);
        Assert.True(l.Single(c => c.Id == b).IsCurrent);
        await hv.ApplyCheckpointAsync("t", a);
        var c2 = await hv.CreateCheckpointAsync("t", "c");
        l = await hv.ListCheckpointsAsync("t");
        Assert.Equal(a, l.Single(c => c.Id == c2).ParentId);
        await hv.DeleteCheckpointAsync("t", a);
        l = await hv.ListCheckpointsAsync("t");
        Assert.All(l, c => Assert.Equal("", c.ParentId));
        Assert.True(l.Single(c => c.Id == c2).IsCurrent);
        await hv.DeleteCheckpointAsync("t", c2);
        Assert.False((await hv.ListCheckpointsAsync("t")).Single().IsCurrent);
    }

    [Fact]
    public async Task Demo_switches_block_in_use_delete_and_protect_default()
    {
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero);
        var ext = (await hv.ListSwitchesDetailedAsync()).Single(s => s.Name == "外部网络");
        Assert.True(ext.InUse);
        await Assert.ThrowsAsync<HyperVException>(() => hv.DeleteSwitchAsync(ext.Id));
        await Assert.ThrowsAsync<HyperVException>(() => hv.DeleteSwitchAsync(VSwitchInfo.DefaultSwitchId));
        await hv.RenameSwitchAsync(ext.Id, "桥接");
        Assert.Contains("桥接", await hv.ListSwitchesAsync());
        Assert.Equal("桥接", (await hv.ListVmsAsync()).Single(v => v.Name == "Debian 12 服务器").SwitchName);
        await hv.CreateSwitchAsync("隔离", SwitchKind.Private);
        await Assert.ThrowsAsync<HyperVException>(() => hv.CreateSwitchAsync("隔离", SwitchKind.Internal));
        Assert.Equal(3, (await hv.ListSwitchesDetailedAsync()).Count);
    }

    [Fact]
    public async Task Demo_tpm_unsupported_blocks_windows11_creation_and_advanced_tpm()
    {
        var hv = new DemoHyperVService(seed: false, delay: TimeSpan.Zero) { TpmUnsupported = true };
        await Assert.ThrowsAsync<HyperVException>(() => hv.CreateVmAsync(new CreateVmRequest { Name = "w", ProcessorCount = Math.Min(2, Environment.ProcessorCount), MemoryMiB = 4096, DiskGiB = 64, SwitchName = "", Os = OsKind.Windows, RequireTpm = Environment.ProcessorCount >= 2 }).ContinueWith(t => { if (Environment.ProcessorCount < 2) throw new HyperVException(HvErrorKind.Unknown, "skip"); t.GetAwaiter().GetResult(); }));
        Assert.Empty(await hv.ListVmsAsync());
        await hv.CreateVmAsync(new CreateVmRequest { Name = "l", ProcessorCount = 1, MemoryMiB = 1024, DiskGiB = 8, SwitchName = "" });
        var ex = await Assert.ThrowsAsync<HyperVException>(() => hv.ApplyAdvancedSettingsAsync("l", new AdvancedSettingsChange { Tpm = true }));
        Assert.Contains("TPM", ex.Message);
    }
}
