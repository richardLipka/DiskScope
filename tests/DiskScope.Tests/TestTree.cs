namespace DiskScope.Tests;

/// <summary>A temporary directory tree with known contents, deleted on dispose.</summary>
sealed class TestTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "DiskScopeTest_" + Guid.NewGuid().ToString("N"));

    public TestTree()
    {
        Directory.CreateDirectory(Root);
    }

    public string File(string relative, int bytes)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string Dir(string relative)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}
