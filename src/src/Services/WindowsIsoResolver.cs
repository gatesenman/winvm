using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinVM.Services;

/// <summary>
/// Resolves a real Microsoft CDN ISO URL via the official software-download-connector JSON API
/// (same flow as community Fido.ps1): session whitelist → ov-df handshake → SKUs → download links.
/// Prefer Chinese Simplified (zh-CN) and x64. Falls back to English when zh-CN is unavailable.
/// </summary>
public static class WindowsIsoResolver
{
    public const string OrgId = "y6jn8c31";
    public const string ProfileId = "606624d44113";
    public const string InstanceId = "560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175";

    /// <summary>Windows 11 Home/Pro/Edu consumer editions (x64 + ARM64 product IDs).</summary>
    public static readonly int[] Win11Consumer = { 3813, 3816 };
    /// <summary>Windows 11 Home China (shown when locale is zh*).</summary>
    public static readonly int[] Win11HomeChina = { 3814, 3817 };
    /// <summary>Windows 10 22H2 Home/Pro/Edu.</summary>
    public static readonly int[] Win10Consumer = { 2618 };

    public static Task<ResolvedIso> ResolveWindows11Async(HttpClient http, CancellationToken ct) =>
        ResolveAsync(http, PreferChinaEditions(Win11Consumer, Win11HomeChina), "Windows 11",
            "https://www.microsoft.com/zh-cn/software-download/windows11", ct);

    public static Task<ResolvedIso> ResolveWindows10Async(HttpClient http, CancellationToken ct) =>
        ResolveAsync(http, Win10Consumer, "Windows 10",
            "https://www.microsoft.com/zh-cn/software-download/windows10ISO", ct);

    private static int[] PreferChinaEditions(int[] consumer, int[] china)
    {
        var loc = System.Globalization.CultureInfo.CurrentUICulture.Name;
        return loc.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? china.Concat(consumer).ToArray()
            : consumer;
    }

