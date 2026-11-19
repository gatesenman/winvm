using WinVM.Models;
using Xunit;

namespace WinVM.Tests;

public class VmInfoTests
{
    [Theory]
    //            state              start  stop   off    pause  resume restart reset  settings resize console
    [InlineData(VmState.Off,       true,  false, false, false, false, false,  false, true,    true,  true)]
    [InlineData(VmState.Running,   false, true,  true,  true,  false, true,   true,  false,   false, true)]
    [InlineData(VmState.Paused,    false, false, true,  false, true,  false,  true,  false,   false, true)]
    [InlineData(VmState.Saved,     true,  false, true,  false, false, false,  false, false,   false, true)]
    [InlineData(VmState.Starting,  false, false, true,  false, false, false,  false, false,   false, true)]
    [InlineData(VmState.Stopping,  false, false, true,  false, false, false,  false, false,   false, true)]
    public void Command_availability_by_state(VmState s, bool start, bool stop, bool off, bool pause, bool resume,
        bool restart, bool reset, bool settings, bool resize, bool console)
    {
        var v = new VmInfo { State = s, VhdPath = @"C:\a.vhdx" };
        Assert.Equal(start, v.CanStart);
        Assert.Equal(stop, v.CanStop);
        Assert.Equal(off, v.CanTurnOff);
        Assert.Equal(pause, v.CanPause);
        Assert.Equal(resume, v.CanResume);
        Assert.Equal(restart, v.CanRestart);
        Assert.Equal(reset, v.CanReset);
        Assert.Equal(settings, v.CanEditSettings);
        Assert.Equal(resize, v.CanResizeDisk);
        Assert.Equal(console, v.CanOpenConsole);
    }

    [Fact]
    public void Transient_states_block_delete_and_rename()
    {
        foreach (var s in new[] { VmState.Starting, VmState.Stopping, VmState.Saving, VmState.Resetting })
        {
            var v = new VmInfo { State = s };
            Assert.True(v.IsTransient);
            Assert.False(v.CanDelete);
            Assert.False(v.CanRename);
        }
    }

    [Fact]
    public void Resize_requires_a_disk()
    {
        Assert.False(new VmInfo { State = VmState.Off, VhdPath = "" }.CanResizeDisk);
    }

    [Fact]
    public void UpdateFrom_reports_changes_and_notifies_derived_once()
    {
        var a = new VmInfo { Name = "A", Id = "1", State = VmState.Off };
        var events = new List<string>();
        a.PropertyChanged += (_, e) => events.Add(e.PropertyName!);
        Assert.False(a.UpdateFrom(new VmInfo { Name = "A", Id = "1", State = VmState.Off }));
        Assert.Empty(events);

        Assert.True(a.UpdateFrom(new VmInfo { Name = "A", Id = "1", State = VmState.Running, UptimeSeconds = 5 }));
        Assert.Equal(VmState.Running, a.State);
        Assert.Equal(1, events.Count(e => e == nameof(VmInfo.CanStart)));
        Assert.Equal(1, events.Count(e => e == nameof(VmInfo.StateText)));
    }

    [Fact]
    public void Setting_a_property_raises_derived_notifications()
    {
        var a = new VmInfo { State = VmState.Off };
        var events = new List<string>();
        a.PropertyChanged += (_, e) => events.Add(e.PropertyName!);
        a.State = VmState.Paused;
        Assert.Contains(nameof(VmInfo.CanResume), events);
        Assert.Contains(nameof(VmInfo.StateText), events);
    }

    [Theory]
    [InlineData(0L, "—")]
    [InlineData(536870912L, "512 MiB")]
    [InlineData(1073741824L, "1 GiB")]
    [InlineData(3221225472L, "3 GiB")]
    [InlineData(1610612736L, "1.5 GiB")]
    public void Formats_bytes(long b, string s) => Assert.Equal(s, VmInfo.FormatBytes(b));

    [Theory]
    [InlineData(0L, "—")]
    [InlineData(42L, "42 秒")]
    [InlineData(125L, "2 分 5 秒")]
    [InlineData(3 * 3600 + 120L, "3 小时 2 分")]
    [InlineData(2 * 86400 + 3600L, "2 天 1 小时")]
    public void Formats_uptime(long s, string text) => Assert.Equal(text, VmInfo.FormatUptime(s));

    [Fact]
    public void Ref_prefers_id()
    {
        Assert.Equal("id-1", new VmInfo { Name = "n", Id = "id-1" }.Ref);
        Assert.Equal("n", new VmInfo { Name = "n" }.Ref);
    }
}
