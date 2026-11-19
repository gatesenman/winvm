using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;
using Xunit;

namespace WinVM.Tests;

public class V15ParserTests
{
    [Fact]
    public void ParseAdvanced_reads_all_fields_and_strips_os_tag()
    {
        var json = """
{"Generation":2,"State":"Off","SecureBoot":"On","SecureBootTemplate":"MicrosoftUEFICertificateAuthority","Tpm":true,
 "DynamicMemory":true,"Startup":1073741824,"Minimum":536870912,"Maximum":2147483648,"Buffer":25,
 "Boot":[{"Index":0,"Kind":"Dvd","Description":"ubuntu.iso"},{"Index":1,"Kind":"Disk","Description":"a.vhdx"},{"Index":2,"Kind":"Network","Description":"Default Switch"}],
 "Nested":false,"StartAction":"Nothing","StartDelay":5,"StopAction":"TurnOff","Notes":"[WinVM os=linux]\nhello 中文","Transport":"HvSocket","HostEnhanced":true}
""";
        var a = VmParser.ParseAdvanced(json);
        Assert.True(a.IsGen2 && a.IsOff && a.SecureBoot && a.TpmEnabled && a.DynamicMemory);
        Assert.Equal(SecureBootTemplates.UefiCa, a.SecureBootTemplate);
        Assert.Equal(536870912, a.MinimumBytes);
        Assert.Equal(2147483648, a.MaximumBytes);
        Assert.Equal(25, a.MemoryBuffer);
        Assert.Equal(new[] { BootDeviceKind.Dvd, BootDeviceKind.Disk, BootDeviceKind.Network }, a.BootOrder.Select(b => b.Kind));
        Assert.Equal("DVD 驱动器 · ubuntu.iso", a.BootOrder[0].Display);
        Assert.Equal(AutoStartActions.Nothing, a.AutoStartAction);
        Assert.Equal(5, a.AutoStartDelaySeconds);
        Assert.Equal(AutoStopActions.TurnOff, a.AutoStopAction);
        Assert.Equal("hello 中文", a.Notes);
        Assert.True(a.EnhancedSessionHvSocket && a.HostEnhancedSessionEnabled);
    }

    [Fact]
    public void ParseAdvanced_gen1_and_single_boot_object_and_bad_json()
    {
        var a = VmParser.ParseAdvanced("""{"Generation":1,"State":"Running","SecureBoot":"Off","Boot":{"Index":0,"Kind":"Disk","Description":""}}""");
        Assert.False(a.IsGen2);
        Assert.False(a.IsOff);
        Assert.Single(a.BootOrder);
        Assert.Equal(AutoStartActions.StartIfRunning, a.AutoStartAction);
        Assert.Throws<HyperVException>(() => VmParser.ParseAdvanced("not json"));
    }

    [Fact]
    public void ParseSwitches_handles_single_vm_string_arrays_and_default_first()
    {
        var json = """
[{"Id":"11111111-1111-1111-1111-111111111111","Name":"Lab","Type":"Private","Adapter":"","AllowManagementOS":false,"Vms":"vm1"},
 {"Id":"c08cb7b8-9b3c-408e-8e30-5e16a3aeb444","Name":"Default Switch","Type":"Internal","Vms":["b","a","a"]},
 {"Id":"22222222-2222-2222-2222-222222222222","Name":"Ext","Type":"External","Adapter":"Intel NIC","AllowManagementOS":true,"Vms":[]}]
""";
        var l = VmParser.ParseSwitches(json);
        Assert.Equal(new[] { "Default Switch", "Ext", "Lab" }, l.Select(s => s.Name));
        Assert.True(l[0].IsDefaultSwitch && !l[0].CanModify);
        Assert.Equal(new[] { "a", "b" }, l[0].ConnectedVms);
        Assert.Equal("默认（NAT）", l[0].KindText);
        Assert.Equal(SwitchKind.External, l[1].Kind);
        Assert.Contains("Intel NIC", l[1].KindDescription);
        Assert.Equal(new[] { "vm1" }, l[2].ConnectedVms);
        Assert.Equal("专用", l[2].KindText);
        Assert.Empty(VmParser.ParseSwitches(""));
    }

    [Fact]
    public void ParseAdapters_and_checkpoint_parent_fields()
    {
        var ad = VmParser.ParseAdapters("""[{"Name":"以太网","Description":"Intel","Status":"Up","Bound":true},{"Name":"WLAN","Description":"Wi-Fi","Status":"Disconnected","Bound":false}]""");
        Assert.Equal(2, ad.Count);
        Assert.Contains("已被外部交换机使用", ad[0].Display);
        Assert.Contains("Disconnected", ad[1].Display);
        var cps = VmParser.ParseCheckpoints("""[{"Name":"a","Id":"A","ParentId":"","IsCurrent":false,"CreationTime":"2026-10-01T10:00:00"},{"Name":"b","Id":"B","ParentId":"A","IsCurrent":true,"CreationTime":"2026-10-02T10:00:00"}]""");
        Assert.Equal("A", cps.Single(c => c.Name == "b").ParentId);
        Assert.True(cps.Single(c => c.Name == "b").IsCurrent);
    }

    [Fact]
    public void Notes_helpers_preserve_tag()
    {
        Assert.Equal("hi", OsDetector.UserNotes("[WinVM os=windows]\nhi"));
        Assert.Equal("[WinVM os=windows]\nnew", OsDetector.JoinNotes("[WinVM os=windows]\nold", " new "));
        Assert.Equal("[WinVM os=linux]", OsDetector.JoinNotes("x [WinVM os=linux]", ""));
        Assert.Equal("plain", OsDetector.JoinNotes(null, "plain"));
    }
}

