using DiskScope.Core;

namespace DiskScope.Tests;

public class TreemapTests
{
    [Fact]
    public void SquarifyFillsTheRectangleProportionally()
    {
        var bounds = new RectF(0, 0, 600, 400);
        var areas = new List<double> { 6, 6, 4, 3, 2, 2, 1 }.Select(a => a * 600 * 400 / 24).ToList();
        var rects = TreemapLayout.Squarify(areas, bounds);

        Assert.Equal(areas.Count, rects.Count);
        for (int i = 0; i < rects.Count; i++)
        {
            Assert.Equal(areas[i], rects[i].Width * rects[i].Height, 0.5);
            Assert.True(rects[i].X >= -0.01 && rects[i].Y >= -0.01 && rects[i].Right <= 600.01 && rects[i].Bottom <= 400.01);
        }
        // No two rectangles overlap.
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                float w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X), h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
                Assert.False(w > 0.01 && h > 0.01, $"{i} overlaps {j}");
            }
        // Squarified: no extremely thin slices for this classic example.
        Assert.All(rects, r => Assert.True(Math.Max(r.Width / r.Height, r.Height / r.Width) < 4));
    }

    [Fact]
    public void BuildsNestedItemsInsideFolders()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        int Link(int parent, int child)
        {
            s[child].NextSibling = s[parent].FirstChild;
            s[parent].FirstChild = child;
            return child;
        }
        int dir = Link(root, s.Add(root, "big", NodeFlags.Directory, 0, 0, 0, 0));
        Link(dir, s.Add(dir, "a.mkv", NodeFlags.None, 0, 600, 600, 0));
        Link(dir, s.Add(dir, "b.mkv", NodeFlags.None, 0, 200, 200, 0));
        Link(root, s.Add(root, "c.zip", NodeFlags.None, 0, 200, 200, 0));
        Link(root, s.Add(root, "empty.txt", NodeFlags.None, 0, 0, 0, 0));
        s.ComputeTotalsFromRoot();

        var items = TreemapLayout.Build(s, root, new RectF(0, 0, 500, 200), padding: 2);
        var byName = items.ToDictionary(i => s.GetName(i.Node));

        Assert.Equal(["a.mkv", "b.mkv", "big", "c.zip"], byName.Keys.Order());
        Assert.Equal(0.8 * 500 * 200, byName["big"].Bounds.Width * byName["big"].Bounds.Height, 1);
        Assert.Equal(1, byName["a.mkv"].Depth);
        var inner = byName["big"].Bounds.Inset(2);
        Assert.True(inner.Contains(byName["a.mkv"].Bounds.X + 0.1f, byName["a.mkv"].Bounds.Y + 0.1f));
        Assert.True(byName["a.mkv"].Bounds.Width * byName["a.mkv"].Bounds.Height > 2.9 * byName["b.mkv"].Bounds.Width * byName["b.mkv"].Bounds.Height);
    }
}
