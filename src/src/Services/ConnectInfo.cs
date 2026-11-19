using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinVM.Models;

namespace WinVM.Services;

/// <summary>Agent-friendly connection snapshot for a Hyper-V guest.</summary>
public sealed class ConnectInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("ips")] public string[] Ips { get; set; } = Array.Empty<string>();
    [JsonPropertyName("switch")] public string? Switch { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("rdp")] public EndpointInfo? Rdp { get; set; }
    [JsonPropertyName("ssh")] public EndpointInfo? Ssh { get; set; }
    [JsonPropertyName("vmconnect")] public string? VmConnect { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }

    public sealed class EndpointInfo
    {
        [JsonPropertyName("host")] public string Host { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ConnectInfo FromVm(VmInfo v, string? username = null)
    {
        var ips = string.IsNullOrWhiteSpace(v.GuestIp)
            ? Array.Empty<string>()
            : v.GuestIp.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ip = v.PrimaryGuestIp;
        var running = v.State == VmState.Running;
        var hasIp = !string.IsNullOrEmpty(ip);
        var os = v.Os switch
        {
            OsKind.Windows => "Windows",
            OsKind.Linux => "Linux",
            _ => "Unknown"
        };
        var info = new ConnectInfo
        {
            Name = v.Name,
            Id = v.Id,
            State = v.State.ToString(),
            Os = os,
            Ip = string.IsNullOrEmpty(ip) ? null : ip,
            Ips = ips,
            Switch = string.IsNullOrWhiteSpace(v.SwitchName) ? null : v.SwitchName,
            Username = string.IsNullOrWhiteSpace(username) ? null : username,
            VmConnect = Guid.TryParse(v.Id, out _)
                ? $"vmconnect.exe localhost -G {v.Id}"
                : $"vmconnect.exe localhost \"{v.Name}\"",
            Ready = running && hasIp,
            Notes = running
                ? (hasIp
                    ? "Guest IP reported by Hyper-V integration services."
                    : "Running but no guest IP yet — use vmconnect / Enhanced Session, or wait for DHCP.")
                : "VM not running."
        };
        if (hasIp)
        {
            info.Rdp = new EndpointInfo { Host = ip, Port = 3389 };
            info.Ssh = new EndpointInfo { Host = ip, Port = 22 };
        }
        return info;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>Human-readable block for clipboard / agents.</summary>
    public string ToClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"VM: {Name}");
        sb.AppendLine($"State: {State}");
        sb.AppendLine($"OS: {Os}");
        if (!string.IsNullOrEmpty(Ip)) sb.AppendLine($"IP: {Ip}");
        if (!string.IsNullOrEmpty(Username)) sb.AppendLine($"User: {Username}");
        if (Rdp != null) sb.AppendLine($"RDP: {Rdp.Host}:{Rdp.Port}");
        if (Ssh != null)
        {
            var u = string.IsNullOrEmpty(Username) ? "" : Username + "@";
            sb.AppendLine($"SSH: ssh {u}{Ssh.Host}" + (Ssh.Port == 22 ? "" : $" -p {Ssh.Port}"));
        }
        if (!string.IsNullOrEmpty(VmConnect)) sb.AppendLine($"vmconnect: {VmConnect}");
        sb.AppendLine($"Ready: {(Ready ? "yes" : "no")}");
        return sb.ToString().TrimEnd();
    }
}
