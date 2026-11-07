using DiskScope.Core;
using Microsoft.Win32;

namespace DiskScope.Tests;

public class ExplorerIntegrationTests
{
    [Fact]
    public void RegistersAndUnregistersUnderTheGivenKey()
    {
        // A throwaway key, so the real Explorer menu is never touched by tests.
        string root = @"Software\DiskScopeTests\" + Guid.NewGuid().ToString("N");
        var integration = new ExplorerIntegration(root);
        try
        {
            Assert.False(integration.IsRegistered);
            integration.Register(@"C:\Tools\Disk Scope\DiskScope.exe", "Analyzovat v DiskScope");
            Assert.True(integration.IsRegistered);
            Assert.Equal(@"C:\Tools\Disk Scope\DiskScope.exe", integration.RegisteredExecutable);

            using (var cmd = Registry.CurrentUser.OpenSubKey($@"{root}\Directory\Background\shell\DiskScope\command"))
                Assert.Equal("\"C:\\Tools\\Disk Scope\\DiskScope.exe\" --scan \"%V\"", cmd!.GetValue(null));
            using (var verb = Registry.CurrentUser.OpenSubKey($@"{root}\Drive\shell\DiskScope"))
                Assert.Equal("Analyzovat v DiskScope", verb!.GetValue(null));

            integration.Unregister();
            Assert.False(integration.IsRegistered);
            using var drive = Registry.CurrentUser.OpenSubKey($@"{root}\Drive\shell\DiskScope");
            Assert.Null(drive);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\DiskScopeTests", throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData("C:\"", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"D:\Data\Fotky", @"D:\Data\Fotky")]
    [InlineData("E:", @"E:\")]
    public void FixesDriveRootQuoting(string arg, string expected) => Assert.Equal(expected, ExplorerIntegration.FixPathArgument(arg));
}
