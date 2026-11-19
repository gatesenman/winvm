using WinVM.Services;
using Xunit;

namespace WinVM.Tests;

public class ErrorMapperTests
{
    [Theory]
    [InlineData("You do not have the required permission to complete this task. Contact the administrator of the authorization policy for the computer 'PC'.", HvErrorKind.AccessDenied)]
    [InlineData("你没有完成此任务所需的权限。请与计算机“SD-20250722DZPD”授权策略的管理员联系。", HvErrorKind.AccessDenied)]
    [InlineData("Access is denied.", HvErrorKind.AccessDenied)]
    [InlineData("Hyper-V was unable to find a virtual machine with name \"foo\".", HvErrorKind.NotFound)]
    [InlineData("Hyper-V 找不到名为“foo”的虚拟机。", HvErrorKind.NotFound)]
    [InlineData("Hyper-V was unable to find a virtual switch with name \"Nope\".", HvErrorKind.SwitchNotFound)]
    [InlineData("找不到名为“外网”的虚拟交换机。", HvErrorKind.SwitchNotFound)]
    [InlineData("The operation cannot be performed while the object is in its current state.", HvErrorKind.InvalidState)]
    [InlineData("无法在对象处于当前状态时执行此操作。", HvErrorKind.InvalidState)]
    [InlineData("'vm' failed to start. Not enough memory in the system to start the virtual machine vm.", HvErrorKind.InsufficientMemory)]
    [InlineData("The virtual machine could not be shut down because the shutdown integration service is not available.", HvErrorKind.GuestNoResponse)]
    [InlineData("The process cannot access the file because it is being used by another process.", HvErrorKind.FileInUse)]
    [InlineData("Failed to resize the virtual disk: the disk is part of a checkpoint (differencing) chain.", HvErrorKind.CheckpointBlocksResize)]
    [InlineData("The size must be greater than the current size of the virtual disk.", HvErrorKind.ResizeTooSmall)]
    [InlineData("The virtual machine could not be started because the hypervisor is not running.", HvErrorKind.HypervisorNotRunning)]
    [InlineData("Something completely different happened", HvErrorKind.Unknown)]
    public void Maps_known_messages(string raw, HvErrorKind kind)
    {
        var (k, msg) = ErrorMapper.Map(raw);
        Assert.Equal(kind, k);
        Assert.False(string.IsNullOrWhiteSpace(msg));
        Assert.True(msg.Length <= 170);
    }

    [Fact]
    public void Own_marker_passes_through_verbatim()
    {
        var (k, msg) = ErrorMapper.Map("WINVM:ISO 文件不存在。");
        Assert.Equal(HvErrorKind.Validation, k);
        Assert.Equal("ISO 文件不存在。", msg);
    }

    [Fact]
    public void Own_marker_wins_over_keyword_rules()
    {
        // "找不到" would otherwise map to NotFound with a generic message
        var (k, msg) = ErrorMapper.Map("WINVM:找不到虚拟交换机「X」。");
        Assert.Equal(HvErrorKind.Validation, k);
        Assert.Equal("找不到虚拟交换机「X」。", msg);
    }

    [Fact]
    public void Cleans_powershell_decoration_en()
    {
        var raw = "Start-VM : 'x' failed to start.\r\nAt line:3 char:1\r\n+ Start-VM -VM $vm\r\n+ ~~~~~~~~~~~~~~~~\r\n    + CategoryInfo          : NotSpecified: (:) [Start-VM], VirtualizationException\r\n    + FullyQualifiedErrorId : Unspecified,Microsoft.HyperV.PowerShell.Commands.StartVM";
        Assert.Equal("'x' failed to start.", ErrorMapper.Clean(raw));
    }

    [Fact]
    public void Cleans_powershell_decoration_zh()
    {
        var raw = "Get-VM : 你没有完成此任务所需的权限。\r\n所在位置 行:1 字符: 1\r\n+ Get-VM\r\n    + CategoryInfo          : InvalidOperation";
        Assert.Equal("你没有完成此任务所需的权限。", ErrorMapper.Clean(raw));
    }

    [Fact]
    public void Decodes_clixml_error_stream()
    {
        var raw = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T></TN></Obj><S S=\"Error\">Access is denied._x000D__x000A_</S></Objs>";
        var (k, _) = ErrorMapper.Map(raw);
        Assert.Equal(HvErrorKind.AccessDenied, k);
    }

    [Fact]
    public void ToException_prefixes_action()
    {
        var ex = ErrorMapper.ToException("启动", "Access is denied.");
        Assert.StartsWith("启动失败：", ex.Message);
        Assert.Equal(HvErrorKind.AccessDenied, ex.Kind);
        Assert.Equal("Access is denied.", ex.Detail);
    }

    [Fact]
    public void Friendly_handles_common_exception_types()
    {
        Assert.Equal("操作超时，请稍后重试。", ErrorMapper.Friendly(new TimeoutException("x")));
        Assert.Equal("bad", ErrorMapper.Friendly(new ArgumentException("bad")));
        Assert.Equal("m", ErrorMapper.Friendly(new HyperVException(HvErrorKind.Unknown, "m")));
    }

    [Fact]
    public void Long_unknown_messages_are_truncated()
    {
        var (_, msg) = ErrorMapper.Map(new string('x', 500));
        Assert.True(msg.Length <= 161);
    }

    [Fact]
    public void Host_firmware_signature_failure_is_classified()
    {
        var raw = "“WinVM-E2E”无法启动。\n\nMicrosoft Virtual BIOS (实例 ID AC6B8DC1): 错误“无效签名。”。\n\n无法启动 'x'，因为无法加载固件资源 DLL： Invalid Signature. (0x80090006)。";
        var (kind, msg) = ErrorMapper.Map(raw);
        Assert.Equal(HvErrorKind.HostCannotStart, kind);
        Assert.Contains("0x80090006", msg);
    }

}
