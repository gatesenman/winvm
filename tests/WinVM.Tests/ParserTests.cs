using WinVM.Models;
using WinVM.Services;
using Xunit;

namespace WinVM.Tests;

public class ParserTests
{
    private const string Two = """
[{"Name":"Ubuntu","Id":"11111111-1111-1111-1111-111111111111","State":"Running","ProcessorCount":4,"MemoryStartup":4294967296,"MemoryAssigned":4294967296,"DynamicMemory":false,"CpuUsage":12,"UptimeSeconds":3700,"Generation":2,"Version":"12.0","Path":"C:\\VMs\\Ubuntu","Notes":"[WinVM os=linux]","CreationTime":"2026-10-01T08:00:00.0000000+08:00","SwitchName":"Default Switch","DvdPath":"","VhdPath":"C:\\VMs\\Ubuntu\\u.vhdx","DiskSizeBytes":68719476736,"DiskFileBytes":1073741824,"CheckpointCount":2,"SecureBoot":false},
 {"Name":"Win11","Id":"22222222-2222-2222-2222-222222222222","State":"Off","ProcessorCount":"2","MemoryStartup":"8589934592","Generation":2,"Path":null,"Notes":null,"CreationTime":"/Date(1760000000000)/","SwitchName":null,"DvdPath":"D:\\Win11_24H2.iso","VhdPath":"","DiskSizeBytes":0,"CheckpointCount":0,"SecureBoot":true}]
""";

    [Fact]
    public void Parses_array_with_all_fields()
    {
        var list = VmParser.ParseVmList(Two);
        Assert.Equal(2, list.Count);
        var u = list.Single(v => v.Name == "Ubuntu");
        Assert.Equal(VmState.Running, u.State);
        Assert.Equal(4, u.ProcessorCount);
        Assert.Equal(4L << 30, u.MemoryStartupBytes);
        Assert.Equal(12, u.CpuUsage);
        Assert.Equal(3700, u.UptimeSeconds);
        Assert.Equal(64L << 30, u.DiskSizeBytes);
        Assert.Equal(2, u.CheckpointCount);
        Assert.Null(u.DvdPath);
        Assert.Equal(OsKind.Linux, u.Os);
        Assert.Equal("1 小时 1 分", u.UptimeText);
        Assert.NotNull(u.CreationTime);

        var w = list.Single(v => v.Name == "Win11");
        Assert.Equal(VmState.Off, w.State);
        Assert.Equal(2, w.ProcessorCount);            // number as string
        Assert.Equal(8L << 30, w.MemoryStartupBytes); // number as string
        Assert.Equal("", w.SwitchName);               // null -> ""
        Assert.Equal(OsKind.Windows, w.Os);           // from ISO name
        Assert.True(w.SecureBoot);
        Assert.NotNull(w.CreationTime);               // /Date()/ form
        Assert.Equal("—", w.DiskShort);
    }

    [Fact]
    public void Single_object_is_accepted()
    {
        var list = VmParser.ParseVmList("""{"Name":"Solo","Id":"x","State":"Paused"}""");
        Assert.Single(list);
        Assert.Equal(VmState.Paused, list[0].State);
        Assert.Equal(2, list[0].Generation); // default
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("  \n ")]
    public void Empty_inputs_give_empty_list(string json) => Assert.Empty(VmParser.ParseVmList(json));

    [Fact]
    public void Leading_noise_is_ignored()
    {
        var list = VmParser.ParseVmList("WARNING: something\n[{\"Name\":\"A\",\"State\":\"Off\"}]");
        Assert.Single(list);
    }

    [Fact]
    public void Invalid_json_throws_friendly()
    {
        var ex = Assert.Throws<HyperVException>(() => VmParser.ParseVmList("[{\"Name\":"));
        Assert.Contains("解析", ex.Message);
    }

