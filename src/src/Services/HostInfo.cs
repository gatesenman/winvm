namespace WinVM.Services;

/// <summary>Facts about the host OS used for explanations (never changes anything).</summary>
public static class HostInfo
{
    public static int Build => OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0;

    /// <summary>Windows Insider channel name (Canary/Dev/Beta/ReleasePreview) when enrolled, else null.</summary>
    public static string? InsiderChannel
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
                var branch = k?.GetValue("BranchName") as string;
                if (!string.IsNullOrWhiteSpace(branch)) return branch;
                using var u = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\UI\Selection");
                var ch = u?.GetValue("UIBranch") as string;
                return string.IsNullOrWhiteSpace(ch) ? null : ch;
            }
            catch { return null; }
        }
    }

    /// <summary>Insider enrolment, or a build number beyond the public releases (Canary builds are 27xxx).</summary>
    public static bool LooksLikeInsider(int build, string? channel) => !string.IsNullOrEmpty(channel) || build >= 27000;

    /// <summary>Detailed explanation for the "host cannot start any VM" (0x80090006) failure.</summary>
    public static string HostCannotStartExplanation(int build, string? channel)
    {
        var insider = LooksLikeInsider(build, channel);
        var host = build > 0 ? $"此电脑运行的是 Windows 内部版本 {build}" + (insider ? $"（Windows 预览体验计划{(string.IsNullOrEmpty(channel) ? "" : " · " + channel + " 频道")}）" : "") + "。"
                             : "";
        return host + "\n\n" +
               "Hyper-V 在启动虚拟机时需要加载微软签名的虚拟机固件（Microsoft Virtual BIOS / UEFI）。错误 0x80090006 表示该固件文件的签名校验失败，" +
               "因此这台主机上的所有虚拟机都无法启动——与虚拟机本身的配置无关，WinVM 也无法绕过。" +
               (insider ? "这是预览版系统中的已知问题类型。" : "") + "\n\n" +
               "建议：\n" +
               "1. 打开「设置 → Windows 更新」，安装所有可用更新后重启（新版本通常会修复此问题）。\n" +
               (insider
                   ? "2. 若仍无法启动，可在「设置 → Windows 更新 → Windows 预览体验计划」中改用 Release Preview 频道，或退出预览体验计划并安装正式版 Windows。\n"
                   : "2. 若仍无法启动，可尝试在「启用或关闭 Windows 功能」中关闭再重新启用 Hyper-V（需要重启）。\n") +
               "3. 修复后回到 WinVM 点击「启动」即可，虚拟机及其数据不受影响。\n\n" +
               "WinVM 不会修改任何主机设置。";
    }
}
