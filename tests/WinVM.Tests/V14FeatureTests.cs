using System.Net;
using System.Security.Cryptography;
using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;
using Xunit;

namespace WinVM.Tests;

public class IsoCatalogTests
{
    private const string H1 = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string H2 = "2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void ParseSums_reads_gnu_and_bsd_formats_and_ignores_noise()
    {
        var text = $"-----BEGIN PGP SIGNED MESSAGE-----\n# comment\n{H1} *ubuntu-26.04.1-desktop-amd64.iso\r\n{H2.ToUpperInvariant()}  debian-13.7.0-amd64-netinst.iso\nSHA256 (Fedora-Workstation-Live-44-1.7.x86_64.iso) = {H1}\ngarbage line\n";
        var m = IsoCatalog.ParseSums(text);
        Assert.Equal(3, m.Count);
        Assert.Equal(H1, m["ubuntu-26.04.1-desktop-amd64.iso"]);
        Assert.Equal(H2, m["debian-13.7.0-amd64-netinst.iso"]);
        Assert.Equal(H1, m["Fedora-Workstation-Live-44-1.7.x86_64.iso"]);
    }

    [Fact]
    public void Candidates_prefer_point_release_over_amd64_suffix_digits()
    {
        var sums = new Dictionary<string, string>
        {
            ["ubuntu-26.04-desktop-amd64.iso"] = H1,
            ["ubuntu-26.04.1-desktop-amd64.iso"] = H2,
            ["ubuntu-26.04.10-desktop-amd64.iso"] = H2,
            ["ubuntu-26.04.9-desktop-amd64.iso"] = H2,
            ["ubuntu-26.04.1-live-server-amd64.iso"] = H1,
        };
        var c = IsoCatalog.Candidates(sums, @"^ubuntu-[\d.]+-desktop-amd64\.iso$");
        Assert.Equal(new[] { "ubuntu-26.04.10-desktop-amd64.iso", "ubuntu-26.04.9-desktop-amd64.iso", "ubuntu-26.04.1-desktop-amd64.iso", "ubuntu-26.04-desktop-amd64.iso" },
            c.Select(x => x.Key));
        Assert.True(IsoCatalog.CompareVersions(IsoCatalog.VersionKey("ubuntu-26.04.1-desktop-amd64.iso"), IsoCatalog.VersionKey("ubuntu-26.04-desktop-amd64.iso")) > 0);
    }

    [Theory]
    [InlineData("ubuntu-26.04.1-desktop-amd64.iso", "26.04.1")]
    [InlineData("debian-13.7.0-amd64-netinst.iso", "13.7.0")]
    [InlineData("Fedora-Workstation-Live-44-1.7.x86_64.iso", "44")]
    [InlineData("linuxmint-22.3-cinnamon-64bit.iso", "22.3")]
    [InlineData("AlmaLinux-10.1-x86_64-minimal.iso", "10.1")]
    public void VersionFromFile_extracts_display_version(string file, string expected) =>
        Assert.Equal(expected, IsoCatalog.VersionFromFile(file));

    [Fact]
    public void HighestDir_picks_numeric_maximum()
    {
        var html = "<a href=\"../\">..</a><a href=\"21.3/\">21.3/</a><a href=\"22.3/\">22.3/</a><a href=\"22.10/\">x</a><a href=\"beta/\">b</a><a href=\"22.2/\">y</a>";
        Assert.Equal("22.10", IsoCatalog.HighestDir(html));
        Assert.Null(IsoCatalog.HighestDir("<a href=\"beta/\">b</a>"));
    }

