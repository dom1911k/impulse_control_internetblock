using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NetGuard.Windows;

/// <summary>
/// Limits Chrome to a list of websites using Chrome's own enterprise policies
/// (HKLM\SOFTWARE\Policies\Google\Chrome: URLBlocklist = "*", URLAllowlist = the sites).
/// Chrome picks up changes within seconds. Used only while a short window ("Bass check") is open;
/// afterwards the entries are removed again. Only these two lists are touched, and only if NetGuard created them.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ChromePolicy
{
    private const string Key = @"SOFTWARE\Policies\Google\Chrome";
    private const string Marker = "NetGuardManaged";

    /// <summary>Chrome's own pages and extensions (Bass Trainer's player) keep working.</summary>
    private static readonly string[] AlwaysAllowed = { "chrome://*", "chrome-extension://*", "chrome-untrusted://*", "devtools://*" };

    /// <summary>Make sure the policy says exactly this. Returns true if something had to be (re)written.</summary>
    public static bool Ensure(IEnumerable<string> websites)
    {
        var allow = AlwaysAllowed.Concat(websites.Select(w => w.Trim()).Where(w => w.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var chrome = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        if (Matches(chrome, "URLBlocklist", new[] { "*" }) && Matches(chrome, "URLAllowlist", allow) && chrome.GetValue(Marker) is int)
            return false;
        Write(chrome, "URLBlocklist", new[] { "*" });
        Write(chrome, "URLAllowlist", allow);
        chrome.SetValue(Marker, 1, RegistryValueKind.DWord);
        return true;
    }

    /// <summary>Remove NetGuard's restriction (only if NetGuard put it there). Returns true if something was removed.</summary>
    public static bool Remove()
    {
        using var chrome = Registry.LocalMachine.OpenSubKey(Key, writable: true);
        if (chrome?.GetValue(Marker) is not int) return false;
        chrome.DeleteSubKeyTree("URLBlocklist", throwOnMissingSubKey: false);
        chrome.DeleteSubKeyTree("URLAllowlist", throwOnMissingSubKey: false);
        chrome.DeleteValue(Marker, throwOnMissingValue: false);
        return true;
    }

    private static bool Matches(RegistryKey chrome, string list, IReadOnlyCollection<string> expected)
    {
        using var k = chrome.OpenSubKey(list);
        if (k == null) return false;
        var values = k.GetValueNames().Select(n => k.GetValue(n) as string).Where(v => v != null).ToList();
        return values.Count == expected.Count && expected.All(e => values.Contains(e, StringComparer.OrdinalIgnoreCase));
    }

    private static void Write(RegistryKey chrome, string list, IReadOnlyList<string> items)
    {
        chrome.DeleteSubKeyTree(list, throwOnMissingSubKey: false);
        using var k = chrome.CreateSubKey(list, writable: true);
        for (var i = 0; i < items.Count; i++) k.SetValue((i + 1).ToString(), items[i], RegistryValueKind.String);
    }
}
