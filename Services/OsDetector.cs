using System.Text.RegularExpressions;
using WinVM.Models;

namespace WinVM.Services;

/// <summary>Guesses the guest OS for the library icon. WinVM writes "[WinVM os=linux|windows]" into
/// VM notes at creation; otherwise falls back to keywords in notes, name and ISO file name.</summary>
public static class OsDetector
{
    public const string NotesTagPrefix = "[WinVM os=";

    public static string NotesTag(OsKind os) => $"{NotesTagPrefix}{(os == OsKind.Windows ? "windows" : "linux")}]";

    private static readonly Regex Tag = new(@"\[WinVM os=(windows|linux)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Win = new(@"windows|(?<![a-z])win(?![a-z])|(?<![a-z])win\d|(?<![a-z])w(7|8|10|11)(?![a-z0-9])|winsrv|ltsc|server\s*20\d\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Lin = new(@"linux|ubuntu|debian|fedora|centos|rocky|manjaro|opensuse|suse|redhat|alpine|gentoo|nixos|pop[-_ ]?os|elementary|deepin|kylin|openeuler|zorin|endeavour|garuda|parrot|raspbian|freebsd|(?<![a-z])(arch|mint|kali|rhel|alma|uos|void|tails|mx)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Notes without WinVM's own tag (what the user sees and edits).</summary>
    public static string UserNotes(string? notes) =>
        string.IsNullOrEmpty(notes) ? "" : Tag.Replace(notes, "").Trim('\r', '\n', ' ');

    /// <summary>Combines WinVM's tag (kept from <paramref name="currentNotes"/>) with new user text.</summary>
    public static string JoinNotes(string? currentNotes, string? userText)
    {
        var m = string.IsNullOrEmpty(currentNotes) ? null : Tag.Match(currentNotes);
        var tag = m is { Success: true } ? m.Value : null;
        var u = (userText ?? "").Trim();
        return tag == null ? u : u.Length == 0 ? tag : tag + "\n" + u;
    }

    public static OsKind Detect(string? notes, string? name, string? isoPath)
    {
        if (!string.IsNullOrEmpty(notes))
        {
            var m = Tag.Match(notes);
            if (m.Success)
                return m.Groups[1].Value.Equals("windows", StringComparison.OrdinalIgnoreCase) ? OsKind.Windows : OsKind.Linux;
        }
        string iso = "";
        try { iso = string.IsNullOrEmpty(isoPath) ? "" : Path.GetFileName(isoPath); } catch { }
        foreach (var s in new[] { notes ?? "", name ?? "", iso })
        {
            if (s.Length == 0) continue;
            if (Lin.IsMatch(s)) return OsKind.Linux;
            if (Win.IsMatch(s)) return OsKind.Windows;
        }
        return OsKind.Unknown;
    }
}
