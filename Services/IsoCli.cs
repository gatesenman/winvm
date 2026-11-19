using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace WinVM.Services;

/// <summary>Command-line diagnostics for the image gallery (used by the end-to-end tests):
/// <c>--iso-probe [--out file]</c> resolves every entry and checks the file with HEAD + a 1 MiB ranged GET;
/// <c>--iso-download id [--dir d] [--max-bytes n]</c> downloads (optionally only n bytes, to test resume) and verifies.</summary>
public static class IsoCli
{
    public static int Probe(string? outPath)
    {
        var sb = new StringBuilder();
        void W(string s) { Console.WriteLine(s); sb.AppendLine(s); }
        using var http = IsoCatalog.CreateHttpClient();
        var failures = 0;
        W($"WinVM {HyperVService.AppVersion} ISO probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var e in IsoCatalog.Entries)
        {
            if (e.IsLinkOnly)
            {
                var st = Head(http, e.HomePage);
                W($"[link] {e.Id}: {e.HomePage} -> {st}");
                continue;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                var r = e.Resolve!(http, CancellationToken.None).GetAwaiter().GetResult();
                var range = RangeCheck(http, r.Url);
                W($"[ok]   {e.Id}: {r.FileName} size={r.Size} sha256={(r.Sha256 ?? "-")} range={range} ({sw.ElapsedMilliseconds} ms)\n       {r.Url}");
                if (!range.StartsWith("206")) failures++;
            }
            catch (Exception ex)
            {
                failures++;
                W($"[FAIL] {e.Id}: {ex.Message}");
            }
        }
        W(failures == 0 ? "ALL OK" : $"FAILURES {failures}");
        if (outPath != null) File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    private static string Head(HttpClient http, string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var r = http.Send(req, HttpCompletionOption.ResponseHeadersRead);
            return ((int)r.StatusCode).ToString();
        }
        catch (Exception ex) { return "error " + ex.Message; }
    }

    /// <summary>Requests the first MiB with a Range header; "206 1048576" means resumable downloads work.</summary>
    private static string RangeCheck(HttpClient http, string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 1024 * 1024 - 1);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var r = http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            using var s = r.Content.ReadAsStream(cts.Token);
            var buf = new byte[81920];
            long n = 0; int k;
            while ((k = s.Read(buf, 0, buf.Length)) > 0 && n < 2 * 1024 * 1024) n += k;
            return $"{(int)r.StatusCode} {n}";
        }
        catch (Exception ex) { return "error " + ex.Message; }
    }

    public static int Download(string id, string? dir, long maxBytes)
    {
        var e = IsoCatalog.Find(id);
        if (e?.Resolve == null) { Console.WriteLine($"unknown or link-only entry: {id}"); return 2; }
        dir ??= IsoCatalog.DefaultIsoFolder;
        using var http = IsoCatalog.CreateHttpClient();
        var r = e.Resolve(http, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"resolved {r.FileName} size={r.Size} sha256={r.Sha256}\n{r.Url}");
        var target = Path.Combine(dir, r.FileName);
        using var cts = new CancellationTokenSource();
        var last = DateTime.MinValue;
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (maxBytes > 0 && p.Stage == DownloadStage.Downloading && p.Received >= maxBytes && !cts.IsCancellationRequested)
            {
                Console.WriteLine($"stopping at {p.Received} bytes (resume test)");
                cts.Cancel();
            }
            if ((DateTime.Now - last).TotalSeconds >= 5 || p.Stage == DownloadStage.Completed)
            {
                last = DateTime.Now;
                Console.WriteLine($"{p.Stage} {p.Received}/{p.Total} {p.BytesPerSecond / 1048576:0.0} MB/s");
            }
        });
        var sw = Stopwatch.StartNew();
        try
        {
            var path = new IsoDownloader(http).DownloadAsync(r.Url, target, r.Sha256, progress, cts.Token).GetAwaiter().GetResult();
            Console.WriteLine($"OK verified {path} in {sw.Elapsed.TotalSeconds:0}s");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"PAUSED partial={IsoDownloader.PartialBytes(target)}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED " + ex.Message);
            return 1;
        }
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }
}
