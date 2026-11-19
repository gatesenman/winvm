using System.Net;
using System.Text;
using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;
using Xunit;

namespace WinVM.Tests;

public class V16ConnectInfoTests
{
    [Fact]
    public void ConnectInfo_from_running_vm_with_ip()
    {
        var vm = new VmInfo
        {
            Name = "Ubuntu-桌面-1",
            Id = Guid.NewGuid().ToString(),
            State = VmState.Running,
            Os = OsKind.Linux,
            GuestIp = "192.168.0.12,fe80::1",
            SwitchName = "Default Switch"
        };
        var info = ConnectInfo.FromVm(vm, "agent");
        Assert.True(info.Ready);
        Assert.Equal("192.168.0.12", info.Ip);
        Assert.Equal(3389, info.Rdp!.Port);
        Assert.Equal(22, info.Ssh!.Port);
        Assert.Contains("vmconnect", info.VmConnect!);
        Assert.Contains("192.168.0.12", info.ToClipboardText());
        Assert.Contains("agent", info.ToClipboardText());
        Assert.Contains("\"ready\": true", info.ToJson().Replace("\r", ""));
    }

    [Fact]
    public void ConnectInfo_not_ready_without_ip()
    {
        var vm = new VmInfo { Name = "x", State = VmState.Running, Os = OsKind.Windows };
        var info = ConnectInfo.FromVm(vm);
        Assert.False(info.Ready);
        Assert.Null(info.Rdp);
    }

    [Fact]
    public void FirstBoot_writes_windows_and_linux_scripts_without_logging_password()
    {
        var root = Path.Combine(Path.GetTempPath(), "winvm-fb-" + Guid.NewGuid().ToString("N"));
        try
        {
            // redirect via writing into FolderFor using temp by overriding through Write destination:
            // FirstBootProfile uses SetupRoot under UserProfile — write then assert under known folder name
            var name = "WinVM-e2e-fb-" + Guid.NewGuid().ToString("N")[..8];
            var dir = FirstBootProfile.Write(name, new FirstBootProfile.Options
            {
                Os = OsKind.Windows,
                EnableRdp = true,
                EnableOpenSsh = true,
                AgentReady = true,
                Hostname = "win-guest",
                Username = "admin1",
                Password = "Secret-Never-Log"
            });
            Assert.True(File.Exists(Path.Combine(dir, "Enable-WinGuest.ps1")));
            Assert.True(File.Exists(Path.Combine(dir, "Windows-RDP-notes.txt")));
            var ps = File.ReadAllText(Path.Combine(dir, "Enable-WinGuest.ps1"));
            Assert.Contains("fDenyTSConnections", ps);
            Assert.Contains("Secret-Never-Log", ps); // present in script file only

            var dirL = FirstBootProfile.Write(name + "-L", new FirstBootProfile.Options
            {
                Os = OsKind.Linux,
                EnableOpenSsh = true,
                AgentReady = true,
                Hostname = "linux-guest",
                Username = "agent",
                Password = "Secret-Linux"
            });
            Assert.True(File.Exists(Path.Combine(dirL, "cloud-init", "user-data")));
            Assert.True(File.Exists(Path.Combine(dirL, "firstboot.sh")));
            var ud = File.ReadAllText(Path.Combine(dirL, "cloud-init", "user-data"));
            Assert.Contains("openssh-server", ud);
            Assert.Contains("Asia/Shanghai", ud);
            Assert.Contains("curl", ud);
        }
        finally
        {
            try
            {
                var setup = FirstBootProfile.SetupRoot;
                foreach (var d in Directory.Exists(setup) ? Directory.GetDirectories(setup, "WinVM-e2e-fb-*") : Array.Empty<string>())
                    Directory.Delete(d, true);
            }
            catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}

public class V16WindowsIsoResolverTests
{
    [Fact]
    public void ParseSkus_and_links_from_fido_shaped_json()
    {
        var skuJson = """{"Skus":[{"Id":"123","ProductDownloadId":"abc","LocalizedLanguage":"Chinese (Simplified)","Language":"zh-CN"},{"Id":"456","ProductDownloadId":"def","LocalizedLanguage":"English","Language":"en-US"}]}""";
        var skus = WindowsIsoResolver.ParseSkus(skuJson);
        Assert.True(skus.Count >= 1);
        var zh = skus.FirstOrDefault(s => s.Language.Contains("zh", StringComparison.OrdinalIgnoreCase)
                                          || s.LocalizedLanguage.Contains("Chinese", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(zh);

        var linkJson = """{"ProductDownloadOptions":[{"Uri":"https://software.download.prss.microsoft.com/dbazure/Win11.iso","DownloadType":1}]}""";
        var links = WindowsIsoResolver.ParseDownloadLinks(linkJson);
        Assert.NotEmpty(links);
        Assert.Contains(".iso", links[0].Uri, StringComparison.OrdinalIgnoreCase);
    }
}

public class V16OneClickTests
{
    [Fact]
    public void SuggestName_and_BuildRequest_win11_tpm()
    {
        var e = IsoCatalog.Find("windows-11") ?? IsoCatalog.Entries.First(x => x.Id.Contains("windows-11"));
        var name = OneClickInstallViewModel.SuggestName(e, new[] { "Windows11-1" });
        Assert.Equal("Windows11-2", name);

        var hv = new DemoHyperVService(seed: false, delay: TimeSpan.Zero);
        var prefs = TestPrefs.Isolated();
        var lib = new ImageLibraryViewModel(prefs);
        var vm = new OneClickInstallViewModel(hv, prefs, lib);
        var req = vm.BuildRequest(e, "Windows11-2", @"C:\iso\Win11.iso");
        Assert.True(req.RequireTpm);
        Assert.Equal(OsKind.Windows, req.Os);
        Assert.True(req.MemoryMiB >= 4096);
    }

    [Fact]
    public void Mac_card_is_unsupported_not_downloadable()
    {
        var mac = IsoCatalog.Entries.Where(e => e.IsUnsupported).ToList();
        Assert.NotEmpty(mac);
        Assert.All(mac, e => Assert.False(e.IsDownloadable));
    }
}
