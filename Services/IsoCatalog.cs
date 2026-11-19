using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using WinVM.Models;

namespace WinVM.Services;

/// <summary>A downloadable image resolved to a concrete file on an official server.</summary>
public sealed record ResolvedIso(string FileName, string Url, string? Sha256, long? Size, string Version);

/// <summary>One entry of the system image gallery.</summary>
public sealed class IsoEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Family { get; init; }
    public required string Description { get; init; }
    public OsKind Os { get; init; } = OsKind.Linux;
    /// <summary>Approximate download size shown before resolution, e.g. "约 6 GB".</summary>
    public string SizeHint { get; init; } = "";
    /// <summary>Recommended VM sizing for this system.</summary>
    public int RecommendedCpu { get; init; } = 2;
    public int RecommendedMemoryMiB { get; init; } = 4096;
    public int RecommendedDiskGiB { get; init; } = 40;
    /// <summary>Gen2 Secure Boot works with the Microsoft UEFI CA template for this system.</summary>
    public bool SecureBootCapable { get; init; } = true;
    /// <summary>Windows 11: creation requires TPM + Secure Boot (see CreateVmRequest.RequireTpm).</summary>
    public bool RequireTpm { get; init; }
    /// <summary>Official vendor / info page.</summary>
    public string HomePage { get; init; } = "";
    /// <summary>Null when online resolve is unavailable (Mac) or intentionally link-only.</summary>
    public Func<HttpClient, CancellationToken, Task<ResolvedIso>>? Resolve { get; init; }
    /// <summary>Hyper-V cannot run this guest (e.g. macOS). Shown as an info card with no download.</summary>
    public bool IsUnsupported { get; init; }
    public bool IsLinkOnly => Resolve == null && !IsUnsupported;
    public bool IsDownloadable => Resolve != null && !IsUnsupported;
}

/// <summary>Curated official images. URLs are resolved at runtime from the vendors' published checksum files,
/// so new point releases are picked up without an app update; every download is verified against SHA-256.</summary>
public static class IsoCatalog
{
    public static string DefaultIsoFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WinVM", "ISO");