    [Fact]
    public void Catalog_has_checksummed_linux_and_windows_entries()
    {
        var linux = IsoCatalog.Entries.Where(e => e.Os == OsKind.Linux).ToList();
        Assert.True(linux.Count >= 6);
        Assert.All(linux, e => Assert.False(e.IsLinkOnly));
        var win = IsoCatalog.Entries.Where(e => e.Os == OsKind.Windows).ToList();
        Assert.NotEmpty(win);
        Assert.Contains(win, e => e.Id.Contains("windows-11") && e.IsDownloadable);
        Assert.Contains(win, e => e.Id.Contains("windows-10") && e.IsDownloadable);
        Assert.All(win, e =>
        {
            var host = new Uri(e.HomePage).Host;
            Assert.True(host.EndsWith("microsoft.com", StringComparison.OrdinalIgnoreCase), host);
        });
        var mac = IsoCatalog.Entries.Where(e => e.IsUnsupported).ToList();
        Assert.NotEmpty(mac);
        Assert.Equal(IsoCatalog.Entries.Count, IsoCatalog.Entries.Select(e => e.Id).Distinct().Count());
    }
}

/// <summary>Local HTTP server serving one payload with Range/If-Range support.</summary>
internal sealed class TestFileServer : IDisposable
{
    private readonly HttpListener _l = new();
    private readonly CancellationTokenSource _cts = new();
    public byte[] Data { get; set; }
    public string ETag { get; set; } = "\"v1\"";
    public int Requests;
    public int RangeRequests;
    /// <summary>When set, the response is cut off after this many bytes of body.</summary>
    public long? CutAfter { get; set; }
    /// <summary>Delay between 64 KiB chunks.</summary>
    public TimeSpan ChunkDelay { get; set; } = TimeSpan.Zero;
    public string Url { get; }

    public TestFileServer(byte[] data)
    {
        Data = data;
        var port = 0;
        for (var attempt = 0; ; attempt++)
        {
            port = Random.Shared.Next(20000, 60000);
            try { _l.Prefixes.Clear(); _l.Prefixes.Add($"http://127.0.0.1:{port}/"); _l.Start(); break; }
            catch (HttpListenerException) when (attempt < 20) { }
        }
        Url = $"http://127.0.0.1:{port}/file.iso";
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        Interlocked.Increment(ref Requests);
        var res = ctx.Response;
        try
        {
            long start = 0;
            var range = ctx.Request.Headers["Range"];
            var ifRange = ctx.Request.Headers["If-Range"];
            res.Headers["ETag"] = ETag;
            res.Headers["Accept-Ranges"] = "bytes";
            if (range != null && (ifRange == null || ifRange == ETag))
            {
                Interlocked.Increment(ref RangeRequests);
                start = long.Parse(range.Replace("bytes=", "").Split('-')[0]);
                if (start >= Data.Length) { res.StatusCode = 416; res.Close(); return; }
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {start}-{Data.Length - 1}/{Data.Length}";
            }
            res.ContentLength64 = Data.Length - start;
            if (ctx.Request.HttpMethod == "HEAD") { res.Close(); return; }
            var sent = 0L;
            for (var off = start; off < Data.Length; off += 65536)
            {
                var n = (int)Math.Min(65536, Data.Length - off);
                if (CutAfter is { } cut && sent + n > cut) { res.Abort(); return; }
                await res.OutputStream.WriteAsync(Data.AsMemory((int)off, n));
                sent += n;
                if (ChunkDelay > TimeSpan.Zero) await Task.Delay(ChunkDelay);
            }
            res.Close();
        }
        catch { try { res.Abort(); } catch { } }
    }

    public void Dispose() { _cts.Cancel(); try { _l.Stop(); _l.Close(); } catch { } }
}

