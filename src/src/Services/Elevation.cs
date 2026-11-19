using System.Diagnostics;
using System.Security.Principal;

namespace WinVM.Services;

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Relaunch this exe with the UAC "runas" verb. Returns false if the user cancelled.</summary>
    public static bool RelaunchElevated(IEnumerable<string>? args = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            foreach (var a in args ?? Array.Empty<string>()) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Log.Info("Relaunched elevated");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Elevation cancelled by user");
            return false;
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn("OpenFolder failed: " + ex.Message);
        }
    }

    /// <summary>Opens a web page in the default browser. Goes through explorer.exe so an elevated WinVM does not
    /// start the browser elevated.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { url }, UseShellExecute = false });
            Log.Info("Open URL " + url);
        }
        catch (Exception ex)
        {
            Log.Warn("OpenUrl failed: " + ex.Message);
        }
    }
}