    public static HttpClient CreateHttpClient()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinVM", HyperVService.AppVersion));
        return h;
    }

    public static IReadOnlyList<IsoEntry> Entries { get; } = new List<IsoEntry>
    {
        new()
        {
            Id = "ubuntu-desktop-lts", Family = "Ubuntu", Name = "Ubuntu 26.04 LTS 桌面版",
            Description = "最流行的 Linux 桌面发行版，长期支持 5 年。适合日常使用与开发。",
            SizeHint = "约 6 GB", RecommendedCpu = 2, RecommendedMemoryMiB = 4096, RecommendedDiskGiB = 40,
            HomePage = "https://ubuntu.com/download/desktop",
            Resolve = (h, ct) => FromSums(h, "https://releases.ubuntu.com/26.04/", "SHA256SUMS", @"^ubuntu-26\.04(\.\d+)*-desktop-amd64\.iso$", ct)
        },
        new()
        {
            Id = "ubuntu-server-lts", Family = "Ubuntu", Name = "Ubuntu Server 26.04 LTS",
            Description = "Ubuntu 服务器版（无桌面），适合搭建服务、容器与实验环境。",
            SizeHint = "约 3 GB", RecommendedCpu = 2, RecommendedMemoryMiB = 2048, RecommendedDiskGiB = 25,
            HomePage = "https://ubuntu.com/download/server",
            Resolve = (h, ct) => FromSums(h, "https://releases.ubuntu.com/26.04/", "SHA256SUMS", @"^ubuntu-26\.04(\.\d+)*-live-server-amd64\.iso$", ct)
        },
        new()
        {
            Id = "ubuntu-desktop-2404", Family = "Ubuntu", Name = "Ubuntu 24.04 LTS 桌面版",
            Description = "上一代长期支持版本，软件兼容性最好。",
            SizeHint = "约 6 GB", RecommendedCpu = 2, RecommendedMemoryMiB = 4096, RecommendedDiskGiB = 40,
            HomePage = "https://ubuntu.com/download/desktop",
            Resolve = (h, ct) => FromSums(h, "https://releases.ubuntu.com/24.04/", "SHA256SUMS", @"^ubuntu-24\.04(\.\d+)*-desktop-amd64\.iso$", ct)
        },
        new()
        {
            Id = "debian-netinst", Family = "Debian", Name = "Debian 稳定版（网络安装）",
            Description = "稳定可靠的通用发行版。小体积网络安装镜像，安装时联网下载所需软件包。",
            SizeHint = "约 0.8 GB", RecommendedCpu = 2, RecommendedMemoryMiB = 2048, RecommendedDiskGiB = 20,
            HomePage = "https://www.debian.org/download",
            Resolve = (h, ct) => FromSums(h, "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/", "SHA256SUMS", @"^debian-\d+(\.\d+)*-amd64-netinst\.iso$", ct)
        },
        new()
        {
            Id = "fedora-workstation", Family = "Fedora", Name = "Fedora Workstation",
            Description = "紧跟上游的 GNOME 桌面发行版，适合体验最新的 Linux 技术。",
            SizeHint = "约 3 GB", RecommendedCpu = 2, RecommendedMemoryMiB = 4096, RecommendedDiskGiB = 40,
            HomePage = "https://fedoraproject.org/workstation/download",
            Resolve = ResolveFedora
        },
        new()
        {
            Id = "linuxmint-cinnamon", Family = "Linux Mint", Name = "Linux Mint Cinnamon",
            Description = "基于 Ubuntu LTS，界面接近 Windows，上手容易。",
            SizeHint = "约 3 GB", RecommendedCpu = 2, RecommendedMemoryMiB = 4096, RecommendedDiskGiB = 30,
            HomePage = "https://linuxmint.com/download.php",
            Resolve = ResolveMint
        },
        new()
        {
            Id = "almalinux-minimal", Family = "AlmaLinux", Name = "AlmaLinux 10（最小安装）",
            Description = "与 RHEL 二进制兼容的企业级发行版，适合服务器与运维练习。",
            SizeHint = "约 1.6 GB", RecommendedCpu = 2, RecommendedMemoryMiB = 2048, RecommendedDiskGiB = 20,
            HomePage = "https://almalinux.org/get-almalinux/",
            Resolve = (h, ct) => FromSums(h, "https://repo.almalinux.org/almalinux/10/isos/x86_64/", "CHECKSUM", @"^AlmaLinux-10(\.\d+)+-x86_64-minimal\.iso$", ct)
        },
        new()
        {
            Id = "windows-11", Family = "Windows", Name = "Windows 11", Os = OsKind.Windows,
            Description = "一键从 Microsoft 官方通道解析并下载 ISO（优先简体中文），创建时启用 TPM 与安全启动。若在线解析失败可打开官网或使用本地 ISO。",
            SizeHint = "约 7 GB", RecommendedCpu = 4, RecommendedMemoryMiB = 8192, RecommendedDiskGiB = 80,
            RequireTpm = true, SecureBootCapable = true,
            HomePage = "https://www.microsoft.com/zh-cn/software-download/windows11",
            Resolve = WindowsIsoResolver.ResolveWindows11Async
        },
        new()
        {
            Id = "windows-10", Family = "Windows", Name = "Windows 10", Os = OsKind.Windows,
            Description = "一键从 Microsoft 官方通道解析并下载 ISO（优先简体中文）。若在线解析失败可打开官网或使用本地 ISO。",
            SizeHint = "约 5 GB", RecommendedCpu = 2, RecommendedMemoryMiB = 4096, RecommendedDiskGiB = 64,
            HomePage = "https://www.microsoft.com/zh-cn/software-download/windows10ISO",
            Resolve = WindowsIsoResolver.ResolveWindows10Async
        },
        new()
        {
            Id = "windows-server-2025", Family = "Windows", Name = "Windows Server 2025（评估版）", Os = OsKind.Windows,
            Description = "请从 Microsoft 评估中心下载 180 天评估版 ISO，然后使用「本地 ISO」或一键安装时选择已下载文件。",
            SizeHint = "约 6 GB", RecommendedCpu = 4, RecommendedMemoryMiB = 4096, RecommendedDiskGiB = 60,
            HomePage = "https://www.microsoft.com/zh-cn/evalcenter/download-windows-server-2025"
        },
        new()
        {
            Id = "macos-unsupported", Family = "macOS", Name = "macOS", Os = OsKind.Unknown,
            Description = "Hyper-V 无法运行 macOS 虚拟机：Apple 软件许可协议不允许在非 Apple 硬件上安装 macOS，且 Hyper-V 不提供 macOS 来宾支持。WinVM 不会提供或抓取 macOS 安装镜像。如需体验 macOS，请使用 Apple 电脑上的官方虚拟化方案（如 macOS 虚拟机 App / VirtualBuddy）。",
            SizeHint = "", RecommendedCpu = 0, RecommendedMemoryMiB = 0, RecommendedDiskGiB = 0,
            IsUnsupported = true, SecureBootCapable = false,
            HomePage = "https://support.apple.com/zh-cn/102624"
        }
    };

    public static IsoEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ resolvers

    /// <summary>Parses GNU (<c>hash *file</c> / <c>hash  file</c>) and BSD (<c>SHA256 (file) = hash</c>) checksum lists.
    /// PGP armour and comments are ignored.</summary>
    public static Dictionary<string, string> ParseSums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var bsd = Regex.Match(line, @"^SHA256\s*\((?<f>[^)]+)\)\s*=\s*(?<h>[0-9a-fA-F]{64})$");
            if (bsd.Success) { map[bsd.Groups["f"].Value.Trim()] = bsd.Groups["h"].Value.ToLowerInvariant(); continue; }
            var gnu = Regex.Match(line, @"^(?<h>[0-9a-fA-F]{64})\s+\*?(?<f>\S.*)$");
            if (gnu.Success) map[gnu.Groups["f"].Value.Trim()] = gnu.Groups["h"].Value.ToLowerInvariant();
        }
        return map;
    }

    /// <summary>Numeric version key for natural ordering of file names ("26.04.10" &gt; "26.04.9" &gt; "26.04").</summary>
    public static int[] VersionKey(string fileName)
    {
        var m = Regex.Match(fileName, @"\d+(\.\d+)*");
        return m.Success ? m.Value.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray() : Array.Empty<int>();
    }

    public static int CompareVersions(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : -1;
            var y = i < b.Length ? b[i] : -1;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>Candidates from a checksum list matching <paramref name="pattern"/>, newest first.</summary>
    public static List<KeyValuePair<string, string>> Candidates(Dictionary<string, string> sums, string pattern) =>
        sums.Where(kv => Regex.IsMatch(kv.Key, pattern, RegexOptions.IgnoreCase))
            .OrderByDescending(kv => VersionKey(kv.Key), Comparer<int[]>.Create(CompareVersions))
            .ToList();

    /// <summary>Version text shown to the user, derived from the file name.</summary>
    public static string VersionFromFile(string fileName)
    {
        var fedora = Regex.Match(fileName, @"-(\d+)-\d+(\.\d+)+\.x86_64");   // Fedora-…-44-1.7.x86_64.iso → 44
        if (fedora.Success) return fedora.Groups[1].Value;
        var m = Regex.Match(fileName, @"\d+(\.\d+)+");
        return m.Success ? m.Value : Regex.Match(fileName, @"\d+").Value;
    }

    internal static async Task<string> GetTextAsync(HttpClient h, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await h.GetStringAsync(url, cts.Token).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new IsoException($"无法访问官方服务器（{new Uri(url).Host}）：{Describe(ex)}", ex); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IsoException($"连接官方服务器（{new Uri(url).Host}）超时，请检查网络。"); }
    }

    /// <summary>HEAD request (falls back to a 1-byte ranged GET for servers that reject HEAD). Returns size or null; 404 → -1.</summary>
    internal static async Task<long?> ProbeAsync(HttpClient h, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var r = await h.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (r.StatusCode == HttpStatusCode.NotFound) return -1;
            if (r.IsSuccessStatusCode) return r.Content.Headers.ContentLength;
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        try
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var r = await h.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (r.StatusCode == HttpStatusCode.NotFound) return -1;
            return r.Content.Headers.ContentRange?.Length ?? r.Content.Headers.ContentLength;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Picks the newest file from <paramref name="sumsName"/> in <paramref name="baseUrl"/> that is actually
    /// present on the server (checksum lists sometimes keep older point releases that were removed).</summary>
    public static async Task<ResolvedIso> FromSums(HttpClient h, string baseUrl, string sumsName, string pattern, CancellationToken ct)
    {
        var sums = ParseSums(await GetTextAsync(h, baseUrl + sumsName, ct).ConfigureAwait(false));
        var cands = Candidates(sums, pattern);
        if (cands.Count == 0) throw new IsoException("官方校验文件中没有找到匹配的镜像，可能已发布新版本。请打开官网下载。");
        foreach (var (file, hash) in cands.Take(4))
        {
            var url = baseUrl + Uri.EscapeDataString(file);
            var size = await ProbeAsync(h, url, ct).ConfigureAwait(false);
            if (size == -1) continue; // listed but removed
            return new ResolvedIso(file, url, hash, size, VersionFromFile(file));
        }
        throw new IsoException("官方服务器上暂时找不到镜像文件，请稍后重试或打开官网下载。");
    }

    /// <summary>Highest-numbered sub-directory in an Apache/nginx index page (e.g. "22.3/").</summary>
    public static string? HighestDir(string indexHtml, string pattern = @"^\d+(\.\d+)*$")
    {
        var dirs = Regex.Matches(indexHtml, "href=\"(?<d>[^\"/?]+)/\"")
            .Select(m => m.Groups["d"].Value)
            .Where(d => Regex.IsMatch(d, pattern))
            .Distinct()
            .ToList();
        return dirs.OrderByDescending(VersionKey, Comparer<int[]>.Create(CompareVersions)).FirstOrDefault();
    }

    private static async Task<ResolvedIso> ResolveMint(HttpClient h, CancellationToken ct)
    {
        const string root = "https://mirrors.edge.kernel.org/linuxmint/stable/";
        var dir = HighestDir(await GetTextAsync(h, root, ct).ConfigureAwait(false))
                  ?? throw new IsoException("无法确定 Linux Mint 的最新版本，请打开官网下载。");
        return await FromSums(h, root + dir + "/", "sha256sum.txt", @"^linuxmint-[\d.]+-cinnamon-64bit\.iso$", ct).ConfigureAwait(false);
    }

    /// <summary>Fedora: the newest release directory that has a Workstation ISO (probing upward from a known release).</summary>
    private static async Task<ResolvedIso> ResolveFedora(HttpClient h, CancellationToken ct)
    {
        const string root = "https://dl.fedoraproject.org/pub/fedora/linux/releases/";
        string? listing = null;
        string? dirUrl = null;
        var index = await GetTextAsync(h, root, ct).ConfigureAwait(false);
        var versions = Regex.Matches(index, "href=\"(?<d>\\d+)/\"").Select(m => int.Parse(m.Groups["d"].Value)).Distinct().OrderByDescending(v => v).ToList();
        foreach (var v in versions.Take(3))
        {
            var u = $"{root}{v}/Workstation/x86_64/iso/";
            try { listing = await GetTextAsync(h, u, ct).ConfigureAwait(false); dirUrl = u; break; }
            catch (IsoException) { } // release directory without Workstation yet (pre-release)
        }
        if (listing == null || dirUrl == null) throw new IsoException("无法确定 Fedora 的最新版本，请打开官网下载。");
        var sumsFile = Regex.Match(listing, "href=\"(?<f>Fedora-Workstation-[^\"]*-CHECKSUM)\"").Groups["f"].Value;
        if (sumsFile.Length == 0) throw new IsoException("Fedora 官方目录中没有找到校验文件，请打开官网下载。");
        // Download from the mirror redirector (fast, nearby mirror); checksums come from the master server.
        var r = await FromSums(h, dirUrl, sumsFile, @"^Fedora-Workstation-Live-.*x86_64\.iso$", ct).ConfigureAwait(false);
        var mirrorUrl = r.Url.Replace("https://dl.fedoraproject.org/pub/", "https://download.fedoraproject.org/pub/", StringComparison.Ordinal);
        return r with { Url = mirrorUrl };
    }

    internal static string Describe(HttpRequestException ex) => ex.StatusCode is { } c
        ? $"HTTP {(int)c}"
        : (ex.InnerException?.Message ?? ex.Message);
}

public sealed class IsoException : Exception
{
    public IsoException(string message, Exception? inner = null) : base(message, inner) { }
}
