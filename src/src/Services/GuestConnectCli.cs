using System.Text.Json;
using WinVM.Models;

namespace WinVM.Services;

/// <summary>Headless helpers: <c>--connect-info &lt;vm&gt;</c> and <c>--list-ready</c> for agents on the same PC.</summary>
public static class GuestConnectCli
{
    public static int ConnectInfo(string vmKey, string? outPath, string? username)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("connect-info requires Windows + Hyper-V.");
            return 2;
        }
        try
        {
            using var hv = new HyperVService();
            var list = hv.ListVmsAsync().GetAwaiter().GetResult();
            var vm = list.FirstOrDefault(v =>
                v.Name.Equals(vmKey, StringComparison.OrdinalIgnoreCase) ||
                v.Id.Equals(vmKey, StringComparison.OrdinalIgnoreCase));
            if (vm == null)
            {
                Console.Error.WriteLine($"VM not found: {vmKey}");
                return 1;
            }
            var user = username ?? AppPreferences.Shared.LastGuestUser;
            var info = Services.ConnectInfo.FromVm(vm, user);
            var json = info.ToJson();
            if (!string.IsNullOrEmpty(outPath))
            {
                File.WriteAllText(outPath, json);
                Console.WriteLine(outPath);
            }
            else
                Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    public static int ListReady(string? outPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("list-ready requires Windows + Hyper-V.");
            return 2;
        }
        try
        {
            using var hv = new HyperVService();
            var list = hv.ListVmsAsync().GetAwaiter().GetResult();
            var user = AppPreferences.Shared.LastGuestUser;
            var ready = list
                .Where(v => v.State == VmState.Running && v.HasGuestIp)
                .Select(v => Services.ConnectInfo.FromVm(v, user))
                .ToList();
            var json = JsonSerializer.Serialize(ready, new JsonSerializerOptions { WriteIndented = true });
            if (!string.IsNullOrEmpty(outPath))
            {
                File.WriteAllText(outPath, json);
                Console.WriteLine(outPath);
            }
            else
                Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }
}
