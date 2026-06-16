using System.Security.Principal;

namespace Rynez;

/// <summary>Checks whether the process has administrator privileges.</summary>
internal static class Admin
{
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var p = new WindowsPrincipal(id);
            return p.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
