using System.Diagnostics;

namespace DiskScope.Core.Deletion;

/// <summary>
/// Shrinking a dynamic virtual disk (.vhdx, .vhd) such as a WSL distribution's or Docker Desktop's. These files grow
/// as data is written inside and never shrink by themselves, even after that data is deleted. Compacting needs the
/// virtual machine stopped and administrator rights; Windows' diskpart does it on every edition, Home included.
/// </summary>
public static class VirtualDisk
{
    public static bool IsVirtualDisk(string path) =>
        path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase);

    /// <summary>A disk of a WSL distribution (WSL can be shut down first; Docker Desktop must be quit by the user).</summary>
    public static bool LooksLikeWsl(string path) =>
        path.EndsWith(@"\ext4.vhdx", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\wsl\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("CanonicalGroupLimited", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Docker\wsl\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The diskpart script: attach read-only (so nothing inside can change), compact, detach.</summary>
    public static string Script(string path)
    {
        if (path.Contains('"') || path.Contains('\n') || path.Contains('\r')) throw new ArgumentException("Invalid path.", nameof(path));
        return $"select vdisk file=\"{path}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n";
    }

    /// <summary>
    /// Shuts down WSL when asked, then runs diskpart on the disk. When this process is not elevated, diskpart is
    /// started elevated (Windows asks for consent) and its output cannot be read; the exit code still tells success.
    /// </summary>
    /// <returns>The exit code, diskpart's output (when it could be read) and the file size before and after.</returns>
    public static async Task<(int ExitCode, string Output, long Before, long After)> CompactAsync(string path, bool shutdownWsl, bool elevated,
        CancellationToken cancel = default)
    {
        long before = new FileInfo(path).Length;
        if (shutdownWsl)
        {
            using var wsl = Process.Start(new ProcessStartInfo("wsl.exe", "--shutdown") { UseShellExecute = false, CreateNoWindow = true });
            if (wsl != null) await wsl.WaitForExitAsync(cancel);
        }
        string script = Path.Combine(Path.GetTempPath(), $"diskscope-compact-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(script, Script(path), cancel);
        try
        {
            var start = elevated
                ? new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }
                : new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("diskpart could not be started.");
            string output = elevated ? await process.StandardOutput.ReadToEndAsync(cancel) : "";
            await process.WaitForExitAsync(cancel);
            return (process.ExitCode, output, before, new FileInfo(path).Length);
        }
        finally
        {
            try { File.Delete(script); } catch (IOException) { }
        }
    }
}