    public static async Task<ResolvedIso> ResolveAsync(HttpClient http, IReadOnlyList<int> productEditionIds,
        string productLabel, string referer, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var editionId in productEditionIds)
        {
            ct.ThrowIfCancellationRequested();
            var sessionId = Guid.NewGuid().ToString();
            try
            {
                await RegisterSessionAsync(http, sessionId, ct).ConfigureAwait(false);
                var skus = await GetSkusAsync(http, editionId, sessionId, ct).ConfigureAwait(false);
                if (skus.Count == 0) continue;
                var sku = PickPreferredSku(skus)
                          ?? throw new IsoException($"Microsoft 未返回可用语言的 {productLabel} SKU。");
                var links = await GetDownloadLinksAsync(http, sku.Id, sessionId, referer, ct).ConfigureAwait(false);
                var link = PickX64(links)
                           ?? throw new IsoException($"Microsoft 未返回 {productLabel} 的 x64 ISO 下载链接。");
                var fileName = FileNameFromUri(link.Uri) ?? $"{productLabel.Replace(' ', '_')}_x64.iso";
                long? size = null;
                try { size = await IsoCatalog.ProbeAsync(http, link.Uri, ct).ConfigureAwait(false); }
                catch { /* size optional */ }
                if (size is < 0) size = null;
                var version = GuessVersion(fileName, productLabel);
                return new ResolvedIso(fileName, link.Uri, Sha256: null, size, version);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                Log.Warn($"Windows ISO resolve edition={editionId}: {ex.Message}");
            }
        }
        var detail = last is IsoException ie ? ie.Message : last?.Message ?? "未知错误";
        throw new IsoException(
            $"无法从 Microsoft 获取 {productLabel} 在线下载链接（{detail}）。" +
            "可打开官网手动下载，或使用「本地 ISO」。", last);
    }

    /// <summary>vlscppe session whitelist + ov-df handshake (required since ~2026).</summary>
    public static async Task RegisterSessionAsync(HttpClient http, string sessionId, CancellationToken ct)
    {
        using (var r = await http.GetAsync(
                   $"https://vlscppe.microsoft.com/tags?org_id={OrgId}&session_id={sessionId}", ct)
                   .ConfigureAwait(false))
        { /* 200 or redirect is fine; ignore body */ }

        string mdt;
        using (var r = await http.GetAsync(
                   $"https://ov-df.microsoft.com/mdt.js?instanceId={InstanceId}&PageId=si&session_id={sessionId}", ct)
                   .ConfigureAwait(false))
        {
            mdt = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        var w = Regex.Match(mdt, @"[?&]w=([A-Fa-f0-9]+)").Groups[1].Value;
        var rticks = Regex.Match(mdt, @"rticks\s*=\s*""\+?(\d+)").Groups[1].Value;
        if (string.IsNullOrEmpty(w) || string.IsNullOrEmpty(rticks))
            throw new IsoException("Microsoft 下载会话握手失败（ov-df）。请稍后重试或打开官网下载。");

        var mdtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var r = await http.GetAsync(
                   $"https://ov-df.microsoft.com/?session_id={sessionId}&CustomerId={InstanceId}&PageId=si&w={w}&mdt={mdtMs}&rticks={rticks}",
                   ct).ConfigureAwait(false))
        { /* ignore */ }
    }

    public static async Task<IReadOnlyList<WindowsSku>> GetSkusAsync(HttpClient http, int productEditionId,
        string sessionId, CancellationToken ct)
    {
        var url =
            "https://www.microsoft.com/software-download-connector/api/getskuinformationbyproductedition" +
            $"?profile={ProfileId}&productEditionId={productEditionId}&SKU=undefined&friendlyFileName=undefined" +
            $"&Locale=zh-CN&sessionID={sessionId}";
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(2000, ct).ConfigureAwait(false);
            try
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new IsoException($"Microsoft SKU API HTTP {(int)resp.StatusCode}");
                return ParseSkus(body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        throw last ?? new IsoException("无法获取 Microsoft SKU 列表。");
    }

    public static async Task<IReadOnlyList<WindowsDownloadLink>> GetDownloadLinksAsync(HttpClient http,
        string skuId, string sessionId, string referer, CancellationToken ct)
    {
        var url =
            "https://www.microsoft.com/software-download-connector/api/GetProductDownloadLinksBySku" +
            $"?profile={ProfileId}&productEditionId=undefined&SKU={Uri.EscapeDataString(skuId)}" +
            $"&friendlyFileName=undefined&Locale=zh-CN&sessionID={sessionId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IsoException($"Microsoft 下载链接 API HTTP {(int)resp.StatusCode}");
        return ParseDownloadLinks(body);
    }

    /// <summary>Parse getskuinformationbyproductedition JSON. Exposed for unit tests.</summary>
    public static IReadOnlyList<WindowsSku> ParseSkus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("Errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var msg = errors[0].TryGetProperty("Value", out var v) ? v.GetString() : "SKU 错误";
            if (msg != null && msg.Contains("715-123130", StringComparison.Ordinal))
                throw new IsoException("Microsoft 暂时限制了此 IP 的 ISO 下载请求（715-123130）。请稍后再试，或打开官网手动下载。");
            throw new IsoException(msg ?? "Microsoft SKU 错误");
        }
        if (!root.TryGetProperty("Skus", out var skus) || skus.ValueKind != JsonValueKind.Array)
            return Array.Empty<WindowsSku>();
        var list = new List<WindowsSku>();
        foreach (var s in skus.EnumerateArray())
        {
            var id = s.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;
            var lang = s.TryGetProperty("Language", out var l) ? l.GetString() ?? "" : "";
            var localized = s.TryGetProperty("LocalizedLanguage", out var ll) ? ll.GetString() ?? lang : lang;
            list.Add(new WindowsSku(id!, lang, localized));
        }
        return list;
    }

    /// <summary>Parse GetProductDownloadLinksBySku JSON. Exposed for unit tests.</summary>
    public static IReadOnlyList<WindowsDownloadLink> ParseDownloadLinks(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("Errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var type = errors[0].TryGetProperty("Type", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
            var msg = errors[0].TryGetProperty("Value", out var v) ? v.GetString() : "下载链接错误";
            if (type == 9 || (msg != null && msg.Contains("715-123130", StringComparison.Ordinal)))
                throw new IsoException("Microsoft 暂时限制了此 IP 的 ISO 下载请求（715-123130）。请稍后再试，或打开官网手动下载。");
            throw new IsoException(msg ?? "Microsoft 下载链接错误");
        }
        if (!root.TryGetProperty("ProductDownloadOptions", out var opts) || opts.ValueKind != JsonValueKind.Array)
            return Array.Empty<WindowsDownloadLink>();
        var list = new List<WindowsDownloadLink>();
        foreach (var o in opts.EnumerateArray())
        {
            var uri = o.TryGetProperty("Uri", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(uri)) continue;
            var dtype = o.TryGetProperty("DownloadType", out var d) && d.TryGetInt32(out var di) ? di : -1;
            list.Add(new WindowsDownloadLink(ArchFromType(dtype), uri!));
        }
        return list;
    }

    public static WindowsSku? PickPreferredSku(IReadOnlyList<WindowsSku> skus)
    {
        // Prefer Simplified Chinese, then any Chinese, then English, then first.
        static int Score(WindowsSku s)
        {
            var n = (s.Language + " " + s.LocalizedLanguage).ToLowerInvariant();
            if (n.Contains("zh-cn") || (n.Contains("chinese") && n.Contains("simp")) || n.Contains("简体"))
                return 100;
            if (n.Contains("chinese") || n.Contains("中文") || n.StartsWith("zh"))
                return 80;
            if (n.Contains("en-us") || n == "english" || n.StartsWith("english "))
                return 50;
            if (n.Contains("english"))
                return 40;
            return 0;
        }
        return skus.OrderByDescending(Score).ThenBy(s => s.LocalizedLanguage, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public static WindowsDownloadLink? PickX64(IReadOnlyList<WindowsDownloadLink> links) =>
        links.FirstOrDefault(l => l.Arch.Equals("x64", StringComparison.OrdinalIgnoreCase))
        ?? links.FirstOrDefault();

    public static string ArchFromType(int type) => type switch
    {
        0 => "x86",
        1 => "x64",
        2 => "ARM64",
        _ => "Unknown"
    };

    public static string? FileNameFromUri(string uri)
    {
        try
        {
            var path = new Uri(uri).AbsolutePath;
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(name) && name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(name);
            // CDN sometimes puts the name in a query parameter
            var q = new Uri(uri).Query;
            var m = Regex.Match(q, @"[?&](?:fileName|filename|FileName)=([^&]+\.iso)", RegexOptions.IgnoreCase);
            if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        }
        catch { /* ignore */ }
        return null;
    }

    private static string GuessVersion(string fileName, string productLabel)
    {
        var m = Regex.Match(fileName, @"(\d{2}H[12]|\d{4}|\d+\.\d+)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return productLabel.Contains("11", StringComparison.Ordinal) ? "11" : "10";
    }
}

public sealed record WindowsSku(string Id, string Language, string LocalizedLanguage);
public sealed record WindowsDownloadLink(string Arch, string Uri);
