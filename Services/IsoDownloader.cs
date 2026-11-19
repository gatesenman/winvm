using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinVM.Services;

public enum DownloadStage { Connecting, Downloading, Verifying, Completed }

public readonly record struct DownloadProgress(DownloadStage Stage, long Received, long? Total, double BytesPerSecond)
{
    public double Fraction => Total is > 0 ? Math.Clamp(Received / (double)Total.Value, 0, 1) : -1;
}

/// <summary>
/// Resumable HTTP download with SHA-256 verification.
/// <list type="bullet">
/// <item>Data goes to <c>file.iso.part</c>; a <c>.part.json</c> sidecar remembers URL, ETag/Last-Modified and size,
/// so a resumed download never mixes bytes of two different files (If-Range).</item>
/// <item>Cancel keeps the partial file; the next call resumes with a Range request.</item>
/// <item>After download the whole file is hashed; on mismatch the partial data is deleted.</item>
/// <item>A verified file gets a <c>.sha256</c> sidecar, so it is recognised later without re-hashing.</item>
/// </list>
/// </summary>
public sealed class IsoDownloader
{
    private readonly HttpClient _http;
    public IsoDownloader(HttpClient? http = null) => _http = http ?? IsoCatalog.CreateHttpClient();

    public static string PartPath(string final) => final + ".part";
    public static string MetaPath(string final) => final + ".part.json";
    public static string HashSidecar(string final) => final + ".sha256";

    private sealed record PartMeta(string Url, string? ETag, string? LastModified, long? Total);

    /// <summary>True when <paramref name="final"/> exists and was verified against <paramref name="sha256"/> before.</summary>
    public static bool IsVerified(string final, string? sha256)
    {
        try
        {
            if (!File.Exists(final)) return false;
            var side = HashSidecar(final);
            if (sha256 == null)
                return File.Exists(side); // written as "ok" after a no-hash download completes
            return File.Exists(side) && File.ReadAllText(side).Trim().StartsWith(sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static long PartialBytes(string final)
    {
        try { var p = PartPath(final); return File.Exists(p) ? new FileInfo(p).Length : 0; }
        catch { return 0; }
    }

    public async Task<string> DownloadAsync(string url, string final, string? sha256, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        if (IsVerified(final, sha256))
        {
            progress?.Report(new DownloadProgress(DownloadStage.Completed, new FileInfo(final).Length, new FileInfo(final).Length, 0));
            return final;
        }
        var part = PartPath(final);
        var metaPath = MetaPath(final);
        PartMeta? meta = null;
        try { if (File.Exists(metaPath)) meta = JsonSerializer.Deserialize<PartMeta>(File.ReadAllText(metaPath)); } catch { }
        if (meta != null && meta.Url != url) meta = null; // a different release: start over
        long have = meta != null && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have == 0 && File.Exists(part)) File.Delete(part);
        if (meta != null && meta.Total is long tt && have > tt) { File.Delete(part); have = 0; }

        progress?.Report(new DownloadProgress(DownloadStage.Connecting, have, meta?.Total, 0));
        long? total = meta?.Total;
        var attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (total is long t0 && have >= t0) break;
            try
            {
                (have, total) = await DownloadOnceAsync(url, part, metaPath, have, meta, progress, ct).ConfigureAwait(false);
                meta = ReadMeta(metaPath);
                if (have == 0 && total == null)
                {
                    // stale partial data was discarded (HTTP 416): start over once more
                    if (++attempts >= 4) throw new IsoException("服务器拒绝断点续传请求，请稍后重试。");
                    continue;
                }
                if (total == null || have >= total) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                // Network hiccup: keep the partial data and retry a few times with backoff.
                if (++attempts >= 4) throw new IsoException("下载中断：" + (ex is HttpRequestException h ? IsoCatalog.Describe(h) : ex.Message) + "。已保留已下载部分，可稍后继续。", ex);
                Log.Warn($"ISO download retry {attempts}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempts), ct).ConfigureAwait(false);
                have = File.Exists(part) ? new FileInfo(part).Length : 0;
                meta = ReadMeta(metaPath);
            }
        }

        if (sha256 != null)
        {
            progress?.Report(new DownloadProgress(DownloadStage.Verifying, 0, have, 0));
            var actual = await HashFileAsync(part, have, progress, ct).ConfigureAwait(false);
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(part); TryDelete(metaPath);
                throw new IsoException($"校验失败：文件的 SHA-256 与官方发布值不一致（可能下载损坏或被篡改），已删除，请重新下载。\n期望：{sha256}\n实际：{actual}");
            }
        }
        if (File.Exists(final)) File.Delete(final);
        File.Move(part, final);
        TryDelete(metaPath);
        if (sha256 != null) File.WriteAllText(HashSidecar(final), sha256.ToLowerInvariant() + " *" + Path.GetFileName(final) + "\n");
        else File.WriteAllText(HashSidecar(final), "ok *" + Path.GetFileName(final) + "\n");
        progress?.Report(new DownloadProgress(DownloadStage.Completed, have, have, 0));
        Log.Info($"ISO downloaded and verified: {final} ({have} bytes)");
        return final;
    }

    private static PartMeta? ReadMeta(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PartMeta>(File.ReadAllText(path)) : null; } catch { return null; }
    }