public class IsoDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winvm-dl-" + Guid.NewGuid().ToString("N"));
    public IsoDownloaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Payload(int size) { var b = new byte[size]; new Random(42).NextBytes(b); return b; }
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Downloads_verifies_and_writes_sidecar()
    {
        var data = Payload(700_000);
        using var srv = new TestFileServer(data);
        var final = Path.Combine(_dir, "a.iso");
        var stages = new List<DownloadStage>();
        var p = await new IsoDownloader(new HttpClient()).DownloadAsync(srv.Url, final, Sha(data), new SyncProgress<DownloadProgress>(x => stages.Add(x.Stage)), default);
        Assert.Equal(final, p);
        Assert.Equal(data, await File.ReadAllBytesAsync(final));
        Assert.False(File.Exists(IsoDownloader.PartPath(final)));
        Assert.False(File.Exists(IsoDownloader.MetaPath(final)));
        Assert.True(IsoDownloader.IsVerified(final, Sha(data)));
        Assert.Contains(DownloadStage.Verifying, stages);
        Assert.Equal(DownloadStage.Completed, stages[^1]);
    }

    [Fact]
    public async Task Cancel_keeps_partial_and_next_run_resumes_with_range()
    {
        var data = Payload(2_000_000);
        using var srv = new TestFileServer(data) { ChunkDelay = TimeSpan.FromMilliseconds(15) };
        var final = Path.Combine(_dir, "b.iso");
        using var cts = new CancellationTokenSource();
        var dl = new IsoDownloader(new HttpClient());
        var prog = new SyncProgress<DownloadProgress>(x => { if (x.Received > 500_000) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dl.DownloadAsync(srv.Url, final, Sha(data), prog, cts.Token));
        var partial = IsoDownloader.PartialBytes(final);
        Assert.InRange(partial, 400_000, data.Length - 1);
        Assert.False(File.Exists(final));

        srv.ChunkDelay = TimeSpan.Zero;
        await dl.DownloadAsync(srv.Url, final, Sha(data), null, default);
        Assert.Equal(data, await File.ReadAllBytesAsync(final));
        Assert.True(srv.RangeRequests >= 1, "resume must use a Range request");
    }

    [Fact]
    public async Task Dropped_connection_is_retried_and_resumed()
    {
        var data = Payload(1_000_000);
        using var srv = new TestFileServer(data) { CutAfter = 300_000 };
        var final = Path.Combine(_dir, "c.iso");
        var dl = new IsoDownloader(new HttpClient());
        var t = dl.DownloadAsync(srv.Url, final, Sha(data), null, default);
        await Task.Delay(300);
        srv.CutAfter = null;
        await t;
        Assert.Equal(data, await File.ReadAllBytesAsync(final));
        Assert.True(srv.Requests >= 2);
    }

    [Fact]
    public async Task Checksum_mismatch_deletes_file_and_reports_chinese_error()
    {
        var data = Payload(300_000);
        using var srv = new TestFileServer(data);
        var final = Path.Combine(_dir, "d.iso");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => new IsoDownloader(new HttpClient()).DownloadAsync(srv.Url, final, new string('0', 64), null, default));
        Assert.Contains("校验", ex.Message);
        Assert.False(File.Exists(final));
        Assert.False(File.Exists(IsoDownloader.PartPath(final)));
    }

    [Fact]
    public async Task Changed_file_on_server_restarts_instead_of_corrupting()
    {
        var oldData = Payload(800_000);
        using var srv = new TestFileServer(oldData) { ChunkDelay = TimeSpan.FromMilliseconds(40) };
        var final = Path.Combine(_dir, "e.iso");
        using var cts = new CancellationTokenSource();
        var dl = new IsoDownloader(new HttpClient());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dl.DownloadAsync(srv.Url, final, null,
            new SyncProgress<DownloadProgress>(x => { if (x.Received > 200_000) cts.Cancel(); }), cts.Token));
        var newData = Payload(900_000); newData[0] ^= 0xFF; newData[^1] ^= 0xFF;
        srv.Data = newData; srv.ETag = "\"v2\""; srv.ChunkDelay = TimeSpan.Zero;
        await dl.DownloadAsync(srv.Url, final, Sha(newData), null, default);
        Assert.Equal(newData, await File.ReadAllBytesAsync(final));
    }
}

internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) => _a = a;
    public void Report(T value) { lock (this) _a(value); }
}

