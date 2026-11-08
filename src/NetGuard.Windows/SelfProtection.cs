using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using NetGuard.Core;

namespace NetGuard.Windows;

/// <summary>
/// Single-account mode: your everyday account is an administrator, so NetGuard protects its own
/// parts from administrators. Applied by the service (running as SYSTEM, the owner of everything below):
///   • the Windows service: administrators may query and start it, but not stop, reconfigure or delete it
///   • its registry key: read-only for administrators
///   • the program folder and the data folder: read-only for administrators
/// Undoing this needs taking ownership of each part first – deliberately slow and obscure.
/// Relax() reverses it; the service only does that for an uninstall that was allowed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SelfProtection
{
    // SDDL rights: CC query config, LC query status, SW enum deps, RP start, WP stop, DT pause,
    // LO interrogate, CR user control, SD delete, RC read control, WD write DAC, WO write owner, DC change config.
    private const string HardenedSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPLOCRRC;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CCLCSWLOCRRC;;;AU)";
    private const string DefaultSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceControl.ServiceName;

    private static readonly SecurityIdentifier System_ = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>Apply (or re-apply) protection. Returns what had to be changed (empty if already in place).</summary>
    public static List<string> Apply(NetGuardPaths paths, string programDir)
    {
        var changed = new List<string>();
        Try(changed, "service permissions", () => SetServiceSddl(HardenedSddl));
        Try(changed, "service registry key", () => SetRegistryKey(hardened: true));
        Try(changed, "program folder", () => SetFolder(programDir, hardened: true, isDataDir: false, paths));
        Try(changed, "data folder", () => SetFolder(paths.DataDir, hardened: true, isDataDir: true, paths));
        // Keep restarting forever if killed (the last failure action repeats).
        ProcessRunner.Run("sc.exe", $"failure {ServiceControl.ServiceName} reset= 86400 actions= restart/2000/restart/2000/restart/2000");
        return changed;
    }

    /// <summary>Give administrators full control back (only for an allowed uninstall).</summary>
    public static List<string> Relax(NetGuardPaths paths, string programDir)
    {
        var changed = new List<string>();
        Try(changed, "service permissions", () => SetServiceSddl(DefaultSddl));
        Try(changed, "service registry key", () => SetRegistryKey(hardened: false));
        Try(changed, "program folder", () => SetFolder(programDir, hardened: false, isDataDir: false, paths));
        Try(changed, "data folder", () => SetFolder(paths.DataDir, hardened: false, isDataDir: true, paths));
        return changed;
    }

    public static bool IsServiceHardened()
    {
        var r = ProcessRunner.Run("sc.exe", $"sdshow {ServiceControl.ServiceName}");
        return r.ExitCode == 0 && Normalize(r.Output) == Normalize(HardenedSddl);
    }

    private static void Try(List<string> changed, string what, Func<bool> action)
    {
        try { if (action()) changed.Add(what); }
        catch (Exception e) { changed.Add($"{what}: FAILED ({e.Message})"); }
    }

    /// <summary>Only the DACL ("D:…") part matters; sdshow may also print an audit section ("S:…").</summary>
    private static string Normalize(string sddl)
    {
        var compact = new string(sddl.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var d = compact.IndexOf("D:", StringComparison.Ordinal);
        if (d < 0) return compact;
        var sPart = compact.IndexOf("S:", d, StringComparison.Ordinal);
        return sPart < 0 ? compact[d..] : compact[d..sPart];
    }

    private static bool SetServiceSddl(string sddl)
    {
        var current = ProcessRunner.Run("sc.exe", $"sdshow {ServiceControl.ServiceName}");
        if (current.ExitCode == 0 && Normalize(current.Output) == Normalize(sddl)) return false;
        var r = ProcessRunner.Run("sc.exe", $"sdset {ServiceControl.ServiceName} {sddl}");
        if (r.ExitCode != 0) throw new InvalidOperationException(r.Output);
        return true;
    }

    private static bool SetRegistryKey(bool hardened)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadKey | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership);
        if (key == null) return false;

        var sec = new RegistrySecurity();
        sec.SetOwner(System_);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new RegistryAccessRule(System_, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(Admins, hardened ? RegistryRights.ReadKey : RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(Users, RegistryRights.ReadKey, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));

        var before = key.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        var after = sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        if (before == after) return false;
        key.SetAccessControl(sec);
        return true;
    }

    private static bool SetFolder(string dir, bool hardened, bool isDataDir, NetGuardPaths paths)
    {
        if (!Directory.Exists(dir)) return false;
        var sec = new DirectorySecurity();
        sec.SetOwner(System_);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(System_, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Admins, hardened ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
            Inherit, PropagationFlags.None, AccessControlType.Allow));
        if (!isDataDir)
            sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));

        var di = new DirectoryInfo(dir);
        var before = di.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        var after = sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        var changed = before != after;
        if (changed) di.SetAccessControl(sec);

        // Files: owner SYSTEM, no explicit entries (inherit from the folder).
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            var fs = fi.GetAccessControl();
            var explicitRules = fs.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            var isLog = isDataDir && string.Equals(fi.FullName, Path.GetFullPath(paths.LogFile), StringComparison.OrdinalIgnoreCase);
            var wantExplicit = isLog ? 1 : 0;
            var owner = fs.GetOwner(typeof(SecurityIdentifier));
            if (explicitRules.Count == wantExplicit && !fs.AreAccessRulesProtected && System_.Equals(owner)) continue;

            fs.SetOwner(System_);
            fs.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            foreach (var rule in explicitRules) fs.RemoveAccessRuleSpecific(rule);
            if (isLog) fs.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read | FileSystemRights.Synchronize, AccessControlType.Allow));
            fi.SetAccessControl(fs);
            changed = true;
        }
        return changed;
    }
}
