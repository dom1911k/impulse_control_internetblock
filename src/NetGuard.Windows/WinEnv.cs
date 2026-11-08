using System.Runtime.Versioning;
using System.Security.Principal;

namespace NetGuard.Windows;

[SupportedOSPlatform("windows")]
public static class WinEnv
{
    public static bool IsElevatedAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string CurrentUser()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.Name;
    }
}
