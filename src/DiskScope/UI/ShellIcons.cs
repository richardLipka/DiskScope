using System.Runtime.InteropServices;

namespace DiskScope.UI;

/// <summary>Small system icons by file extension, cached (looked up without touching the files).</summary>
static class ShellIcons
{
    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_SMALLICON = 0x1;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(nint icon);

    static readonly Dictionary<string, Icon?> Cache = new(StringComparer.OrdinalIgnoreCase);
    const string FolderKey = "\0folder";
    const string DriveKey = "\0drive";

    public static Icon? Folder => Get(FolderKey, "folder", FILE_ATTRIBUTE_DIRECTORY);

    public static Icon? Drive(string root) => Get(DriveKey + root, root, 0, useAttributes: false);

    public static Icon? ForFile(string name)
    {
        string ext = Path.GetExtension(name);
        // Files with embedded icons (.exe, .ico, .lnk) get their generic type icon.
        return Get(ext.Length == 0 ? "." : ext, "file" + ext, FILE_ATTRIBUTE_NORMAL);
    }

    static Icon? Get(string key, string path, uint attributes, bool useAttributes = true)
    {
        if (Cache.TryGetValue(key, out var icon)) return icon;
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | (useAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
        SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (info.hIcon != 0)
        {
            icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
        }
        Cache[key] = icon;
        return icon;
    }
}
