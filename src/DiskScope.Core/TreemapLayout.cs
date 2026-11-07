namespace DiskScope.Core;

public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;
    public RectF Inset(float d) => new(X + d, Y + d, Math.Max(0, Width - 2 * d), Math.Max(0, Height - 2 * d));
}

/// <summary>One rectangle of the map: a file, or a folder whose children are drawn inside it.</summary>
public readonly record struct TreemapItem(int Node, RectF Bounds, int Depth, bool IsDirectory);

/// <summary>
/// Squarified treemap layout (Bruls, Huizing, van Wijk): areas are proportional to on-disk size and rectangles are
/// kept close to squares. Folders are inset by a small padding so their outline stays visible. Items smaller than
/// <paramref name="minSide"/> pixels are not laid out; their area stays part of the parent.
/// </summary>
public static class TreemapLayout
{
    public static List<TreemapItem> Build(NodeStore store, int root, RectF area, int maxDepth = 8, float minSide = 3, float padding = 2)
    {
        var items = new List<TreemapItem>();
        if (store[root].Size <= 0 || area.Width < 1 || area.Height < 1) return items;
        Layout(store, root, area, 0, maxDepth, minSide, padding, items);
        return items;
    }

    static void Layout(NodeStore store, int dir, RectF area, int depth, int maxDepth, float minSide, float padding, List<TreemapItem> items)
    {
        var children = new List<int>();
        for (int c = store[dir].FirstChild; c != NodeStore.None; c = store[c].NextSibling)
            if (store[c].Size > 0 && store.IsLive(c)) children.Add(c);
        if (children.Count == 0) return;
        children.Sort((a, b) => store[b].Size.CompareTo(store[a].Size));

        double total = store[dir].Size;
        double scale = area.Width * (double)area.Height / total;
        var rects = Squarify(children.Select(c => store[c].Size * scale).ToList(), area);
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (r.Width < minSide || r.Height < minSide) continue;
            int node = children[i];
            bool isDir = store[node].IsDirectory;
            items.Add(new TreemapItem(node, r, depth, isDir));
            if (isDir && depth + 1 < maxDepth)
            {
                var inner = r.Inset(padding);
                if (inner.Width >= minSide && inner.Height >= minSide)
                    Layout(store, node, inner, depth + 1, maxDepth, minSide, padding, items);
            }
        }
    }

    /// <summary>Lays out areas (already scaled to pixels, sorted descending) inside a rectangle, in the same order.</summary>
    internal static List<RectF> Squarify(List<double> areas, RectF bounds)
    {
        var result = new List<RectF>(areas.Count);
        var rect = bounds;
        int start = 0;
        while (start < areas.Count)
        {
            double side = Math.Min(rect.Width, rect.Height);
            if (side <= 0) break;
            // Grow the row while the worst aspect ratio in it improves.
            int end = start + 1;
            double rowSum = areas[start];
            double worst = Worst(areas, start, end, rowSum, side);
            while (end < areas.Count)
            {
                double candidate = Worst(areas, start, end + 1, rowSum + areas[end], side);
                if (candidate > worst) break;
                rowSum += areas[end];
                worst = candidate;
                end++;
            }

            // Place the row along the shorter side.
            bool horizontal = rect.Width >= rect.Height; // the row is a column at the left edge when the rect is wide
            double thickness = rowSum / side;
            double offset = 0;
            for (int i = start; i < end; i++)
            {
                double length = areas[i] / thickness;
                result.Add(horizontal
                    ? new RectF(rect.X, (float)(rect.Y + offset), (float)thickness, (float)length)
                    : new RectF((float)(rect.X + offset), rect.Y, (float)length, (float)thickness));
                offset += length;
            }
            rect = horizontal
                ? new RectF((float)(rect.X + thickness), rect.Y, (float)Math.Max(0, rect.Width - thickness), rect.Height)
                : new RectF(rect.X, (float)(rect.Y + thickness), rect.Width, (float)Math.Max(0, rect.Height - thickness));
            start = end;
        }
        return result;
    }

    static double Worst(List<double> areas, int start, int end, double sum, double side)
    {
        double max = areas[start], min = areas[end - 1]; // sorted descending
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * Math.Max(min, 1e-9)));
    }
}
