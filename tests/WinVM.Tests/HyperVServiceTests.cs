using WinVM.Models;
using WinVM.Services;
using Xunit;

namespace WinVM.Tests;

public class HyperVServiceTests
{
    private static (HyperVService svc, FakePsRunner ps) Make(Func<string, PsResult>? responder = null)
    {
        var ps = new FakePsRunner();
        if (responder != null) ps.Responder = responder;
        return (new HyperVService(ps, isWindows: true, storeRoot: @"C:\Users\me\WinVM\Machines"), ps);
    }

    [Fact]
    public void Quotes_are_escaped_against_injection()
    {
        Assert.Equal("'a''b'", HyperVService.Q("a'b"));
        Assert.Equal("'x\u2019\u2019y'", HyperVService.Q("x\u2019y"));
        Assert.Equal("''", HyperVService.Q(null));
    }

    [Fact]
    public async Task Actions_use_resolver_and_action_lane()
    {
        var (svc, ps) = Make();
        await svc.StartAsync("evil'; Remove-Item C:\\ -Recurse; '");
        var (script, lane) = ps.Calls.Single();
        Assert.Equal(PsLane.Action, lane);
        Assert.Contains("Resolve-WinVM 'evil''; Remove-Item C:\\ -Recurse; '''", script);
        Assert.Contains("Start-VM -VM $vm", script);
    }

    [Fact]
    public async Task Restart_is_guest_reboot_and_reset_is_hard()
    {
        var (svc, ps) = Make();
        await svc.RestartAsync("id");
        await svc.ResetAsync("id");
        Assert.Contains("-Type Reboot", ps.Calls[0].Script);
        Assert.Contains("-Type Reset", ps.Calls[1].Script);
    }

    [Fact]
    public async Task List_uses_single_batched_query()
    {
        var (svc, ps) = Make(_ => new PsResult(true, """[{"Name":"A","Id":"1","State":"Off"}]""", ""));
        var list = await svc.ListVmsAsync();
        Assert.Single(list);
        var (script, lane) = Assert.Single(ps.Calls);
        Assert.Equal(PsLane.Query, lane);
        Assert.Contains("Get-VMHardDiskDrive -VM $vms", script);
        Assert.Contains("Get-VMSnapshot -VM $vms", script);
        Assert.Contains("Get-VHD -Path $paths", script);
        Assert.DoesNotContain("foreach ($vm in $vms) {\n  $dvd = $null", script);
    }

    [Fact]
    public async Task Failures_become_mapped_HyperVException()
    {
        var (svc, _) = Make(_ => new PsResult(false, "", "Access is denied."));
        var ex = await Assert.ThrowsAsync<HyperVException>(() => svc.StopAsync("x"));
        Assert.Equal(HvErrorKind.AccessDenied, ex.Kind);
        Assert.StartsWith("关机失败：", ex.Message);
    }

    [Fact]
    public async Task Timeout_maps_to_friendly_message()
    {
        var ps = new ThrowingRunner();
        var svc = new HyperVService(ps, isWindows: true);
        var ex = await Assert.ThrowsAsync<HyperVException>(() => svc.PauseAsync("x"));
        Assert.Equal(HvErrorKind.Timeout, ex.Kind);
    }

    private sealed class ThrowingRunner : IPsRunner
    {
        public Task<PsResult> RunAsync(string script, TimeSpan timeout, PsLane lane = PsLane.Query, CancellationToken ct = default) =>
            throw new TimeoutException();
        public void Dispose() { }
    }

    [Fact]
    public async Task Memory_is_aligned_to_2MiB_and_only_changed_values_sent()
    {
        var (svc, ps) = Make();
        await svc.UpdateSettingsAsync("id", null, 1_395_864_371L, null);
        var s = ps.Calls.Single().Script;
        Assert.Contains("$mb = [long]1396703232", s); // rounded to nearest 2 MiB (666 x 2 MiB)
        Assert.Contains("Set-VMMemory -VM $vm -StartupBytes $mb", s);
        Assert.DoesNotContain("Set-VMProcessor", s);
        Assert.DoesNotContain("Set-VMDvdDrive", s);
    }

    [Fact]
    public async Task Settings_can_eject_iso_and_change_switch()
    {
        var (svc, ps) = Make();
        await svc.UpdateSettingsAsync("id", 2, null, "", "Default Switch");
        var s = ps.Calls.Single().Script;
        Assert.Contains("Set-VMProcessor -VM $vm -Count 2", s);
        Assert.Contains("-Path $null", s);
        Assert.Contains("Connect-VMNetworkAdapter", s);
    }

