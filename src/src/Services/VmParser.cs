using System.Globalization;
using System.Text.Json;
using WinVM.Models;

namespace WinVM.Services;

/// <summary>Parses the JSON produced by the batched Get-VM pipeline. Tolerates a single object
/// (PowerShell unwraps one-element arrays), nulls, numbers-as-strings and missing fields.</summary>
public static class VmParser
{
    public static VmState ParseState(string? s) => s switch
    {
        "Off" or "OffCritical" => VmState.Off,
        "Running" or "RunningCritical" => VmState.Running,
        "Paused" or "PausedCritical" => VmState.Paused,
        "Saved" or "SavedCritical" => VmState.Saved,
        "Starting" or "StartingCritical" => VmState.Starting,
        "Stopping" or "StoppingCritical" => VmState.Stopping,
        "Saving" or "SavingCritical" => VmState.Saving,
        "Pausing" or "PausingCritical" => VmState.Pausing,
        "Resuming" or "ResumingCritical" => VmState.Resuming,
        "Reset" or "Resetting" or "ResettingCritical" => VmState.Resetting,
        null or "" => VmState.Unknown,
        _ => VmState.Other
    };

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object => new[] { root },
            _ => Array.Empty<JsonElement>()
        };

    private static string Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => ""
        };
    }

    private static long Long(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var l)) return l;
            if (v.TryGetDouble(out var d)) return (long)d;
        }
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
            return (long)ds;
        return 0;
    }

    private static bool Bool(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind == JsonValueKind.True ||
               (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b) ||
               (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i != 0);
    }

    private static DateTime? Date(JsonElement el, string prop)
    {
        var s = Str(el, prop);
        if (s.Length == 0) return null;
        // ConvertTo-Json in PS 5.1 may emit "/Date(1700000000000)/"
        if (s.StartsWith("/Date(", StringComparison.Ordinal))
        {
            var num = new string(s.Skip(6).TakeWhile(c => char.IsDigit(c) || c == '-').ToArray());
            if (long.TryParse(num, out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        }
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return dto.LocalDateTime;
        return null;
    }

    public static IReadOnlyList<VmInfo> ParseVmList(string? json)
    {
        json = Trim(json);
        if (json.Length == 0 || json == "null") return Array.Empty<VmInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<VmInfo>();
            foreach (var el in Items(doc.RootElement))
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = Str(el, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var dvd = Str(el, "DvdPath");
                var notes = Str(el, "Notes");
                var info = new VmInfo
                {
                    Name = name,
                    Id = Str(el, "Id"),
                    State = ParseState(Str(el, "State")),
                    ProcessorCount = (int)Long(el, "ProcessorCount"),
                    MemoryStartupBytes = Long(el, "MemoryStartup"),
                    MemoryAssignedBytes = Long(el, "MemoryAssigned"),
                    DynamicMemory = Bool(el, "DynamicMemory"),
                    CpuUsage = (int)Long(el, "CpuUsage"),
                    UptimeSeconds = Long(el, "UptimeSeconds"),
                    Generation = el.TryGetProperty("Generation", out _) ? Math.Max(1, (int)Long(el, "Generation")) : 2,
                    Version = Str(el, "Version"),
                    Path = Str(el, "Path"),
                    DvdPath = string.IsNullOrWhiteSpace(dvd) ? null : dvd,
                    Notes = notes,
                    CreationTime = Date(el, "CreationTime"),
                    SwitchName = Str(el, "SwitchName"),
                    GuestIp = Str(el, "IpAddresses"),
                    DiskSizeBytes = Long(el, "DiskSizeBytes"),
                    DiskFileBytes = Long(el, "DiskFileBytes"),
                    VhdPath = Str(el, "VhdPath"),
                    CheckpointCount = (int)Long(el, "CheckpointCount"),
                    SecureBoot = Bool(el, "SecureBoot"),
                    Os = OsDetector.Detect(notes, name, dvd)
                };
                list.Add(info);
            }
            return list.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (JsonException ex)
        {
            throw new HyperVException(HvErrorKind.Unknown, "解析虚拟机列表失败。", ex.Message + "\n" + json);
        }
    }

    public static IReadOnlyList<CheckpointInfo> ParseCheckpoints(string? json)
    {
        json = Trim(json);
        if (json.Length == 0 || json == "null") return Array.Empty<CheckpointInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<CheckpointInfo>();
            foreach (var el in Items(doc.RootElement))
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = Str(el, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new CheckpointInfo
                {
                    Name = name,
                    Id = Str(el, "Id"),
                    SnapshotType = Str(el, "SnapshotType"),
                    ParentCheckpointName = Str(el, "ParentCheckpointName"),
                    ParentId = Str(el, "ParentId"),
                    IsCurrent = Bool(el, "IsCurrent"),
                    CreationTime = Date(el, "CreationTime")
                });
            }
            return list.OrderByDescending(c => c.CreationTime ?? DateTime.MinValue).ToList();
        }
        catch (JsonException ex)
        {
            throw new HyperVException(HvErrorKind.Unknown, "解析检查点列表失败。", ex.Message + "\n" + json);
        }
    }

    public static IReadOnlyList<string> ParseStringList(string? json)
    {
        json = Trim(json);
        if (json.Length == 0 || json == "null") return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.String)
                return string.IsNullOrWhiteSpace(r.GetString()) ? Array.Empty<string>() : new[] { r.GetString()! };
            if (r.ValueKind == JsonValueKind.Array)
                return r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        catch (JsonException) { }
        return Array.Empty<string>();
    }

    /// <summary>Takes the JSON payload even if a stray line was printed before it.</summary>
    private static string Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        if (s[0] is '[' or '{' or '"' || s == "null") return s;
        var i = s.IndexOfAny(new[] { '[', '{' });
        return i >= 0 ? s[i..] : s;
    }

    // ---------------------------------------------------------------- 1.5: advanced settings / switches

    private static string NonEmpty(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

    private static List<string> Strings(JsonElement el, string prop)
    {
        var r = new List<string>();
        if (!el.TryGetProperty(prop, out var v)) return r;
        if (v.ValueKind == JsonValueKind.String) { if (!string.IsNullOrEmpty(v.GetString())) r.Add(v.GetString()!); return r; }
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(x.GetString())) r.Add(x.GetString()!);
        return r;
    }

    public static VmAdvancedSettings ParseAdvanced(string? json)
    {
        json = Trim(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = Items(doc.RootElement).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
            if (el.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
            var boot = new List<BootEntry>();
            if (el.TryGetProperty("Boot", out var b))
                foreach (var e in Items(b))
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var kind = Str(e, "Kind") switch
                    {
                        "Disk" => BootDeviceKind.Disk, "Dvd" => BootDeviceKind.Dvd, "Network" => BootDeviceKind.Network,
                        "File" => BootDeviceKind.File, _ => BootDeviceKind.Other
                    };
                    boot.Add(new BootEntry((int)Long(e, "Index"), kind, Str(e, "Description")));
                }
            var sb = Str(el, "SecureBoot");
            return new VmAdvancedSettings
            {
                Generation = (int)Math.Max(1, Long(el, "Generation")),
                State = ParseState(Str(el, "State")),
                SecureBoot = sb.Equals("On", StringComparison.OrdinalIgnoreCase) || sb.Equals("True", StringComparison.OrdinalIgnoreCase),
                SecureBootTemplate = NonEmpty(Str(el, "SecureBootTemplate"), SecureBootTemplates.Windows),
                TpmEnabled = Bool(el, "Tpm"),
                DynamicMemory = Bool(el, "DynamicMemory"),
                StartupBytes = Long(el, "Startup"),
                MinimumBytes = Long(el, "Minimum"),
                MaximumBytes = Long(el, "Maximum"),
                MemoryBuffer = (int)Long(el, "Buffer"),
                BootOrder = boot,
                NestedVirtualization = Bool(el, "Nested"),
                AutoStartAction = NonEmpty(Str(el, "StartAction"), AutoStartActions.StartIfRunning),
                AutoStartDelaySeconds = (int)Long(el, "StartDelay"),
                AutoStopAction = NonEmpty(Str(el, "StopAction"), AutoStopActions.Save),
                Notes = OsDetector.UserNotes(Str(el, "Notes")),
                EnhancedSessionHvSocket = Str(el, "Transport").Equals("HvSocket", StringComparison.OrdinalIgnoreCase),
                HostEnhancedSessionEnabled = Bool(el, "HostEnhanced"),
            };
        }
        catch (JsonException ex)
        {
            throw new HyperVException(HvErrorKind.Unknown, "读取高级设置失败：无法解析 Hyper-V 返回的数据。", ex.Message);
        }
    }

    public static IReadOnlyList<VSwitchInfo> ParseSwitches(string? json)
    {
        json = Trim(json);
        if (json.Length == 0 || json == "null") return Array.Empty<VSwitchInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<VSwitchInfo>();
            foreach (var el in Items(doc.RootElement))
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = Str(el, "Name");
                if (name.Length == 0) continue;
                list.Add(new VSwitchInfo
                {
                    Id = Str(el, "Id"),
                    Name = name,
                    Kind = Str(el, "Type") switch { "External" or "2" => SwitchKind.External, "Internal" or "1" => SwitchKind.Internal, _ => SwitchKind.Private },
                    AdapterDescription = Str(el, "Adapter"),
                    AllowManagementOs = Bool(el, "AllowManagementOS"),
                    ConnectedVms = Strings(el, "Vms").Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList()
                });
            }
            return list.OrderBy(x => x.IsDefaultSwitch ? 0 : 1).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (JsonException ex)
        {
            throw new HyperVException(HvErrorKind.Unknown, "读取虚拟交换机失败：无法解析 Hyper-V 返回的数据。", ex.Message);
        }
    }

    public static IReadOnlyList<NetAdapterInfo> ParseAdapters(string? json)
    {
        json = Trim(json);
        if (json.Length == 0 || json == "null") return Array.Empty<NetAdapterInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Items(doc.RootElement).Where(e => e.ValueKind == JsonValueKind.Object && Str(e, "Name").Length > 0)
                .Select(e => new NetAdapterInfo { Name = Str(e, "Name"), Description = Str(e, "Description"), Status = Str(e, "Status"), BoundToSwitch = Bool(e, "Bound") })
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new HyperVException(HvErrorKind.Unknown, "读取网络适配器失败：无法解析返回的数据。", ex.Message);
        }
    }
}
