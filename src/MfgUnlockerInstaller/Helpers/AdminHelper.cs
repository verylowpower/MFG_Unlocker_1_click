using System.Diagnostics;
using System.Security.Principal;

namespace MfgUnlockerInstaller.Helpers;

/// <summary>
/// Helpers for checking and requesting Windows administrator privileges when modifying protected game folders.
/// </summary>
public static class AdminHelper
{
    public static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool RestartAsAdministrator()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            ProcessStartInfo psi = new()
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
