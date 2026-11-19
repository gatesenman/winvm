using System.Diagnostics;
using System.Text;

namespace WinVM.Services;

public readonly record struct PsResult(bool Ok, string Output, string Error);

public enum PsLane
{
    /// <summary>Read-only queries (refresh, lists).</summary>
    Query,
    /// <summary>State-changing VM actions.</summary>
    Action
}

public interface IPsRunner : IDisposable
{
    Task<PsResult> RunAsync(string script, TimeSpan timeout, PsLane lane = PsLane.Query, CancellationToken ct = default);

    /// <summary>Ask for data that is not older than "now" on the next call (e.g. manual refresh).</summary>
    void RequestFresh() { }

    /// <summary>Runs a long script in its own one-shot process (export/import/clone), so it never blocks the
    /// shared host. Every stdout line is passed to <paramref name="onLine"/> as it arrives. Cancelling the token
    /// kills the process tree (callers implement graceful cancellation with a flag file first).</summary>
    Task<PsResult> RunIsolatedAsync(string script, TimeSpan timeout, Action<string>? onLine = null, CancellationToken ct = default) =>
        RunAsync(script, timeout, PsLane.Action, ct);
}

/// <summary>
/// Runs PowerShell scripts through a long-lived powershell.exe host, so the Hyper-V module is loaded once
/// instead of on every call (saves ~1 s per call). Each script is sent as one Base64 line on stdin; the host
/// replies with one marker line. Falls back to one-shot processes if the persistent host cannot start.
///
/// Queries and actions deliberately share ONE host: the Hyper-V module caches VM objects per process and
/// updates them asynchronously (names never), so a separate query process would show stale data right after
/// an action (found by the 1.3 end-to-end test). To pick up changes made outside WinVM the host is recycled
/// after <see cref="MaxHostAge"/> (a pre-started spare takes over without delay) or on <see cref="RequestFresh"/>.
/// </summary>
public sealed class PowerShellRunner : IPsRunner
{
    public static TimeSpan MaxHostAge { get; set; } = TimeSpan.FromSeconds(60);
    private readonly PsSession _session;

    public PowerShellRunner()
    {
        _session = new PsSession(FindPowerShell(), "ps");
    }

    /// <summary>Starts the host in the background (module warm-up) to speed up the first refresh.</summary>
    public void Warmup() => _ = _session.RunAsync(PsSession.WarmupScript, TimeSpan.FromSeconds(60), PsLane.Query, default)
        .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    public Task<PsResult> RunAsync(string script, TimeSpan timeout, PsLane lane = PsLane.Query, CancellationToken ct = default) =>
        _session.RunAsync(script, timeout, lane, ct);

    public Task<PsResult> RunIsolatedAsync(string script, TimeSpan timeout, Action<string>? onLine = null, CancellationToken ct = default) =>
        _session.RunOneShotAsync(script, timeout, ct, onLine);

    public void RequestFresh() => _session.RequestRecycle();

    public void Dispose() => _session.Dispose();

    public static string FindPowerShell()
    {
        if (!OperatingSystem.IsWindows()) return "pwsh";
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var winps = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(winps) ? winps : "powershell.exe";
    }

    internal const string OkMarker = "@@WINVM_OK ";
    internal const string ErrMarker = "@@WINVM_ERR ";

    /// <summary>Host loop executed inside the persistent powershell.exe.</summary>
    internal const string HostScript = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}