public class PerfAndModelTests
{
    [Fact]
    public void PerfHistory_keeps_last_60_samples_in_order()
    {
        var h = new PerfHistory();
        Assert.Equal("正在收集数据…", h.CpuStatsText);
        for (var i = 0; i < 75; i++) h.Push(i, i * 1024.0 * 1024, 4096.0 * 1024 * 1024);
        Assert.Equal(PerfHistory.Capacity, h.Count);
        Assert.Equal(15, h.CpuSeries[0]);
        Assert.Equal(74, h.CpuSeries[^1]);
        Assert.Equal(4096.0 * 1024 * 1024, h.MemoryMax);
        Assert.Contains("峰值 74%", h.CpuStatsText);
        h.Push(250, -5, 0);
        Assert.Equal(100, h.CpuSeries[^1]);
        Assert.Equal(0, h.MemorySeries[^1]);
        h.Clear();
        Assert.Equal(0, h.Count);
        Assert.Empty(h.CpuSeries);
    }

    [Fact]
    public void Task_item_reports_progress_and_cancels()
    {
        var cancelled = false;
        var t = new BackgroundTaskItem { Title = "导出", OnCancel = () => cancelled = true };
        t.Report(-1, "准备中");
        Assert.True(t.IsIndeterminate);
        t.Report(0.42, "正在复制");
        Assert.False(t.IsIndeterminate);
        Assert.Equal("42%", t.ProgressText);
        t.Cancel();
        Assert.True(t.Token.IsCancellationRequested);
        Assert.True(cancelled);
    }

    [Fact]
    public void Running_vm_subtitle_shows_cpu_and_uptime()
    {
        var v = new VmInfo { Name = "a", State = VmState.Running, CpuUsage = 12, UptimeSeconds = 3700, MemoryAssignedBytes = 2L << 30, ProcessorCount = 2 };
        Assert.Contains("CPU 12%", v.ListSubtitle);
        Assert.Equal("2 GiB", v.MemoryAssignedShort);
        Assert.True(v.HasLivePerf);
        var off = new VmInfo { Name = "b", State = VmState.Off };
        Assert.True(off.CanClone && off.CanExport);
        Assert.False(v.CanClone || v.CanExport);
    }

    [Fact]
    public void Host_hint_explains_insider_signing_issue_without_changing_settings()
    {
        var t = HostInfo.HostCannotStartExplanation(27902, "CanaryChannel");
        Assert.Contains("0x80090006", t);
        Assert.Contains("27902", t);
        Assert.Contains("不会修改", t);
        Assert.True(HostInfo.LooksLikeInsider(27902, null));
        Assert.True(HostInfo.LooksLikeInsider(26100, "Dev"));
        Assert.False(HostInfo.LooksLikeInsider(26100, null));
    }
}

public class V14ViewModelTests
{
    private static (MainViewModel vm, DemoHyperVService hv, FakeDialogs dlg, AppPreferences prefs) Make()
    {
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { LongOpDuration = TimeSpan.FromMilliseconds(200) };
        var dlg = new FakeDialogs();
        var prefs = TestPrefs.Isolated();
        var vm = new MainViewModel(hv, dlg, prefs, useTimer: false);
        return (vm, hv, dlg, prefs);
    }

    private static VmInfo SelectOff(MainViewModel vm)
    {
        var off = vm.Vms.First(v => v.State == VmState.Off);
        vm.SelectedVm = off;
        return off;
    }

    [Fact]
    public async Task Clone_creates_copy_with_new_id_and_shows_toast()
    {
        var (vm, hv, dlg, _) = Make();
        await vm.InitializeAsync();
        var src = SelectOff(vm);
        dlg.PromptResult = src.Name + " 克隆";
        Assert.True(vm.CloneCommand.CanExecute(null));
        await vm.CloneCommand.ExecuteAsync(null);
        var copy = vm.Vms.Single(v => v.Name == src.Name + " 克隆");
        Assert.NotEqual(src.Id, copy.Id);
        Assert.Same(copy, vm.SelectedVm);
        Assert.Empty(vm.Tasks);
        Assert.Contains(vm.Toasts, t => t.Title == "克隆完成");
        Assert.Empty(dlg.Errors);
        Assert.Equal(6, (await hv.ListVmsAsync()).Count);
    }

