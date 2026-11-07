using System.Security.Principal;

namespace DiskScope.Core.Native;

public static class Elevation
{
    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