public class CheckpointTreeTests
{
    private static CheckpointInfo Cp(string id, string parent, int day, bool current = false) =>
        new() { Name = "cp" + id, Id = id, ParentId = parent, CreationTime = new DateTime(2026, 10, day), IsCurrent = current };

    [Fact]
    public void Build_nests_children_and_places_now_marker_under_current()
    {
        var roots = CheckpointNode.Build(new[] { Cp("C", "A", 3, current: true), Cp("A", "", 1), Cp("B", "A", 2), Cp("D", "C", 4) });
        var a = Assert.Single(roots);
        Assert.Equal("A", a.Checkpoint!.Id);
        Assert.Equal(new[] { "B", "C" }, a.Children.Select(c => c.Checkpoint!.Id));
        var c = a.Children[1];
        Assert.Equal(2, c.Children.Count);
        Assert.Contains(c.Children, n => n.IsNowMarker && n.Name == "当前位置");
        Assert.Contains(c.Children, n => n.Checkpoint?.Id == "D");
        Assert.Equal(5, CheckpointNode.Flatten(roots).Count());
    }

    [Fact]
    public void Build_without_current_puts_marker_at_root_and_handles_orphans_cycles_and_empty()
    {
        Assert.Empty(CheckpointNode.Build(Array.Empty<CheckpointInfo>()));
        var roots = CheckpointNode.Build(new[] { Cp("A", "missing", 1), Cp("B", "", 2) });
        Assert.Equal(3, roots.Count);
        Assert.True(roots[^1].IsNowMarker);
        var cyc = CheckpointNode.Build(new[] { Cp("A", "B", 1), Cp("B", "A", 2) });
        Assert.Equal(3, CheckpointNode.Flatten(cyc).Count());
    }
}

public class AdvancedValidationTests
{
    private static VmAdvancedSettings Cur(bool off = true, int gen = 2, bool dyn = false, bool nested = false) => new()
    {
        Generation = gen, State = off ? VmState.Off : VmState.Running, StartupBytes = 1024L << 20, MinimumBytes = 512L << 20,
        MaximumBytes = 2048L << 20, MemoryBuffer = 20, DynamicMemory = dyn, NestedVirtualization = nested,
        BootOrder = new[] { new BootEntry(0, BootDeviceKind.Disk, ""), new BootEntry(1, BootDeviceKind.Network, "") }
    };

    [Fact]
    public void Off_only_fields_require_off_but_notes_and_auto_actions_do_not()
    {
        var ex = Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(off: false), new AdvancedSettingsChange { Tpm = true }));
        Assert.Contains("关机", ex.Message);
        HyperVService.ValidateAdvanced(Cur(off: false), new AdvancedSettingsChange { Notes = "x", AutoStopAction = AutoStopActions.ShutDown, AutoStartDelaySeconds = 30 });
        Assert.False(new AdvancedSettingsChange { Notes = "x" }.RequiresOff);
        Assert.True(new AdvancedSettingsChange { BootOrder = new[] { 1, 0 } }.RequiresOff);
        Assert.True(new AdvancedSettingsChange().IsEmpty);
    }

    [Theory]
    [InlineData("secure")]
    [InlineData("tpm")]
    [InlineData("boot")]
    public void Gen1_rejects_firmware_features(string what)
    {
        var ch = what switch
        {
            "secure" => new AdvancedSettingsChange { SecureBoot = true },
            "tpm" => new AdvancedSettingsChange { Tpm = true },
            _ => new AdvancedSettingsChange { BootOrder = new[] { 1, 0 } }
        };
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(gen: 1), ch));
    }

    [Fact]
    public void Dynamic_memory_rules()
    {
        HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { DynamicMemory = true, MinimumBytes = 512L << 20, MaximumBytes = 4096L << 20, MemoryBuffer = 50 });
        Assert.Contains("最小内存不能大于最大内存", Assert.Throws<ArgumentException>(() =>
            HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { DynamicMemory = true, MinimumBytes = 4096L << 20, MaximumBytes = 2048L << 20 })).Message);
        Assert.Contains("启动内存", Assert.Throws<ArgumentException>(() =>
            HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { DynamicMemory = true, MinimumBytes = 1536L << 20 })).Message);
        Assert.Contains("缓冲区", Assert.Throws<ArgumentException>(() =>
            HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { DynamicMemory = true, MemoryBuffer = 3 })).Message);
        Assert.Contains("嵌套", Assert.Throws<ArgumentException>(() =>
            HyperVService.ValidateAdvanced(Cur(nested: true), new AdvancedSettingsChange { DynamicMemory = true })).Message);
        Assert.Contains("嵌套", Assert.Throws<ArgumentException>(() =>
            HyperVService.ValidateAdvanced(Cur(dyn: true), new AdvancedSettingsChange { NestedVirtualization = true })).Message);
        HyperVService.ValidateAdvanced(Cur(dyn: true), new AdvancedSettingsChange { NestedVirtualization = true, DynamicMemory = false });
    }

    [Fact]
    public void Boot_order_and_actions_validation()
    {
        HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { BootOrder = new[] { 1, 0 } });
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { BootOrder = new[] { 0, 0 } }));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { BootOrder = new[] { 0 } }));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { BootOrder = new[] { 0, 2 } }));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { AutoStartAction = "Bogus" }));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { AutoStopAction = "Bogus" }));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { AutoStartDelaySeconds = -1 }));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { SecureBoot = true, SecureBootTemplate = "x" }));
        Assert.Throws<ArgumentException>(() => HyperVService.ValidateAdvanced(Cur(), new AdvancedSettingsChange { Notes = new string('x', 4001) }));
    }
}