    [Fact]
    public async Task Delete_never_removes_folders_outside_the_WinVM_store()
    {
        var (svc, ps) = Make();
        await svc.DeleteAsync("id", deleteFiles: true);
        var s = ps.Calls.Single().Script;
        Assert.Contains(@"$store = 'C:\Users\me\WinVM\Machines'", s);
        Assert.Contains("StartsWith($store + '\\'", s);
        Assert.DoesNotContain("Remove-Item -LiteralPath $path -Recurse", s); // 1.2 bug: deleted $vm.Path recursively
        Assert.Contains("ParentPath", s); // checkpoint differencing chain
    }

    [Fact]
    public async Task Create_script_disables_auto_checkpoints_and_rolls_back()
    {
        var (svc, ps) = Make();
        await svc.CreateVmAsync(new CreateVmRequest { Name = "T", ProcessorCount = 1, MemoryMiB = 513, DiskGiB = 2, SwitchName = "", Os = OsKind.Windows });
        var s = ps.Calls.Single().Script;
        Assert.Contains("AutomaticCheckpointsEnabled $false", s);
        Assert.Contains("Remove-VM -VM $vm -Force", s);
        Assert.Contains("$mem = [long]536870912", s); // 513 -> 512 MiB (even)
        Assert.Contains("SecureBootTemplate MicrosoftWindows", s);
        Assert.Contains("$notes = '[WinVM os=windows]'", s);
        Assert.Contains("T.vhdx", s);
    }

    [Theory]
    [InlineData("", "请填写虚拟机名称。")]
    [InlineData("ok", null)]
    public void Validate_name(string name, string? error)
    {
        var req = new CreateVmRequest { Name = name, ProcessorCount = 1, MemoryMiB = 1024, DiskGiB = 8 };
        if (error == null) HyperVService.ValidateCreateRequest(req, false);
        else Assert.Equal(error, Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(req, false)).Message);
    }

    [Fact]
    public void Validate_limits()
    {
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "a", MemoryMiB = 256 }, false));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "a", DiskGiB = 0 }, false));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "a", ProcessorCount = 0 }, false));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "a", IsoPath = "x.img" }, false));
        HyperVService.ValidateCreateRequest(new CreateVmRequest { Name = "a", ProcessorCount = 1, IsoPath = "" }, false); // ISO optional
    }

    [Theory]
    [InlineData("Ubuntu 24.04", "Ubuntu 24.04")]
    [InlineData("a:b*c?", "a_b_c_")]
    [InlineData("  ..  ", "VM")]
    public void Sanitizes_folder_names(string input, string expected) => Assert.Equal(expected, HyperVService.SanitizeName(input));

    [Fact]
    public async Task Non_windows_reports_unavailable()
    {
        var svc = new HyperVService(new FakePsRunner(), isWindows: false);
        var st = await svc.CheckHyperVAsync();
        Assert.False(st.Available);
        Assert.Empty(await svc.ListVmsAsync());
        await Assert.ThrowsAsync<HyperVException>(() => svc.StartAsync("x"));
    }

    [Fact]
    public void Interprets_codes_in_chinese()
    {
        var text = HyperVService.InterpretCodes(new[] { "FEATURE_DISABLED", "NOT_ELEVATED", "PROBE_FAIL:Access is denied." });
        Assert.Contains("未启用", text);
        Assert.Contains("管理员", text);
        Assert.Contains("没有权限", text);
    }
}

public class GuidanceTests
{
    [Fact]
    public void Permission_problem_gets_permission_steps_only()
    {
        var g = HyperVService.GuidanceFor(new[] { "ACCESS_DENIED", "NOT_ELEVATED" });
        Assert.Contains("以管理员身份重新启动", g);
        Assert.DoesNotContain("Enable-WindowsOptionalFeature", g);
    }

    [Fact]
    public void Missing_feature_gets_enable_steps()
    {
        Assert.Contains("Enable-WindowsOptionalFeature", HyperVService.GuidanceFor(new[] { "FEATURE_DISABLED", "ACCESS_DENIED" }));
        Assert.Contains("Start-Service vmms", HyperVService.GuidanceFor(new[] { "VMMS_STOPPED" }));
        Assert.Contains("Enable-WindowsOptionalFeature", HyperVService.GuidanceFor(null));
    }
}
