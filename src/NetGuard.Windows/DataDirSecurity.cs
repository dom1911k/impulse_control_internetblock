using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using NetGuard.Core;

namespace NetGuard.Windows;

/// <summary>
/// C:\ProgramData\NetGuard: SYSTEM and Administrators can write; standard users can only read the log.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirSecurity
{
    public static void Secure(NetGuardPaths paths)
    {
        Directory.CreateDirectory(paths.DataDir);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var dirSec = new DirectorySecurity();
        dirSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // drop inherited "Users can write" ACEs
        dirSec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        dirSec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(paths.DataDir).SetAccessControl(dirSec);

        // Existing files: drop explicit entries so they only inherit from the folder.
        foreach (var f in Directory.EnumerateFiles(paths.DataDir))
        {
            var fi = new FileInfo(f);
            var fs = fi.GetAccessControl();
            fs.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in fs.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
                fs.RemoveAccessRuleSpecific(rule);
            fi.SetAccessControl(fs);
        }

        // The log is readable (not writable) by standard users.
        if (!File.Exists(paths.LogFile)) File.WriteAllText(paths.LogFile, "");
        var logSec = new FileInfo(paths.LogFile).GetAccessControl();
        logSec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Read | FileSystemRights.Synchronize, AccessControlType.Allow));
        new FileInfo(paths.LogFile).SetAccessControl(logSec);
    }
}