    [Fact]
    public void Entries_without_name_are_skipped_and_sorted_by_name()
    {
        var list = VmParser.ParseVmList("""[{"Name":"b"},{"Name":""},{"Name":"A"}]""");
        Assert.Equal(new[] { "A", "b" }, list.Select(v => v.Name));
    }

    [Theory]
    [InlineData("Off", VmState.Off)]
    [InlineData("OffCritical", VmState.Off)]
    [InlineData("Running", VmState.Running)]
    [InlineData("Paused", VmState.Paused)]
    [InlineData("Saved", VmState.Saved)]
    [InlineData("Starting", VmState.Starting)]
    [InlineData("Stopping", VmState.Stopping)]
    [InlineData("Saving", VmState.Saving)]
    [InlineData("Resetting", VmState.Resetting)]
    [InlineData("FastSaved", VmState.Other)]
    [InlineData(null, VmState.Unknown)]
    public void Parses_states(string? s, VmState expected) => Assert.Equal(expected, VmParser.ParseState(s));

    [Fact]
    public void Parses_checkpoints_newest_first()
    {
        var list = VmParser.ParseCheckpoints("""
[{"Name":"old","Id":"1","SnapshotType":"Standard","CreationTime":"2026-01-01T00:00:00Z","ParentCheckpointName":""},
 {"Name":"new","Id":"2","SnapshotType":"Recovery","CreationTime":"2026-06-01T00:00:00Z","ParentCheckpointName":"old"}]
""");
        Assert.Equal(new[] { "new", "old" }, list.Select(c => c.Name));
        Assert.Equal("恢复", list[0].TypeText);
        Assert.Equal("标准", list[1].TypeText);
        Assert.Equal("old", list[0].ParentText);
    }

    [Fact]
    public void Parses_string_lists()
    {
        Assert.Equal(new[] { "Default Switch", "外部" }, VmParser.ParseStringList("[\"Default Switch\",\"外部\"]"));
        Assert.Equal(new[] { "Only" }, VmParser.ParseStringList("\"Only\""));
        Assert.Empty(VmParser.ParseStringList(""));
        Assert.Empty(VmParser.ParseStringList("garbage"));
    }

    [Theory]
    [InlineData("[WinVM os=windows]", "anything", null, OsKind.Windows)]
    [InlineData("[WinVM os=linux]", "Windows 11", null, OsKind.Linux)]
    [InlineData("", "Ubuntu Server", null, OsKind.Linux)]
    [InlineData("", "my-arch-box", null, OsKind.Linux)]
    [InlineData("", "research", null, OsKind.Unknown)]
    [InlineData("", "Win11 Dev", null, OsKind.Windows)]
    [InlineData("", "dev-box", @"D:\iso\Win11_24H2_Chinese.iso", OsKind.Windows)]
    [InlineData("", "dev-box", @"D:\iso\debian-12.7.0-amd64-netinst.iso", OsKind.Linux)]
    [InlineData("", "WinVM-E2E-20261019", null, OsKind.Unknown)]
    [InlineData("", "Windows Server 2022", null, OsKind.Windows)]
    public void Detects_os(string notes, string name, string? iso, OsKind expected) =>
        Assert.Equal(expected, OsDetector.Detect(notes, name, iso));

    [Fact]
    public void Status_json_is_parsed()
    {
        var ok = HyperVService.ParseStatus("""{"Ok":true,"Codes":[],"Elevated":true,"AdminMember":true}""");
        Assert.True(ok.Available);
        var bad = HyperVService.ParseStatus("""{"Ok":false,"Codes":["ACCESS_DENIED","NOT_ELEVATED"],"Elevated":false,"AdminMember":true}""");
        Assert.False(bad.Available);
        Assert.True(bad.NeedsElevation);
        Assert.Contains("管理员", bad.Message);
        var single = HyperVService.ParseStatus("""{"Ok":false,"Codes":"FEATURE_DISABLED","Elevated":true,"AdminMember":true}""");
        Assert.Contains("未启用", single.Message);
        Assert.False(single.NeedsElevation);
    }
}
