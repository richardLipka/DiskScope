using DiskScope.Core.Duplicates;

namespace DiskScope.Tests;

/// <summary>Replacing duplicate files with hard links to one kept file.</summary>
public class HardLinkTests
{
    static byte[] Content(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void ACopyBecomesALinkToTheKeptFile()
    {
        using var t = new TestTree();
        var data = Content(300_000, 1);
        string keeper = Path.Combine(t.Root, "photos", "a.jpg");
        string copy = Path.Combine(t.Root, "backup", "a (copy).jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(keeper)!);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(keeper, data);
        File.WriteAllBytes(copy, data);
        File.SetAttributes(copy, FileAttributes.ReadOnly);

        Assert.Null(HardLinker.Replace(keeper, copy, data.Length, File.GetLastWriteTimeUtc(copy).ToFileTimeUtc()));
        Assert.Equal(2, HardLinker.LinkCount(keeper));
        Assert.Equal(2, HardLinker.LinkCount(copy));
        Assert.Equal(data, File.ReadAllBytes(copy));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(copy)!, "*.dslink-*")); // no temporary link left
    }

    [Fact]
    public void AChangedCopyIsLeftAlone()
    {
        using var t = new TestTree();
        var data = Content(200_000, 2);
        string keeper = Path.Combine(t.Root, "a.bin"), copy = Path.Combine(t.Root, "b.bin");
        File.WriteAllBytes(keeper, data);
        File.WriteAllBytes(copy, data);
        long scannedTime = File.GetLastWriteTimeUtc(copy).ToFileTimeUtc();
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(5)); // written to since the scan
        Assert.Equal(HardLinker.ErrorChanged, HardLinker.Replace(keeper, copy, data.Length, scannedTime));
        Assert.Equal(HardLinker.ErrorChanged, HardLinker.Replace(keeper, copy, data.Length + 1, null));
        Assert.Equal(1, HardLinker.LinkCount(copy));
    }

    [Fact]
    public void DifferentContentOfTheSameSizeIsNotLinked()
    {
        using var t = new TestTree();
        string keeper = Path.Combine(t.Root, "a.bin"), copy = Path.Combine(t.Root, "b.bin");
        File.WriteAllBytes(keeper, Content(200_000, 3));
        File.WriteAllBytes(copy, Content(200_000, 4));
        Assert.Equal(HardLinker.ErrorDiffers, HardLinker.Replace(keeper, copy, 200_000, null));
        Assert.Equal(1, HardLinker.LinkCount(copy));
    }

    [Fact]
    public void AMissingFileIsReported()
    {
        using var t = new TestTree();
        string keeper = t.File("a.bin", 1000);
        Assert.NotNull(HardLinker.Replace(keeper, Path.Combine(t.Root, "gone.bin"), 1000, null));
    }
}