    private async Task<(long Have, long? Total)> DownloadOnceAsync(string url, string part, string metaPath, long have, PartMeta? meta,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
        {
            req.Headers.Range = new RangeHeaderValue(have, null);
            if (meta?.ETag is { } et && EntityTagHeaderValue.TryParse(et, out var etag) && !etag.IsWeak)
                req.Headers.IfRange = new RangeConditionHeaderValue(etag);
            else if (meta?.LastModified is { } lm && DateTimeOffset.TryParse(lm, out var lmd))
                req.Headers.IfRange = new RangeConditionHeaderValue(lmd);
        }
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException("连接超时"); }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
            {
                // Server says our range is past the end: the partial file is complete or stale. Start clean.
                TryDelete(part); TryDelete(metaPath);
                return (0, null);
            }
            if (!resp.IsSuccessStatusCode)
                throw new IsoException(resp.StatusCode == HttpStatusCode.NotFound
                    ? "官方服务器上找不到该文件（可能已发布新版本），请重新打开镜像库刷新。"
                    : $"服务器返回错误 HTTP {(int)resp.StatusCode}。");
            var append = resp.StatusCode == HttpStatusCode.PartialContent && have > 0;
            if (!append) have = 0; // full response (resume unsupported or file changed): restart
            long? total = append
                ? resp.Content.Headers.ContentRange?.Length ?? (resp.Content.Headers.ContentLength + have)
                : resp.Content.Headers.ContentLength;
            var newMeta = new PartMeta(url, resp.Headers.ETag?.ToString(), resp.Content.Headers.LastModified?.ToString("R"), total);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(newMeta));

            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true);
            var buf = new byte[1 << 20];
            var sw = Stopwatch.StartNew();
            long windowBytes = 0; var windowStart = 0L; double speed = 0;
            var lastReport = -1000L;
            while (true)
            {
                // Stall detection: no data for 60 s → treat as network error (retried with resume).
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(60));
                int n;
                try { n = await src.ReadAsync(buf, readCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("网络长时间无响应"); }
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                have += n; windowBytes += n;
                var now = sw.ElapsedMilliseconds;
                if (now - windowStart >= 1000)
                {
                    var inst = windowBytes * 1000.0 / Math.Max(1, now - windowStart);
                    speed = speed <= 0 ? inst : speed * 0.6 + inst * 0.4;
                    windowBytes = 0; windowStart = now;
                }
                if (now - lastReport >= 250)
                {
                    lastReport = now;
                    progress?.Report(new DownloadProgress(DownloadStage.Downloading, have, total, speed));
                }
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(DownloadStage.Downloading, have, total, speed));
            if (total is long t && have < t) throw new IOException("连接提前关闭");
            return (have, total ?? have);
        }
    }

    public static async Task<string> HashFileAsync(string path, long total, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buf = new byte[1 << 20];
        long done = 0; var sw = Stopwatch.StartNew(); long last = -1000;
        int n;
        while ((n = await fs.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buf, 0, n, null, 0);
            done += n;
            if (sw.ElapsedMilliseconds - last >= 250)
            {
                last = sw.ElapsedMilliseconds;
                progress?.Report(new DownloadProgress(DownloadStage.Verifying, done, total, done * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds)));
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
