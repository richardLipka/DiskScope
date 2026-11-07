using DiskScope.Core;
using DiskScope.Core.Deletion;
using DiskScope.Core.Duplicates;
using DiskScope.Core.Scanning;

namespace DiskScope.Tests;

/// <summary>
/// Names ending in a dot or a space are legal on NTFS (WSL creates them), but normal Windows paths drop the ending:
/// "abc." becomes "abc". Every operation must hit the item the tree shows, never its look-alike.
/// </summary>
public class TrailingDotTests
{
    static string Ext(string path) => @"\\?\" + path;

    static void WriteExt(string path, byte[] data)
    {
        Directory.CreateDirectory(Ext(Path.GetDirectoryName(path)!));
        File.WriteAllBytes(Ext(path), data);
    }

    static (TestTree Tree, NodeStore Store) Setup()
    {
        var t = new TestTree();
        var a = new byte[3000];
        var b = new byte[3000];
        new Random(1).NextBytes(a);
        new Random(2).NextBytes(b);
        WriteExt(Path.Combine(t.Root, "abc."), a);       // the look-alike pair: "abc." and "abc"
        WriteExt(Path.Combine(t.Root, "abc"), b);
        WriteExt(Path.Combine(t.Root, "copy of abc"), a); // same content as "abc."
        WriteExt(Path.Combine(t.Root, @"dir.\inner.bin"), a);
        WriteExt(Path.Combine(t.Root, @"dir\inner.bin"), b);
        WriteExt(Path.Combine(t.Root, "space "), a);
        WriteExt(Path.Combine(t.Root, "space"), b);
        var session = new ScanSession(t.Root, ScanMode.Directory);
        session.Start();
        session.Wait();
        return (t, session.Store);
    }

    [Fact]
    public void ScannerShowsBothLookAlikes()
    {
        var (t, s) = Setup();
        using (t)
        {
            foreach (string name in new[] { "abc.", "abc", "dir.", "dir", "space ", "space" })
                Assert.NotEqual(NodeStore.None, s.FindChild(s.Root, name));
            Assert.Equal(7, s[s.Root].FileCount);
        }
    }

    [Fact]
    public void PermanentDeleteHitsOnlyTheNamedItem()
    {
        var (t, s) = Setup();
        using (t)
        {
            using var queue = new DeleteQueue(new FileDeleter());
            foreach (string name in new[] { "abc.", "dir.", "space " })
            {
                int n = s.FindChild(s.Root, name);
                queue.Enqueue([new DeleteJob(s.GetPath(n), s[n].IsDirectory, s[n].Size, DeleteMethod.Permanent)
                    { ExpectedLength = s[n].IsDirectory ? null : s[n].Length, ExpectedMTime = s[n].IsDirectory ? null : s[n].MTime }]);
            }
            Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(30)));
            Assert.All(queue.Jobs, j => Assert.Equal(DeleteState.Done, j.State));

            Assert.False(File.Exists(Ext(Path.Combine(t.Root, "abc."))));
            Assert.False(Directory.Exists(Ext(Path.Combine(t.Root, "dir."))));
            Assert.False(File.Exists(Ext(Path.Combine(t.Root, "space "))));
            // The look-alikes are untouched.
            Assert.True(File.Exists(Path.Combine(t.Root, "abc")));
            Assert.True(File.Exists(Path.Combine(t.Root, @"dir\inner.bin")));
            Assert.True(File.Exists(Path.Combine(t.Root, "space")));
        }
    }

    [Fact]
    public void RecycleBinRefusesSuchNamesInsteadOfRecyclingTheLookAlike()
    {
        var (t, s) = Setup();
        using (t)
        {
            var result = new FileDeleter().Delete(s.GetPath(s.FindChild(s.Root, "abc.")), false, DeleteMethod.RecycleBin, CancellationToken.None);
            Assert.False(result.Gone);
            Assert.Equal(FileDeleter.ErrorUnsafeName, result.Error);
            Assert.True(File.Exists(Ext(Path.Combine(t.Root, "abc."))));
            Assert.True(File.Exists(Path.Combine(t.Root, "abc")));
        }
    }

    [Fact]
    public void ChangeCheckAndExistenceLookAtTheRightFile()
    {
        var (t, s) = Setup();
        using (t)
        {
            int dotted = s.FindChild(s.Root, "abc.");
            // Make the look-alike different in size: a check that looked at "abc" would now refuse.
            File.WriteAllBytes(Path.Combine(t.Root, "abc"), new byte[10]);
            using var queue = new DeleteQueue(new FileDeleter());
            queue.Enqueue([new DeleteJob(s.GetPath(dotted), false, s[dotted].Size, DeleteMethod.Permanent)
                { ExpectedLength = s[dotted].Length, ExpectedMTime = s[dotted].MTime }]);
            Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(30)));
            Assert.Equal(DeleteState.Done, queue.Jobs[0].State);

            Assert.True(TreeSync.ExistsOnDisk(s, s.FindChild(s.Root, "abc")));
            Assert.False(TreeSync.ExistsOnDisk(s, dotted));
        }
    }

    [Fact]
    public void DuplicateSearchReadsTheRightFiles()
    {
        var (t, s) = Setup();
        using (t)
        {
            var groups = new DuplicateFinder { MinSize = 1 }.Find(s, new DuplicateProgress());
            var names = groups.Select(g => g.Nodes.Select(s.GetPath).Select(p => p[(t.Root.Length + 1)..]).Order().ToArray()).ToList();
            // "abc.", "copy of abc", "dir.\inner.bin" and "space " share content A; "abc", "dir\inner.bin", "space" share B.
            Assert.Contains(names, g => g.SequenceEqual(new[] { "abc.", "copy of abc", @"dir.\inner.bin", "space " }));
            Assert.Contains(names, g => g.SequenceEqual(new[] { "abc", @"dir\inner.bin", "space" }));
        }
    }

    [Theory]
    [InlineData(@"C:\a\abc.", true)]
    [InlineData(@"C:\a.\b", true)]
    [InlineData(@"C:\a\space ", true)]
    [InlineData(@"C:\a\b.txt", false)]
    [InlineData(@"\\?\C:\a\b", false)]
    public void DetectsUnsafeNames(string path, bool unsafeName) => Assert.Equal(unsafeName, LongPath.HasUnsafeName(path));
}