    [Fact]
    public async Task Clone_is_disabled_for_running_vm_and_rejects_duplicate_names()
    {
        var (vm, _, dlg, _) = Make();
        await vm.InitializeAsync();
        vm.SelectedVm = vm.Vms.First(v => v.State == VmState.Running);
        Assert.False(vm.CloneCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        var off = SelectOff(vm);
        dlg.PromptResult = vm.Vms.First(v => v != off).Name;
        await vm.CloneCommand.ExecuteAsync(null);
        Assert.Single(dlg.Errors);
        Assert.Equal(5, vm.Vms.Count);
    }

    [Fact]
    public async Task Export_then_import_round_trip()
    {
        var (vm, hv, dlg, prefs) = Make();
        await vm.InitializeAsync();
        var src = SelectOff(vm);
        dlg.FolderResult = @"D:\Exports";
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(@"D:\Exports", prefs.LastExportFolder);
        var exported = Assert.Single(hv.Exports).Key;
        Assert.Contains(vm.Toasts, t => t.Title == "导出完成");

        dlg.FolderResult = exported;
        dlg.PromptResult = "导入副本";
        Assert.True(vm.ImportCommand.CanExecute(null));
        await vm.ImportCommand.ExecuteAsync(null);
        var imp = vm.Vms.Single(v => v.Name == "导入副本");
        Assert.NotEqual(src.Id, imp.Id);
        Assert.Same(imp, vm.SelectedVm);
        Assert.Empty(dlg.Errors);
    }

    [Fact]
    public async Task Cancelling_a_running_clone_leaves_no_copy_and_unlocks_vm()
    {
        var (vm, hv, dlg, _) = Make();
        hv.LongOpDuration = TimeSpan.FromSeconds(5);
        await vm.InitializeAsync();
        var src = SelectOff(vm);
        dlg.PromptResult = "不会出现";
        var run = vm.CloneCommand.ExecuteAsync(null);
        for (var i = 0; i < 50 && vm.Tasks.Count == 0; i++) await Task.Delay(20);
        var task = Assert.Single(vm.Tasks);
        Assert.True(vm.SelectedIsLocked);
        Assert.False(vm.DeleteCommand.CanExecute(null));
        task.Cancel();
        await run;
        Assert.Empty(vm.Tasks);
        Assert.DoesNotContain(vm.Vms, v => v.Name == "不会出现");
        Assert.False(vm.SelectedIsLocked);
        Assert.Contains(vm.Toasts, t => t.Title == "克隆已取消");
        Assert.Empty(dlg.Errors);
    }

    [Fact]
    public async Task Failed_import_shows_error_dialog_with_detail()
    {
        var (vm, _, dlg, _) = Make();
        await vm.InitializeAsync();
        dlg.FolderResult = @"D:\nothing-here";
        dlg.PromptResult = "x1";
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Single(dlg.Errors);
        Assert.Contains(vm.Toasts, t => t.IsError);
    }

    [Fact]
    public async Task Refreshes_feed_perf_history_from_the_poll()
    {
        var (vm, hv, _, _) = Make();
        hv.SimulateLoad = true;
        await vm.InitializeAsync();
        for (var i = 0; i < 5; i++) await vm.RefreshCoreAsync(force: true, manual: false);
        var running = vm.Vms.First(v => v.State == VmState.Running);
        Assert.True(running.Perf.Count >= 5, "count=" + running.Perf.Count);
        var off = vm.Vms.First(v => v.State == VmState.Off);
        Assert.Equal(0, off.Perf.Count);
    }

    [Fact]
    public async Task Notifications_pref_suppresses_success_toasts_but_not_errors()
    {
        var (vm, _, _, prefs) = Make();
        await vm.InitializeAsync();
        prefs.ShowNotifications = false;
        vm.Notify(ToastKind.Success, "ok", "");
        Assert.Empty(vm.Toasts);
        vm.Notify(ToastKind.Error, "bad", "x");
        Assert.Single(vm.Toasts);
        prefs.ShowNotifications = true;
        for (var i = 0; i < 6; i++) vm.Notify(ToastKind.Info, "n" + i, "");
        Assert.Equal(4, vm.Toasts.Count);
    }

    [Fact]
    public void UniqueVmName_appends_number()
    {
        var (vm, _, _, _) = Make();
        vm.InitializeAsync().GetAwaiter().GetResult();
        var n = vm.Vms[0].Name;
        Assert.Equal(n + " 2", vm.UniqueVmName(n));
        Assert.Equal("全新名字", vm.UniqueVmName("全新名字"));
    }

    [Fact]
    public void Preferences_dialog_round_trips_and_keeps_auto_elevate()
    {
        var prefs = TestPrefs.Isolated();
        prefs.AutoElevate = true;
        prefs.RefreshSeconds = 10;
        var p = new PreferencesViewModel(prefs, @"C:\VMs");
        Assert.True(p.AutoElevate);
        Assert.Equal(2, p.RefreshIndex);
        Assert.False(p.MinimizeToTray);
        p.MinimizeToTray = true;
        p.ShowTrayIcon = false;           // turning the tray off also turns minimize-to-tray off
        Assert.False(p.MinimizeToTray);
        p.ThemeIndex = 2;
        p.RefreshIndex = 0;
        p.Apply();
        Assert.True(prefs.AutoElevate);
        Assert.Equal("dark", prefs.Theme);
        Assert.Equal(2, prefs.RefreshSeconds);
        Assert.False(prefs.ShowTrayIcon);
        Assert.False(prefs.MinimizeToTray);
        var p2 = new PreferencesViewModel(prefs, @"C:\VMs") { AutoElevate = false };
        p2.Apply();
        Assert.False(prefs.AutoElevate);
    }

    [Fact]
    public void Defaults_for_new_preferences()
    {
        var prefs = new AppPreferences();
        Assert.False(prefs.MinimizeToTray);
        Assert.True(prefs.ShowTrayIcon);
        Assert.True(prefs.ShowNotifications);
        Assert.Equal(4, prefs.EffectiveRefreshSeconds);
        prefs.RefreshSeconds = 1;
        Assert.Equal(2, prefs.EffectiveRefreshSeconds);
    }

    [Fact]
    public async Task Gallery_pick_applies_recommended_sizing_and_name()
    {
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero);
        var c = new CreateVmViewModel(hv, TestPrefs.Isolated());
        var entry = IsoCatalog.Find("ubuntu-desktop-lts")!;
        c.ApplyImage(new ImagePick(@"C:\Users\a\WinVM\ISO\ubuntu-26.04.1-desktop-amd64.iso", entry));
        Assert.Equal("Ubuntu 26.04.1", c.Name);
        Assert.Equal(entry.RecommendedCpu, c.ProcessorCount);
        Assert.Equal(entry.RecommendedMemoryMiB / 1024m, c.MemoryGiB);
        Assert.Equal(0, c.OsIndex);
        Assert.Contains("建议配置", c.ImageHint);
        c.Name = "我的名字";
        c.ApplyImage(new ImagePick(@"C:\x\debian-13.7.0-amd64-netinst.iso", IsoCatalog.Find("debian-netinst")));
        Assert.Equal("我的名字", c.Name);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Gallery_command_creates_vm_with_picked_image()
    {
        var (vm, _, dlg, _) = Make();
        await vm.InitializeAsync();
        dlg.GalleryResult = new ImagePick(@"C:\iso\debian-13.7.0-amd64-netinst.iso", IsoCatalog.Find("debian-netinst"));
        await vm.ShowImageGalleryCommand.ExecuteAsync(null);
        Assert.Equal(1, dlg.GalleryShown);
        Assert.Equal(dlg.GalleryResult, dlg.LastCreateImage);
    }

    [Fact]
    public void Shortcut_list_covers_menu_accelerators()
    {
        var keys = MainViewModel.Shortcuts.Select(s => s.Keys).ToList();
        foreach (var k in new[] { "Ctrl+N", "F5", "Ctrl+I", "Ctrl+E", "Ctrl+D", "Ctrl+,", "F1", "Ctrl+Q" })
            Assert.Contains(keys, x => x.Contains(k));
    }

    [Fact]
    public async Task Dispose_is_fast_even_with_task_in_flight()
    {
        var (vm, hv, dlg, _) = Make();
        hv.LongOpDuration = TimeSpan.FromSeconds(30);
        await vm.InitializeAsync();
        SelectOff(vm);
        dlg.PromptResult = "inflight";
        _ = vm.CloneCommand.ExecuteAsync(null);
        for (var i = 0; i < 50 && vm.Tasks.Count == 0; i++) await Task.Delay(20);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 500, "dispose took " + sw.ElapsedMilliseconds);
    }
}

