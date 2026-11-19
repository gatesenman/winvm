using System.Text.RegularExpressions;

namespace WinVM.Services;

public enum HvErrorKind
{
    Unknown,
    Validation,
    AccessDenied,
    NotFound,
    AlreadyExists,
    InvalidState,
    GuestNoResponse,
    SwitchNotFound,
    CheckpointBlocksResize,
    ResizeTooSmall,
    InsufficientMemory,
    HypervisorNotRunning,
    /// <summary>The host cannot power on any VM (e.g. Hyper-V firmware/worker component fails to load).</summary>
    HostCannotStart,
    FileInUse,
    Timeout,
    PowerShellUnavailable,
    NotWindows
}

/// <summary>Error raised by the Hyper-V layer. <see cref="Exception.Message"/> is a short Chinese
/// message for the UI; <see cref="Detail"/> holds the raw PowerShell text (logged).</summary>
public sealed class HyperVException : Exception
{
    public HvErrorKind Kind { get; }
    public string Detail { get; }

    public HyperVException(HvErrorKind kind, string message, string detail = "") : base(message)
    {
        Kind = kind;
        Detail = detail;
    }
}

/// <summary>Maps raw PowerShell / Hyper-V error text (English or Chinese OS) to short Chinese messages.</summary>
public static class ErrorMapper
{
    /// <summary>Scripts prefix their own (already Chinese) validation errors with this marker.</summary>
    public const string OwnMarker = "WINVM:";

    private static bool M(string s, string pattern) =>
        Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static (HvErrorKind Kind, string Message) Map(string? raw)
    {
        var m = Clean(raw);
        if (m.Length == 0) return (HvErrorKind.Unknown, "");

        var own = m.IndexOf(OwnMarker, StringComparison.Ordinal);
        if (own >= 0)
        {
            var text = m[(own + OwnMarker.Length)..].Trim();
            var cut = text.IndexOf(OwnMarker, StringComparison.Ordinal);
            if (cut >= 0) text = text[..cut].Trim();
            return (HvErrorKind.Validation, text);
        }

        if (M(m, @"access is denied|拒绝访问|AccessDenied|没有完成此任务所需的权限|do not have the required permission|authorization policy|授权策略|requires elevation|需要提升"))
            return (HvErrorKind.AccessDenied, "没有权限访问 Hyper-V。请以管理员身份运行 WinVM，或将当前用户加入「Hyper-V Administrators」组。");
        if (M(m, @"hypervisor is not running|虚拟机监控程序未运行|virtualization support is disabled|虚拟化.*(禁用|未启用)"))
            return (HvErrorKind.HypervisorNotRunning, "虚拟机监控程序未运行。请在 BIOS 中开启虚拟化（VT-x/AMD-V），并确认 Hyper-V 已启用。");
        if (M(m, @"0x80090006|invalid signature|无效签名|firmware resource|固件资源|Virtual BIOS.*(签名|signature)"))
            return (HvErrorKind.HostCannotStart, "主机无法启动虚拟机：Hyper-V 固件组件加载失败（签名无效 0x80090006）。这是主机系统的问题（常见于 Insider 预览版），请更新 Windows 或修复 Hyper-V 后重试。");
        if (M(m, @"switch|交换机") && M(m, @"not found|找不到|does not exist|不存在|unable to find|无法找到"))
            return (HvErrorKind.SwitchNotFound, "找不到指定的虚拟交换机。请刷新交换机列表或在 Hyper-V 管理器中创建。");
        if (M(m, @"not enough memory|内存不足|insufficient system resources|系统资源不足|could not initialize memory"))
            return (HvErrorKind.InsufficientMemory, "主机可用内存不足，无法完成操作。请减少虚拟机内存或关闭其他程序。");
        if (M(m, @"checkpoint|检查点|snapshot|快照|avhdx|differencing|差异") && M(m, @"resize|扩容|调整大小|expand|扩展"))
            return (HvErrorKind.CheckpointBlocksResize, "存在检查点时无法扩容磁盘，请先删除检查点。");
        if (M(m, @"being used by another process|正由另一进程使用|另一个程序正在使用|is in use|正在使用"))
            return (HvErrorKind.FileInUse, "文件正被其他进程占用。请关闭相关程序后重试。");
        if (M(m, @"shutdown integration|关机集成|integration service|集成服务|guest.*not respond|来宾.*(未响应|不支持)|heartbeat|检测信号"))
            return (HvErrorKind.GuestNoResponse, "来宾操作系统未响应（集成服务不可用）。可改用「强制关闭」或「重置」。");
        if (M(m, @"invalid state|not in a valid state|current state|无效状态|状态无效|当前状态|状态下无法|cannot be performed while|in the '?(running|off|saved|paused)'? state|无法在.*状态"))
            return (HvErrorKind.InvalidState, "虚拟机当前状态不允许此操作。请刷新后重试。");
        if (M(m, @"already exists|已存在|duplicate"))
            return (HvErrorKind.AlreadyExists, "已存在同名虚拟机或文件。");
        if (M(m, @"must be (greater|larger)|greater than|smaller than the current|大于当前|小于当前|cannot shrink|无法缩小"))
            return (HvErrorKind.ResizeTooSmall, "新磁盘容量必须大于当前容量。");
        if (M(m, @"unable to find a virtual machine|was not found|not found|找不到|无法找到|does not exist|不存在|cannot find|未找到"))
            return (HvErrorKind.NotFound, "找不到指定的虚拟机或资源。请刷新列表后重试。");

        var first = m.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? m;
        if (first.Length > 160) first = first[..160] + "…";
        return (HvErrorKind.Unknown, first);
    }

    /// <summary>Removes PowerShell decoration (position info, CategoryInfo, CLIXML, cmdlet prefix).</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Replace("\r", "");

        if (text.Contains("#< CLIXML", StringComparison.Ordinal))
        {
            var sb = new System.Text.StringBuilder();
            foreach (Match mm in Regex.Matches(text, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline))
                sb.Append(System.Net.WebUtility.HtmlDecode(mm.Groups[1].Value).Replace("_x000D_", "").Replace("_x000A_", "\n"));
            text = sb.ToString();
        }

        var keep = new List<string>();
        foreach (var line0 in text.Split('\n'))
        {
            var line = line0.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("At line:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("所在位置", StringComparison.Ordinal)) continue;
            if (line.StartsWith("At ", StringComparison.Ordinal) && line.Contains(".ps1:")) continue;
            if (line.StartsWith("+", StringComparison.Ordinal)) continue;
            if (line.StartsWith("~", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(line, @"^(CategoryInfo|FullyQualifiedErrorId|Line \||\d+ \|)", RegexOptions.IgnoreCase)) continue;
            line = Regex.Replace(line, @"^[A-Z][A-Za-z]+-[A-Za-z]+\s*:\s*", ""); // "Start-VM : msg"
            line = Regex.Replace(line, @"\s+", " ");
            keep.Add(line);
        }
        return string.Join("\n", keep).Trim();
    }

    public static HyperVException ToException(string action, string? raw)
    {
        var (kind, msg) = Map(raw);
        var text = string.IsNullOrWhiteSpace(msg) ? $"{action}失败" : $"{action}失败：{msg}";
        return new HyperVException(kind, text, raw ?? "");
    }

    /// <summary>Short UI message for any exception.</summary>
    public static string Friendly(Exception ex) => ex switch
    {
        HyperVException h => h.Message,
        TimeoutException => "操作超时，请稍后重试。",
        OperationCanceledException => "操作已取消。",
        ArgumentException a => a.Message,
        _ => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message
    };
}
