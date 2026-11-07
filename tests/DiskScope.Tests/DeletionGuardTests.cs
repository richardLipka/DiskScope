using DiskScope.Core.Deletion;
using V = DiskScope.Core.Deletion.DeletionGuard.Verdict;

namespace DiskScope.Tests;

public class DeletionGuardTests
{
    static readonly DeletionGuard Guard = new(p => p
        .Replace("%SystemRoot%", @"C:\Windows").Replace("%ProgramFiles(x86)%", @"C:\Program Files (x86)")
        .Replace("%ProgramFiles%", @"C:\Program Files").Replace("%ProgramData%", @"C:\ProgramData").Replace("%SystemDrive%", "C:"));

    [Theory]
    [InlineData(@"C:\", V.DriveRoot)]
    [InlineData(@"D:\", V.DriveRoot)]
    [InlineData(@"C:\Windows", V.ProtectedFolder)]
    [InlineData(@"C:\Program Files", V.ProtectedFolder)]
    [InlineData(@"C:\Program Files (x86)", V.ProtectedFolder)]
    [InlineData(@"C:\ProgramData", V.ProtectedFolder)]
    [InlineData(@"C:\Users", V.ProtectedFolder)]
    [InlineData(@"C:\Users\bob", V.ProtectedFolder)]
    [InlineData(@"C:\Users\bob\AppData", V.ProtectedFolder)]
    [InlineData(@"C:\Users\bob\AppData\Local", V.ProtectedFolder)]
    [InlineData(@"C:\Users\bob\Documents", V.ProtectedFolder)]
    [InlineData(@"C:\Users\bob\OneDrive - Firma", V.ProtectedFolder)]
    [InlineData(@"C:\Windows\System32", V.InsideWindows)]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", V.InsideWindows)]
    [InlineData(@"C:\Windows\Temp", V.InsideWindows)] // its contents may go, the folder stays
    public void RefusesProtectedPlaces(string path, V expected) => Assert.Equal(expected, Guard.Check(path));

    [Theory]
    [InlineData(@"\\?\C:\Windows\System32", V.InsideWindows)]
    [InlineData(@"\\?\C:\Users\bob", V.ProtectedFolder)]
    [InlineData(@"\\server\share", V.DriveRoot)]
    [InlineData(@"\\?\UNC\server\share", V.DriveRoot)]
    [InlineData(@"C:\Users\bob\..\..\Windows\System32", V.InsideWindows)]
    public void PrefixesAndDotsDoNotGetAround(string path, V expected) => Assert.Equal(expected, Guard.Check(path));

    [Fact]
    public void JunctionAboveTheItemIsResolved()
    {
        using var t = new TestTree();
        string target = t.Dir("realwindows");
        t.File(@"realwindows\kernel.dll", 10);
        string link = Path.Combine(t.Root, "innocent");
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();

        // Pretend the junction's target is the Windows folder.
        var guard = new DeletionGuard(s => s.Replace("%SystemRoot%", target).Replace("%SystemDrive%", "Q:")
            .Replace("%ProgramFiles(x86)%", @"Q:\pf86").Replace("%ProgramFiles%", @"Q:\pf").Replace("%ProgramData%", @"Q:\pd"));
        string sneaky = Path.Combine(link, "kernel.dll");
        Assert.Equal(V.Allowed, guard.Check(sneaky));                // by name it looks harmless
        Assert.Equal(V.InsideWindows, guard.CheckResolved(sneaky));  // but it leads inside "Windows"
        Assert.Equal(V.Allowed, guard.CheckResolved(link));          // the link itself may still be removed
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\setup.log")]
    [InlineData(@"C:\Windows\Temp\some folder")]
    [InlineData(@"C:\Windows\MEMORY.DMP")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download\abc")]
    [InlineData(@"C:\Users\bob\AppData\Local\Temp")]
    [InlineData(@"C:\Users\bob\AppData\Local\npm-cache")]
    [InlineData(@"C:\Users\bob\Documents\old.zip")]
    [InlineData(@"C:\Program Files\Leftover Suite")]
    [InlineData(@"C:\ProgramData\OldVendor")]
    [InlineData(@"D:\Data\x")]
    public void AllowsEverythingElse(string path) => Assert.Equal(V.Allowed, Guard.Check(path));
}
