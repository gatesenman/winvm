using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using WinVM.Services;

namespace WinVM;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        var opt = Parse(args);
        Log.Info($"WinVM {HyperVService.AppVersion} start args=[{string.Join(' ', args)}] elevated={Elevation.IsElevated} os={Environment.OSVersion}");

        if (opt.SelfTest) return RunSelfTest(opt);
        if (opt.IsoProbe) return IsoCli.Probe(opt.Out);
        if (opt.IsoDownload != null) return IsoCli.Download(opt.IsoDownload, opt.Dir, opt.MaxBytes);
        if (opt.ConnectInfoVm != null) return GuestConnectCli.ConnectInfo(opt.ConnectInfoVm, opt.Out, opt.GuestUser);
        if (opt.ListReady) return GuestConnectCli.ListReady(opt.Out);

        if (!opt.App.Demo && !opt.App.DemoNoHyperV && opt.App.ScreenshotDir == null && !opt.NoElevate &&
            AppPreferences.Shared.AutoElevate && OperatingSystem.IsWindows() && !Elevation.IsElevated)
        {
            if (Elevation.RelaunchElevated(args)) return 0;
        }

        try
        {
            App.Options = opt.App;
            var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            // Do not wait for anything else (thread pool work, finalizers of child-process handles…):
            // the UI is gone, preferences are saved, PowerShell processes were terminated.
            Log.Info($"Shutdown: exit code {code}");
            Environment.Exit(code);
            return code;
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            throw;
        }
    }

    private sealed class CliOptions
    {
        public bool SelfTest;
        public string? Out;
        public bool Elevate;
        public bool NoElevate;
        public bool IsoProbe;
        public string? IsoDownload;
        public string? Dir;
        public long MaxBytes;
        public string? ConnectInfoVm;
        public bool ListReady;
        public string? GuestUser;
        public AppOptions App = new();
    }

    private static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--selftest": o.SelfTest = true; break;
                case "--out" when i + 1 < args.Length: o.Out = args[++i]; break;
                case "--elevate": o.Elevate = true; break;
                case "--demo": o.App.Demo = true; break;
                case "--demo-nohyperv": o.App.DemoNoHyperV = true; break;
                case "--theme" when i + 1 < args.Length: o.App.Theme = args[++i]; break;
                case "--screenshot" when i + 1 < args.Length: o.App.ScreenshotDir = args[++i]; break;
                case "--no-elevate": o.NoElevate = true; break;
                case "--debug-inflight" when i + 1 < args.Length && int.TryParse(args[i + 1], out var sec): o.App.DebugInflightSeconds = sec; i++; break;
                case "--iso-probe": o.IsoProbe = true; break;
                case "--iso-download" when i + 1 < args.Length: o.IsoDownload = args[++i]; break;
                case "--connect-info" when i + 1 < args.Length: o.ConnectInfoVm = args[++i]; break;
                case "--list-ready": o.ListReady = true; break;
                case "--user" when i + 1 < args.Length: o.GuestUser = args[++i]; break;
                case "--dir" when i + 1 < args.Length: o.Dir = args[++i]; break;
                case "--max-bytes" when i + 1 < args.Length && long.TryParse(args[i + 1], out var mb): o.MaxBytes = mb; i++; break;
            }
        }
        return o;
    }

    private static int RunSelfTest(CliOptions opt)
    {
        if (opt.Elevate && OperatingSystem.IsWindows() && !Elevation.IsElevated)
        {
            // Re-run elevated and wait (UAC prompt); the elevated child writes the report.
            var exe = Environment.ProcessPath!;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--selftest");
            if (opt.Out != null) { psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(opt.Out); }
            if (opt.App.Demo) psi.ArgumentList.Add("--demo");
            try
            {
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p?.ExitCode ?? 3;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Warn("selftest elevation declined: " + ex.Message);
                return 4;
            }
        }

        IHyperVService hv;
        IPsRunner? raw = null;
        Func<string, bool>? fe = null, de = null;
        string mode;
        if (opt.App.Demo)
        {
            var demo = new DemoHyperVService(seed: true, delay: TimeSpan.Zero);
            hv = demo;
            fe = p => demo.Files.Contains(p);
            de = p => demo.Files.Any(f => f.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));
            mode = "demo";
        }
        else
        {
            raw = new PowerShellRunner();
            hv = new HyperVService(new PowerShellRunner());
            mode = "real";
        }
        var lines = new List<string>();
        var report = new SelfTest(hv, fe, de, raw, s => { lines.Add(s); Console.WriteLine(s); }, mode).RunAsync().GetAwaiter().GetResult();
        var path = SelfTest.Save(report, opt.Out);
        Console.WriteLine($"PASSED {report.Passed} FAILED {report.Failed} SKIPPED {report.Skipped} ENV {report.EnvBlocked} -> {path}");
        raw?.Dispose();
        (hv as IDisposable)?.Dispose();
        return report.Failed > 0 ? 1 : !(report.HyperVAvailable || mode == "demo") ? 2 : report.EnvBlocked > 0 ? 5 : 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software } })
            .LogToTrace();
}