$utf8 = New-Object System.Text.UTF8Encoding($false)
$stdin = [Console]::In
while ($true) {
  $line = $stdin.ReadLine()
  if ($null -eq $line) { break }
  if ($line.Length -eq 0) { continue }
  $ok = $true
  $text = ''
  try {
    $src = $utf8.GetString([Convert]::FromBase64String($line))
    $sb = [ScriptBlock]::Create($src)
    $out = @(& $sb)
    $text = ($out | ForEach-Object { if ($null -eq $_) { '' } else { [string]$_ } }) -join "`n"
  } catch {
    $ok = $false
    $text = $_.Exception.Message
  }
  $b64 = [Convert]::ToBase64String($utf8.GetBytes([string]$text))
  if ($ok) { [Console]::Out.WriteLine('@@WINVM_OK ' + $b64) } else { [Console]::Out.WriteLine('@@WINVM_ERR ' + $b64) }
  [Console]::Out.Flush()
}
""";

    internal static ProcessStartInfo CreateStartInfo(string exe, string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return psi;
    }
}

/// <summary>One persistent powershell.exe running <see cref="PowerShellRunner.HostScript"/>.</summary>
internal sealed class PsHost : IDisposable
{
    private readonly StringBuilder _stderr = new();
    private readonly Process _proc;
    private TaskCompletionSource<PsResult>? _pending;
    public DateTime Started { get; } = DateTime.UtcNow;
    public int Pid => _proc.Id;
    public bool Alive { get { try { return !_proc.HasExited; } catch { return false; } } }

    public PsHost(string exe)
    {
        var psi = PowerShellRunner.CreateStartInfo(exe, PowerShellRunner.HostScript);
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            var line = e.Data;
            bool ok;
            if (line.StartsWith(PowerShellRunner.OkMarker, StringComparison.Ordinal)) ok = true;
            else if (line.StartsWith(PowerShellRunner.ErrMarker, StringComparison.Ordinal)) ok = false;
            else return; // stray host output (Write-Host etc.)
            var b64 = line[(ok ? PowerShellRunner.OkMarker.Length : PowerShellRunner.ErrMarker.Length)..];
            string text;
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim())); }
            catch { text = ""; }
            string err;
            lock (_stderr) { err = _stderr.ToString(); _stderr.Clear(); }
            _pending?.TrySetResult(ok ? new PsResult(true, text, err) : new PsResult(false, "", text));
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_stderr) { if (_stderr.Length < 64 * 1024) _stderr.AppendLine(e.Data); }
        };
        _proc.Exited += (_, _) => _pending?.TrySetException(new PsHostDiedException("PowerShell 进程已退出", beforeSend: false));
        _proc.Start(); // throws if powershell.exe cannot start
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    /// <summary>Sends one script; not thread-safe (callers serialize).</summary>
    public async Task<PsResult> SendAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        lock (_stderr) _stderr.Clear();
        var line = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(script));
        try
        {
            await _proc.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Kill();
            throw new PsHostDiedException(ex.Message, beforeSend: true);
        }
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
        _pending = null;
        if (done != tcs.Task)
        {
            Kill();
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("PowerShell 操作超时");
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Terminates the host immediately. Only the host itself: anything it launched on purpose
    /// (none today) must survive, and enumerating the tree costs time on shutdown.</summary>
    public void Kill()
    {
        try { if (!_proc.HasExited) _proc.Kill(); } catch { }
    }

    /// <summary>Fails a pending call right away (used on shutdown so awaiting code unwinds immediately).</summary>
    public void Abort() => _pending?.TrySetException(new OperationCanceledException("WinVM 正在退出"));

    /// <summary>Fast, non-blocking teardown: the host holds no state worth a graceful exit, so it is killed.</summary>
    public void Dispose()
    {
        Abort();
        Kill();
        try { _proc.Dispose(); } catch { }
    }
}

internal sealed class PsSession : IDisposable
{
    /// <summary>Warm-up only loads the module; it must not cache VM objects (they would go stale).</summary>
    internal const string WarmupScript = "Import-Module Hyper-V -ErrorAction SilentlyContinue; 'ok'";

    private readonly string _exe;
    private readonly string _label;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PsHost? _host;
    private Task<PsHost?>? _spare;
    private volatile bool _recycleRequested;
    private bool _persistentBroken;
    private int _hostDeaths;
    private volatile bool _disposed;
    private readonly CancellationTokenSource _life = new();
    private readonly List<Process> _oneShots = new();

    public PsSession(string exe, string label)
    {
        _exe = exe;
        _label = label;
    }

    public void RequestRecycle() => _recycleRequested = true;

    public async Task<PsResult> RunAsync(string script, TimeSpan timeout, PsLane lane, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        ct = linked.Token;
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_disposed) { throw new OperationCanceledException("WinVM 正在退出"); }
        var sw = Stopwatch.StartNew();
        try
        {
            if (_disposed) throw new OperationCanceledException("WinVM 正在退出");
            if (!_persistentBroken)
            {
                try
                {
                    var host = await GetHostAsync().ConfigureAwait(false);
                    if (_disposed) throw new OperationCanceledException("WinVM 正在退出");
                    var r = await host.SendAsync(script, timeout, ct).ConfigureAwait(false);
                    _hostDeaths = 0;
                    if (!r.Ok && IsStaleHyperVConnection(r.Error))
                    {
                        // vmms.exe was restarted (e.g. after a crash): every host started before that keeps a dead
                        // WMI connection. The call failed before reaching Hyper-V, so retrying once on a new host is safe.
                        Log.Warn($"PS[{_label}] Hyper-V connection is stale (VMMS restarted?); recycling host and spare");
                        DropHost();
                        DropSpare();
                        host = await GetHostAsync().ConfigureAwait(false);
                        r = await host.SendAsync(script, timeout, ct).ConfigureAwait(false);
                    }
                    MaybeStartSpare();
                    return r;
                }
                catch (PsHostDiedException ex)
                {
                    DropHost();
                    if (_disposed) throw new OperationCanceledException("WinVM 正在退出");
                    if (++_hostDeaths >= 3) _persistentBroken = true;
                    // A state-changing script that already reached the host may have run (partly): never repeat it.
                    if (!ex.BeforeSend && lane == PsLane.Action)
                    {
                        Log.Warn($"PS[{_label}] host died during an action ({ex.Message}); not retried");
                        throw new HyperVException(HvErrorKind.Unknown, "PowerShell 进程意外退出，操作结果未知。请刷新列表确认。", ex.Message);
                    }
                    Log.Warn($"PS[{_label}] host died ({ex.Message}); retrying one-shot");
                }
                catch (TimeoutException)
                {
                    DropHost(); // killed by SendAsync; next call starts a new host
                    throw;
                }
            }
            return await RunOneShotAsync(script, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            Log.Write("DEBUG", $"PS[{_label}] {sw.ElapsedMilliseconds} ms: {FirstLine(script)}");
            try { _gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task<PsHost> GetHostAsync()
    {
        var expired = _host != null && DateTime.UtcNow - _host.Started > PowerShellRunner.MaxHostAge;
        if (_host != null && _host.Alive && !expired && !_recycleRequested) return _host;

        // Swap to the pre-started spare when it is ready; an expired host keeps serving until then
        // (bounded staleness) unless fresh data was explicitly requested.
        if (_spare is { IsCompleted: true })
        {
            var spare = _spare.Result;
            _spare = null;
            if (spare is { Alive: true })
            {
                ReplaceHost(spare, _recycleRequested ? "fresh data requested" : "age limit");
                return spare;
            }
            spare?.Dispose();
        }
        if (_host != null && _host.Alive && !_recycleRequested) return _host; // expired, spare still starting

        if (_spare != null)
        {
            var spare = await _spare.ConfigureAwait(false);
            _spare = null;
            if (spare is { Alive: true }) { ReplaceHost(spare, "fresh data requested"); return spare; }
            spare?.Dispose();
        }
        PsHost fresh;
        try { fresh = new PsHost(_exe); }
        catch (Exception ex)
        {
            _persistentBroken = true;
            throw new HyperVException(HvErrorKind.PowerShellUnavailable, "无法启动 PowerShell：" + ex.Message, ex.ToString());
        }
        ReplaceHost(fresh, _host == null ? "start" : "restart");
        return fresh;
    }

    private void ReplaceHost(PsHost next, string reason)
    {
        var old = _host;
        _host = next;
        _recycleRequested = false;
        Log.Info($"PS[{_label}] host pid={next.Pid} ({reason})");
        if (old != null) _ = Task.Run(old.Dispose);
    }

    /// <summary>Hyper-V's "object not found … verify the Virtual Machine Management service is running" error,
    /// which a PowerShell process keeps returning after vmms.exe restarted.</summary>
    internal static bool IsStaleHyperVConnection(string? error) =>
        !string.IsNullOrEmpty(error) && (error.Contains("虚拟机管理服务是否正在运行") ||
            error.Contains("Virtual Machine Management service on the computer", StringComparison.OrdinalIgnoreCase));

    private void DropSpare()
    {
        var spare = _spare;
        _spare = null;
        if (spare != null) _ = spare.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); }, TaskScheduler.Default);
    }

    private void DropHost()
    {
        var old = _host;
        _host = null;
        if (old != null) { old.Kill(); old.Dispose(); }
    }

    private void MaybeStartSpare()
    {
        if (_spare != null || _host == null || _disposed) return;
        if (DateTime.UtcNow - _host.Started < PowerShellRunner.MaxHostAge * 0.75) return;
        var exe = _exe;
        _spare = Task.Run<PsHost?>(async () =>
        {
            PsHost? h = null;
            try
            {
                h = new PsHost(exe);
                var r = await h.SendAsync(WarmupScript, TimeSpan.FromSeconds(60), default).ConfigureAwait(false);
                return h;
            }
            catch (Exception ex)
            {
                Log.Warn($"PS[{_label}] spare host failed: {ex.Message}");
                h?.Dispose();
                return null;
            }
        });
    }

    private static string FirstLine(string s)
    {
        foreach (var l in s.Split('\n'))
        {
            var t = l.Trim();
            if (t.Length > 0 && !t.StartsWith("$ErrorActionPreference") && !t.StartsWith("#") && !t.StartsWith("function Resolve-WinVM")
                && !t.StartsWith("$g = ") && !t.StartsWith("if (") && !t.StartsWith("$v = ") && !t.StartsWith("}") && !t.StartsWith("$all ")
                && !t.StartsWith("return "))
                return t.Length > 90 ? t[..90] : t;
        }
        return "";
    }

    /// <summary>Start info for a one-shot process: stdin is not redirected (the script is on the command line).</summary>
    internal static ProcessStartInfo CreateOneShotStartInfo(string exe, string script)
    {
        var psi = PowerShellRunner.CreateStartInfo(exe, script);
        psi.RedirectStandardInput = false;
        psi.StandardInputEncoding = null; // only valid with a redirected stdin (Process.Start throws otherwise)
        return psi;
    }

    internal async Task<PsResult> RunOneShotAsync(string script, TimeSpan timeout, CancellationToken ct, Action<string>? onLine = null)
    {
        if (_disposed) throw new OperationCanceledException("WinVM 正在退出");
        using var linkedLife = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        ct = linkedLife.Token;
        var wrapped = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}
try {
""" + "\n" + script + "\n" + """
} catch {
  [Console]::Error.WriteLine('@@WINVM_ERR ' + [Convert]::ToBase64String((New-Object System.Text.UTF8Encoding($false)).GetBytes([string]$_.Exception.Message)))
  exit 1
}
""";
        var psi = CreateOneShotStartInfo(_exe, wrapped);
        using var proc = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (so) so.AppendLine(e.Data);
            if (onLine != null) try { onLine(e.Data); } catch { }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        try { proc.Start(); }
        catch (Exception ex)
        {
            throw new HyperVException(HvErrorKind.PowerShellUnavailable, "无法启动 PowerShell：" + ex.Message, ex.ToString());
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        lock (_oneShots) _oneShots.Add(proc);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("PowerShell 操作超时");
        }
        finally
        {
            lock (_oneShots) _oneShots.Remove(proc);
        }
        // Drain the async readers (WaitForExit(ms) does not wait for them).
        try { proc.WaitForExit(); } catch { }
        var stdout = so.ToString().TrimEnd();
        var stderr = se.ToString();
        if (proc.ExitCode == 0) return new PsResult(true, stdout, stderr);
        var idx = stderr.IndexOf(PowerShellRunner.ErrMarker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var b64 = stderr[(idx + PowerShellRunner.ErrMarker.Length)..].Split('\n')[0].Trim();
            try { return new PsResult(false, stdout, Encoding.UTF8.GetString(Convert.FromBase64String(b64))); }
            catch { }
        }
        return new PsResult(false, stdout, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }

    /// <summary>Immediate shutdown: in-flight calls fail with <see cref="OperationCanceledException"/>,
    /// no call is retried, all PowerShell processes are terminated. Never blocks.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _life.Cancel(); } catch { }
        var h = _host;
        _host = null;
        DisposeQuietly(h);
        if (_spare != null) _ = _spare.ContinueWith(t => DisposeQuietly(t.IsCompletedSuccessfully ? t.Result : null));
        Process[] shots;
        lock (_oneShots) shots = _oneShots.ToArray();
        foreach (var p in shots) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }
    }

    private static void DisposeQuietly(PsHost? h) { try { h?.Dispose(); } catch { } }
}

internal sealed class PsHostDiedException : Exception
{
    /// <summary>True when the script never reached the host (safe to run again elsewhere).</summary>
    public bool BeforeSend { get; }
    public PsHostDiedException(string message, bool beforeSend) : base(message) { BeforeSend = beforeSend; }
}
