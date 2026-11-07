using System.Runtime.InteropServices;

namespace DiskScope.UI;

/// <summary>Windows shell operations: Explorer, recycle bin, the native context menu.</summary>
static class Shell
{
    // --- Explorer ---

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, nint bindCtx, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    static extern int SHOpenFolderAndSelectItems(nint pidlFolder, uint count, nint[]? children, uint flags);

    [DllImport("shell32.dll")]
    static extern void ILFree(nint pidl);

    /// <summary>Opens an Explorer window with the item selected.</summary>
    public static bool RevealInExplorer(string path)
    {
        if (SHParseDisplayName(path, 0, out nint pidl, 0, out _) != 0) return false;
        try
        {
            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
        }
        finally
        {
            ILFree(pidl);
        }
    }

    // --- delete ---

    const uint FO_DELETE = 3;
    const ushort FOF_NOCONFIRMATION = 0x10;
    const ushort FOF_ALLOWUNDO = 0x40;
    const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// Deletes files and folders through the shell (with its progress dialog), to the recycle bin
    /// unless <paramref name="permanent"/>. The caller has already asked for confirmation.
    /// </summary>
    public static void Delete(IWin32Window owner, IEnumerable<string> paths, bool permanent)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner.Handle,
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = (ushort)(FOF_NOCONFIRMATION | (permanent ? 0 : FOF_ALLOWUNDO | FOF_WANTNUKEWARNING)),
        };
        SHFileOperation(ref op);
    }

    // --- native context menu ---

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        void ParseDisplayName();
        void EnumObjects();
        void BindToObject();
        void BindToStorage();
        void CompareIDs();
        void CreateViewObject();
        void GetAttributesOf();
        [PreserveSig]
        int GetUIObjectOf(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] nint[] pidls, ref Guid riid, nint reserved, out nint ppv);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public nint hIcon;
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint hmenu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO info);
        [PreserveSig] int GetCommandString(nuint cmd, uint type, nint reserved, nint name, uint max);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint hmenu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO info);
        [PreserveSig] int GetCommandString(nuint cmd, uint type, nint reserved, nint name, uint max);
        [PreserveSig] int HandleMenuMsg(uint msg, nint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, nint wParam, nint lParam, out nint result);
    }

    [DllImport("shell32.dll")]
    static extern int SHBindToParent(nint pidl, ref Guid riid, out IShellFolder folder, out nint childPidl);

    [DllImport("user32.dll")]
    static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    const uint CMF_NORMAL = 0;
    const uint CMF_EXTENDEDVERBS = 0x100;
    const uint TPM_RETURNCMD = 0x100;
    const uint FirstCommand = 1;

    static IContextMenu3? _active;

    /// <summary>Forwards owner-drawn submenu messages ("Open with", "Send to") while the menu is open.</summary>
    public static bool HandleMenuMessage(ref Message m)
    {
        const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
        if (_active == null || m.Msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)) return false;
        if (_active.HandleMenuMsg2((uint)m.Msg, m.WParam, m.LParam, out nint result) != 0) return false;
        m.Result = result;
        return true;
    }

    /// <summary>Shows Explorer's context menu for items in one folder. Returns true when a command was run.</summary>
    public static bool ShowContextMenu(IWin32Window owner, IReadOnlyList<string> paths, Point screen, bool extended)
    {
        var pidls = new List<nint>();
        nint menu = 0;
        object? cm = null;
        try
        {
            foreach (string p in paths)
                if (SHParseDisplayName(p, 0, out nint pidl, 0, out _) == 0) pidls.Add(pidl);
            if (pidls.Count == 0) return false;

            var iidFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidls[0], ref iidFolder, out var folder, out _) != 0) return false;
            var children = new nint[pidls.Count];
            for (int i = 0; i < pidls.Count; i++)
            {
                SHBindToParent(pidls[i], ref iidFolder, out var f, out children[i]);
                Marshal.ReleaseComObject(f);
            }
            var iidMenu = typeof(IContextMenu).GUID;
            int hr = folder.GetUIObjectOf(owner.Handle, (uint)children.Length, children, ref iidMenu, 0, out nint unk);
            Marshal.ReleaseComObject(folder);
            if (hr != 0) return false;
            cm = Marshal.GetObjectForIUnknown(unk);
            Marshal.Release(unk);

            var contextMenu = (IContextMenu)cm;
            menu = CreatePopupMenu();
            contextMenu.QueryContextMenu(menu, 0, FirstCommand, 0x7FFF, CMF_NORMAL | (extended ? CMF_EXTENDEDVERBS : 0));
            _active = cm as IContextMenu3;
            uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD, screen.X, screen.Y, owner.Handle, 0);
            _active = null;
            if (cmd < FirstCommand) return false;

            var info = new CMINVOKECOMMANDINFO
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                hwnd = owner.Handle,
                lpVerb = (nint)(cmd - FirstCommand),
                nShow = 1,
            };
            contextMenu.InvokeCommand(ref info);
            return true;
        }
        finally
        {
            _active = null;
            if (menu != 0) DestroyMenu(menu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            foreach (nint p in pidls) ILFree(p);
        }
    }
}
