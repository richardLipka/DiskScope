using System.Text;
using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>
/// Writes a question about a file or folder that the user can paste into an AI assistant of their choice
/// to learn what the item belongs to and whether it is safe to delete. Nothing is sent anywhere by the app.
/// </summary>
static class AiPrompt
{
    public static string Build(NodeStore store, int node)
    {
        ref var n = ref store[node];
        var sb = new StringBuilder();
        sb.AppendLine(L.T(n.IsDirectory ? "AiIntroFolder" : "AiIntroFile"));
        sb.AppendLine();
        sb.AppendLine(L.T("AiPath", store.GetPath(node)));
        sb.AppendLine(L.T("AiSize", Size(n.Size), Size(n.Length)));
        if (n.IsDirectory) sb.AppendLine(L.T("AiCounts", Format.Count(n.FileCount, L.Culture), Format.Count(n.DirCount, L.Culture)));
        sb.AppendLine(L.T(n.IsDirectory ? "AiTimesFolder" : "AiTimesFile", Date(n.MTime), Date(n.ATime)));
        if (Attributes(n.Attributes) is { Length: > 0 } attrs) sb.AppendLine(L.T("AiAttributes", attrs));

        if (n.IsDirectory)
        {
            var summary = FolderSummary.Build(store, node);
            if (summary.LargestChildren.Count > 0)
            {
                sb.AppendLine().AppendLine(L.T("AiChildren"));
                foreach (int c in summary.LargestChildren)
                    sb.AppendLine($"- {store.GetName(c)}{(store[c].IsDirectory ? "\\" : "")} — {Size(store[c].Size)}, " +
                                  L.T("AiChanged", Date(store[c].MTime)));
            }
            if (summary.Extensions.Count > 0)
            {
                sb.AppendLine().AppendLine(L.T("AiExtensions"));
                foreach (var (ext, size, count) in summary.Extensions)
                    sb.AppendLine($"- {(ext.Length == 0 ? L.T("AiNoExtension") : ext)} — {Size(size)}, {L.T("AiFileCount", Format.Count(count, L.Culture))}");
            }
            if (summary.LargestFiles.Count > 0)
            {
                sb.AppendLine().AppendLine(L.T("AiLargestFiles"));
                foreach (int f in summary.LargestFiles)
                    sb.AppendLine($"- {RelativePath(store, f, node)} — {Size(store[f].Size)}, " + L.T("AiChanged", Date(store[f].MTime)));
            }
            if (summary.Truncated) sb.AppendLine().AppendLine(L.T("AiTruncated"));
        }
        else if (n.Parent != NodeStore.None)
        {
            // A few neighbours often reveal which application a file belongs to.
            var siblings = new List<int>();
            for (int c = store[n.Parent].FirstChild; c != NodeStore.None; c = store[c].NextSibling)
                if (c != node) siblings.Add(c);
            store.SortBySizeDescending(siblings);
            if (siblings.Count > 0)
            {
                sb.AppendLine().AppendLine(L.T("AiSiblings"));
                foreach (int c in siblings.Take(10))
                    sb.AppendLine($"- {store.GetName(c)}{(store[c].IsDirectory ? "\\" : "")} — {Size(store[c].Size)}");
            }
        }

        sb.AppendLine().AppendLine(L.T("AiQuestions"));
        return sb.ToString();
    }

    static string Size(long bytes) => Format.Size(bytes, L.Culture);

    static string Date(long filetime) => Format.FileTime(filetime)?.ToLocalTime().ToString("d", L.Culture) ?? "?";

    static string RelativePath(NodeStore store, int node, int ancestor)
    {
        var parts = new List<string>();
        for (int i = node; i != ancestor && i != NodeStore.None; i = store[i].Parent) parts.Add(store.GetName(i));
        parts.Reverse();
        return string.Join('\\', parts);
    }

    static string Attributes(uint attrs)
    {
        var list = new List<string>();
        if ((attrs & 0x2) != 0) list.Add(L.T("AttrHidden"));
        if ((attrs & 0x4) != 0) list.Add(L.T("AttrSystem"));
        if ((attrs & 0x1) != 0) list.Add(L.T("AttrReadOnly"));
        if ((attrs & 0x800) != 0) list.Add(L.T("AttrCompressed"));
        if ((attrs & 0x400000) != 0) list.Add(L.T("AttrCloud"));
        return string.Join(", ", list);
    }
}
