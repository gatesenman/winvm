using System.Text;
using WinVM.Models;

namespace WinVM.Services;

/// <summary>Best-effort first-boot / agent-ready scripts written under %USERPROFILE%\WinVM\Setup\.
/// Full unattended (unattend.xml / NoCloud ISO attach) is fragile on Insider hosts — we ship
/// clear scripts + README the user (or agent) runs after first connect, while one-click create+start still completes.</summary>
public static class FirstBootProfile
{
    public sealed class Options
    {
        public bool EnableRdp { get; set; }
        public bool EnableOpenSsh { get; set; }
        public bool AgentReady { get; set; }
        public string Hostname { get; set; } = "";
        public string Timezone { get; set; } = "Asia/Shanghai";
        public string Username { get; set; } = "";
        /// <summary>Never logged; only written into local script files under the user's profile.</summary>
        public string? Password { get; set; }
        public OsKind Os { get; set; }
    }

    public static string SetupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WinVM", "Setup");

    public static string FolderFor(string vmName) =>
        Path.Combine(SetupRoot, Sanitize(vmName));

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "VM" : sb.ToString();
    }

    /// <summary>Writes scripts; returns the folder path. Password is never written to logs.</summary>
    public static string Write(string vmName, Options opt)
    {
        var dir = FolderFor(vmName);
        Directory.CreateDirectory(dir);
        if (opt.Os == OsKind.Windows)
            WriteWindows(dir, opt);
        else
            WriteLinux(dir, opt);
        WriteReadme(dir, opt);
        return dir;
    }

    private static void WriteWindows(string dir, Options opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# WinVM first-boot helper — run elevated in the Windows guest after install.");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        if (!string.IsNullOrWhiteSpace(opt.Hostname))
        {
            sb.AppendLine($"if ($env:COMPUTERNAME -ne '{EscapePs(opt.Hostname)}') {{");
            sb.AppendLine($"  Rename-Computer -NewName '{EscapePs(opt.Hostname)}' -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("}");
        }
        sb.AppendLine("try { Set-TimeZone -Id 'China Standard Time' -ErrorAction Stop } catch {");
        sb.AppendLine("  try { tzutil /s 'China Standard Time' } catch {}");
        sb.AppendLine("}");
        if (opt.EnableRdp || opt.AgentReady)
        {
            sb.AppendLine("Set-ItemProperty -Path 'HKLM:\\System\\CurrentControlSet\\Control\\Terminal Server' -Name fDenyTSConnections -Value 0");
            sb.AppendLine("Enable-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue");
        }
        if (opt.EnableOpenSsh || opt.AgentReady)
        {
            sb.AppendLine("try {");
            sb.AppendLine("  Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 -ErrorAction Stop | Out-Null");
            sb.AppendLine("  Start-Service sshd -ErrorAction SilentlyContinue");
            sb.AppendLine("  Set-Service -Name sshd -StartupType Automatic -ErrorAction SilentlyContinue");
            sb.AppendLine("  New-NetFirewallRule -Name sshd -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -ErrorAction SilentlyContinue | Out-Null");
            sb.AppendLine("} catch { Write-Warning \"OpenSSH Server: $($_.Exception.Message)\" }");
        }
        if (!string.IsNullOrWhiteSpace(opt.Username) && !string.IsNullOrEmpty(opt.Password))
        {
            sb.AppendLine($"$u = '{EscapePs(opt.Username)}'");
            sb.AppendLine($"$p = ConvertTo-SecureString '{EscapePs(opt.Password)}' -AsPlainText -Force");
            sb.AppendLine("if (-not (Get-LocalUser -Name $u -ErrorAction SilentlyContinue)) {");
            sb.AppendLine("  New-LocalUser -Name $u -Password $p -PasswordNeverExpires -AccountNeverExpires | Out-Null");
            sb.AppendLine("  Add-LocalGroupMember -Group 'Administrators' -Member $u -ErrorAction SilentlyContinue");
            sb.AppendLine("} else { Set-LocalUser -Name $u -Password $p }");
        }
        sb.AppendLine("Write-Host 'WinVM first-boot script finished.'");
        File.WriteAllText(Path.Combine(dir, "Enable-WinGuest.ps1"), sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        File.WriteAllText(Path.Combine(dir, "Windows-RDP-notes.txt"),
            "Windows 来宾 · RDP 就绪说明\n" +
            "自动：WinVM 已创建并启动虚拟机；本目录含 Enable-WinGuest.ps1。\n" +
            "引导：\n" +
            "  1. 用「连接控制台」(vmconnect / Enhanced Session) 完成 Windows 安装。\n" +
            "  2. 安装后在来宾管理员 PowerShell 运行本脚本。\n" +
            "  3. 来宾 IP 出现在列表后可用「连接 → 远程桌面」。\n" +
            "密码仅写入本机 Setup 脚本，不进入 WinVM 日志。\n",
            Encoding.UTF8);
    }

    private static void WriteLinux(string dir, Options opt)
    {
        var user = string.IsNullOrWhiteSpace(opt.Username) ? "winvm" : opt.Username.Trim();
        var host = string.IsNullOrWhiteSpace(opt.Hostname) ? "winvm-guest" : opt.Hostname.Trim();
        var tz = string.IsNullOrWhiteSpace(opt.Timezone) ? "Asia/Shanghai" : opt.Timezone.Trim();

        var cloud = new StringBuilder();
        cloud.AppendLine("#cloud-config");
        cloud.AppendLine("hostname: " + host);
        cloud.AppendLine("timezone: " + tz);
        cloud.AppendLine("package_update: true");
        cloud.AppendLine("packages:");
        if (opt.EnableOpenSsh || opt.AgentReady)
            cloud.AppendLine("  - openssh-server");
        if (opt.AgentReady)
        {
            cloud.AppendLine("  - curl");
            cloud.AppendLine("  - git");
            cloud.AppendLine("  - sudo");
            cloud.AppendLine("  - ca-certificates");
        }
        cloud.AppendLine("users:");
        cloud.AppendLine("  - name: " + user);
        cloud.AppendLine("    groups: [sudo, adm]");
        cloud.AppendLine("    shell: /bin/bash");
        cloud.AppendLine("    sudo: ALL=(ALL) NOPASSWD:ALL");
        if (!string.IsNullOrEmpty(opt.Password))
            cloud.AppendLine("    plain_text_passwd: '" + EscapeYaml(opt.Password) + "'");
        cloud.AppendLine("    lock_passwd: false");
        cloud.AppendLine("ssh_pwauth: true");
        cloud.AppendLine("runcmd:");
        cloud.AppendLine("  - [ timedatectl, set-timezone, " + tz + " ]");
        if (opt.EnableOpenSsh || opt.AgentReady)
        {
            cloud.AppendLine("  - [ systemctl, enable, --now, ssh ]");
            cloud.AppendLine("  - [ systemctl, enable, --now, sshd ]");
        }
        Directory.CreateDirectory(Path.Combine(dir, "cloud-init"));
        File.WriteAllText(Path.Combine(dir, "cloud-init", "user-data"), cloud.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "cloud-init", "meta-data"),
            "instance-id: winvm-" + host + "\nlocal-hostname: " + host + "\n", Encoding.UTF8);

        var sh = new StringBuilder();
        sh.AppendLine("#!/bin/bash");
        sh.AppendLine("set -euo pipefail");
        sh.AppendLine("hostnamectl set-hostname '" + EscapeSh(host) + "' || true");
        sh.AppendLine("timedatectl set-timezone '" + EscapeSh(tz) + "' || true");
        if (opt.EnableOpenSsh || opt.AgentReady)
            sh.AppendLine("export DEBIAN_FRONTEND=noninteractive; (command -v apt-get >/dev/null && apt-get update && apt-get install -y openssh-server curl git sudo ca-certificates) || (command -v dnf >/dev/null && dnf install -y openssh-server curl git sudo ca-certificates) || true");
        sh.AppendLine("systemctl enable --now ssh 2>/dev/null || systemctl enable --now sshd 2>/dev/null || true");
        if (!string.IsNullOrWhiteSpace(opt.Username))
        {
            sh.AppendLine("id '" + EscapeSh(user) + "' >/dev/null 2>&1 || useradd -m -s /bin/bash -G sudo '" + EscapeSh(user) + "' || useradd -m -s /bin/bash -G wheel '" + EscapeSh(user) + "' || true");
            if (!string.IsNullOrEmpty(opt.Password))
                sh.AppendLine("echo '" + EscapeSh(user) + ":" + EscapeSh(opt.Password) + "' | chpasswd");
        }
        sh.AppendLine("echo 'WinVM firstboot finished.'");
        File.WriteAllText(Path.Combine(dir, "firstboot.sh"), sh.ToString(), new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(dir, "Linux-SSH-notes.txt"),
            "Linux 来宾 · OpenSSH / cloud-init\n" +
            "自动：已生成 cloud-init/user-data 与 firstboot.sh。\n" +
            "引导：\n" +
            "  · 若发行版支持 NoCloud：可将 cloud-init 目录打成 seed.iso 挂到第二光驱（需 genisoimage/oscdimg）。\n" +
            "  · 否则完成安装后用 vmconnect 登录，复制 firstboot.sh 到客机并 sudo bash firstboot.sh。\n" +
            "  · 客机 IP 出现后可用「连接 → SSH」。\n",
            Encoding.UTF8);
    }

    private static void WriteReadme(string dir, Options opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinVM 基础环境脚本（本机生成，不上传）");
        sb.AppendLine("OS: " + opt.Os);
        sb.AppendLine("Hostname: " + opt.Hostname);
        sb.AppendLine("User: " + opt.Username);
        sb.AppendLine("Timezone: " + opt.Timezone);
        sb.AppendLine("RDP: " + opt.EnableRdp + "  OpenSSH: " + opt.EnableOpenSsh + "  AgentReady: " + opt.AgentReady);
        sb.AppendLine();
        sb.AppendLine("密码不会写入 WinVM 日志；若你在向导中填写了密码，仅存在于此目录的脚本内。");
        File.WriteAllText(Path.Combine(dir, "README.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static string EscapePs(string s) => s.Replace("'", "''");
    private static string EscapeSh(string s) => s.Replace("'", "'\\''");
    private static string EscapeYaml(string s) => s.Replace("\\", "\\\\").Replace("'", "''");
}