public class OneShotProcessTests
{
    [Fact]
    public void One_shot_start_info_is_startable()
    {
        // Regression (found by the elevated self-test on the PC): StandardInputEncoding without a redirected
        // stdin made Process.Start throw, so export/import/clone could not run at all.
        var psi = PsSession.CreateOneShotStartInfo("true", "'x'");
        Assert.False(psi.RedirectStandardInput);
        Assert.Null(psi.StandardInputEncoding);
        if (OperatingSystem.IsWindows()) return;
        psi.FileName = "/bin/true";
        psi.ArgumentList.Clear();
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(5000);
        Assert.Equal(0, p.ExitCode);
    }
}

public class LongOpScriptTests
{
    [Fact]
    public async Task Export_import_clone_scripts_run_isolated_and_parse_results()
    {
        TestPrefs.Isolated();
        var ps = new FakePsRunner
        {
            Responder = s => s.Contains("'CLONED:'") ? new PsResult(true, "@@TOTAL 100\nCLONED:11111111-2222-3333-4444-555555555555", "")
                : s.Contains("'IMPORTED:'") ? new PsResult(true, "IMPORTED:aaaaaaaa-2222-3333-4444-555555555555", "")
                : s.Contains("'EXPORTED:'") ? new PsResult(true, @"EXPORTED:C:\exp\vm1", "")
                : new PsResult(true, "OK", "")
        };
        var hv = new HyperVService(ps, isWindows: true);
        var dir = Environment.GetEnvironmentVariable("WINVM_DUMP_PS");
        Assert.Equal(@"C:\exp\vm1", await hv.ExportVmAsync("vm-id-1", @"C:\exp"));
        Assert.Equal("aaaaaaaa-2222-3333-4444-555555555555", await hv.ImportVmAsync(@"C:\exp\vm1", "新名字"));
        Assert.Equal("11111111-2222-3333-4444-555555555555", await hv.CloneVmAsync("vm-id-1", "克隆 1"));
        var scripts = ps.Calls.Select(c => c.Script).Where(s => s.Contains("Test-WinVMCancel")).ToList();
        Assert.Equal(3, scripts.Count);
        Assert.All(scripts, s => Assert.Contains("-AsJob", s));
        Assert.Contains(scripts, s => s.Contains("-GenerateNewId"));
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
            for (var i = 0; i < scripts.Count; i++) File.WriteAllText(Path.Combine(dir, $"longop-{i}.ps1"), scripts[i], new System.Text.UTF8Encoding(true));
        }
    }
}
