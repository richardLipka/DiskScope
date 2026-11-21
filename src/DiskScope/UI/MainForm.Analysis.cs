using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>Analysis panels below the tree (abandoned data, duplicates, treemap), opened from the Analyses menu.</summary>
sealed partial class MainForm
{
    readonly ToolStripDropDownButton _analysisMenu = new();
    readonly ToolStripMenuItem _orphanItem = new();
    readonly TabPage _orphanTab = new();
    readonly OrphanPanel _orphans = new() { Dock = DockStyle.Fill };
    readonly ToolStripMenuItem _duplicateItem = new();
    readonly TabPage _duplicateTab = new();
    readonly DuplicatePanel _duplicates = new() { Dock = DockStyle.Fill };
    readonly ToolStripMenuItem _mapItem = new();
    readonly TabPage _mapTab = new();
    readonly TreemapPanel _map = new() { Dock = DockStyle.Fill };
    readonly ToolStripMenuItem _typesItem = new();
    readonly TabPage _typesTab = new();
    readonly FileTypesPanel _types = new() { Dock = DockStyle.Fill };

    void BuildAnalysis()
    {
        _analysisMenu.DropDownItems.AddRange([_typesItem, _mapItem, new ToolStripSeparator(), _orphanItem, _duplicateItem]);
        _typesItem.Click += (_, _) => ShowBottom(_typesTab);
        _typesTab.Controls.Add(_types);
        _types.RevealRequested += RevealInTree;
        _types.ResultMenu = BuildNodeListMenu(() => _types.SelectedNodes(), () => _types.FocusedNode);
        _mapItem.Click += (_, _) => ShowBottom(_mapTab);
        _mapTab.Controls.Add(_map);
        _map.NodeClicked += node =>
        {
            if (_rows == null || !_rows.Store.IsLive(node)) return;
            _selectingFromMap = true;
            try
            {
                _tree.SelectNode(node);
                UpdateBreadcrumb();
            }
            finally
            {
                _selectingFromMap = false;
            }
        };
        // The map follows the folder selected in the tree.
        _tree.SelectedIndexChanged += (_, _) => FollowTreeInMap();

        _orphanItem.Click += (_, _) => ShowBottom(_orphanTab);
        _orphanTab.Controls.Add(_orphans);
        _orphans.Catalog = () => _cleanup.Catalog;
        _orphans.RevealRequested += RevealInTree;
        _orphans.ResultMenu = BuildNodeListMenu(() => _orphans.SelectedNodes(), () => _orphans.FocusedNode);

        _duplicateItem.Click += (_, _) => ShowBottom(_duplicateTab);
        _duplicateTab.Controls.Add(_duplicates);
        _duplicates.SelectedFolder = SelectedTreeFolder;
        _tree.SelectedIndexChanged += (_, _) =>
        {
            if (BottomVisible(_duplicateTab)) _duplicates.UpdateFolder();
        };
        _duplicates.RevealRequested += RevealInTree;
        _duplicates.CanModify = () => !IsBusy;
        _duplicates.MayReplace = path => _guard.CheckResolved(path) == Core.Deletion.DeletionGuard.Verdict.Allowed;
        _duplicates.Linked += OnDuplicatesLinked;
        _duplicates.ResultMenu = BuildNodeListMenu(() => _duplicates.SelectedNodes(), () => _duplicates.FocusedNode);

        _bottomTabs.TabPages.AddRange([_typesTab, _mapTab, _orphanTab, _duplicateTab]);
    }

    /// <summary>The folder selected in the tree, or the folder of the selected file; the view root when nothing is selected.</summary>
    int SelectedTreeFolder()
    {
        if (_rows == null) return NodeStore.None;
        int node = _tree.FocusedNode;
        if (node == NodeStore.None) return _rows.ViewRoot;
        return _rows.Store[node].IsDirectory ? node : _rows.Store[node].Parent;
    }

    /// <summary>Opens the duplicates panel and searches the given folder with its subfolders.</summary>
    void FindDuplicatesIn(int folder)
    {
        if (_rows == null || folder == NodeStore.None) return;
        ShowBottom(_duplicateTab);
        _duplicates.StartIn(folder);
    }

    bool _selectingFromMap;

    /// <summary>Shows the folder selected in the tree (or the folder of the selected file) in the map.</summary>
    void FollowTreeInMap()
    {
        // A click in the map selects in the tree, but must not move the map away from what the user is looking at.
        if (_selectingFromMap || _rows == null || !(BottomVisible(_mapTab) || BottomVisible(_typesTab))) return;
        int node = _tree.FocusedNode;
        if (node == NodeStore.None) return;
        int folder = _rows.Store[node].IsDirectory ? node : _rows.Store[node].Parent;
        if (folder == NodeStore.None) return;
        if (BottomVisible(_mapTab) && folder != _map.Folder) _map.ShowFolder(folder);
        if (BottomVisible(_typesTab) && folder != _types.Folder) _types.ShowFolder(folder);
    }

    void ApplyAnalysisTexts()
    {
        _analysisMenu.Text = "📊 " + L.T("Analyses");
        _orphanItem.Text = _orphanTab.Text = L.T("OrphanTab");
        _duplicateItem.Text = L.T("DupItem");
        _duplicateTab.Text = L.T("DupTab");
        _mapItem.Text = _mapTab.Text = L.T("MapTab");
        _typesItem.Text = _typesTab.Text = L.T("TypesTab");
        _types.ApplyTexts();
        _orphans.ApplyTexts();
        _duplicates.ApplyTexts();
        _map.ApplyTexts();
    }

    void SetAnalysisStore(NodeStore? store)
    {
        _orphans.SetStore(store);
        _duplicates.SetStore(store);
        _map.SetStore(store);
        _types.SetStore(store);
    }

    /// <summary>Fills a panel when it is shown; duplicates are searched only on request.</summary>
    void RefreshAnalysisTab(TabPage? tab)
    {
        if (tab == _orphanTab) _orphans.Run();
        else if (tab == _duplicateTab) _duplicates.UpdateFolder();
        else if (tab == _mapTab && _rows != null)
        {
            int node = _tree.FocusedNode;
            _map.ShowFolder(node == NodeStore.None ? _rows.ViewRoot : node);
        }
        else if (tab == _typesTab && _rows != null)
        {
            int node = _tree.FocusedNode;
            _types.ShowFolder(node == NodeStore.None ? _rows.ViewRoot : node);
        }
    }

    void RefreshAnalysisAfterChange()
    {
        if (BottomVisible(_orphanTab)) _orphans.RemoveDead();
        else if (BottomVisible(_duplicateTab)) _duplicates.RemoveDead();
        if (BottomVisible(_duplicateTab)) _duplicates.UpdateFolder();
        else if (BottomVisible(_mapTab)) _map.Relayout();
        else if (BottomVisible(_typesTab)) _types.Refresh();
    }

    /// <summary>
    /// Copies now hard links of a kept file: they take no space of their own any more. Shown like the hard links an
    /// MFT scan finds, with their size counted at the kept file only.
    /// </summary>
    void OnDuplicatesLinked(List<int> nodes)
    {
        if (_rows == null || nodes.Count == 0) return;
        var store = _rows.Store;
        long saved = 0;
        foreach (int n in nodes)
        {
            if (!store.IsLive(n)) continue;
            long size = store[n].Size;
            store[n].Size = 0;
            store.SetFlag(n, NodeFlags.HardLink);
            if (store[n].Parent != NodeStore.None) store.AddToAncestors(store[n].Parent, -size, 0, 0, 0);
            saved += size;
        }
        store.MarkChanged();
        _freedThisSession += saved;
        UpdateQueueStatus();
        _tree.Reload();
        UpdateDetail();
    }

    /// <summary>The usual actions for a list of tree nodes: show, open, Explorer, copy, AI question, delete.</summary>
    ContextMenuStrip BuildNodeListMenu(Func<List<int>> selected, Func<int> focused)
    {
        var menu = new ContextMenuStrip();
        var show = new ToolStripMenuItem { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        var open = new ToolStripMenuItem();
        var reveal = new ToolStripMenuItem();
        var copy = new ToolStripMenuItem();
        var ai = new ToolStripMenuItem();
        var recycle = new ToolStripMenuItem { ShortcutKeyDisplayString = "Del" };
        var delete = new ToolStripMenuItem { ShortcutKeyDisplayString = "Shift+Del" };
        menu.Items.AddRange([show, open, reveal, copy, ai, new ToolStripSeparator(), recycle, delete]);
        show.Click += (_, _) => RevealInTree(focused());
        open.Click += (_, _) => OpenNode(focused());
        reveal.Click += (_, _) => RevealNode(focused());
        copy.Click += (_, _) => CopyNodePaths(selected());
        ai.Click += (_, _) => CopyAiPrompt(focused());
        recycle.Click += (_, _) => DeleteNodes(selected(), permanent: false);
        delete.Click += (_, _) => DeleteNodes(selected(), permanent: true);
        menu.Opening += (_, e) =>
        {
            int count = selected().Count;
            if (count == 0)
            {
                e.Cancel = true;
                return;
            }
            show.Text = L.T("ShowInTree");
            open.Text = L.T("Open");
            reveal.Text = L.T("Reveal");
            copy.Text = L.T("CopyPath");
            ai.Text = L.T("CopyAiPrompt");
            recycle.Text = L.T("Recycle");
            delete.Text = L.T("DeletePermanent");
            show.Enabled = open.Enabled = reveal.Enabled = ai.Enabled = count == 1;
        };
        return menu;
    }

    internal TreemapPanel MapPanelForTest => _map;
    internal FileTypesPanel TypesPanelForTest => _types;
    internal void ShowTypesForTest() => ShowBottom(_typesTab);
    internal void ShowMapForTest() => ShowBottom(_mapTab);
    internal DuplicatePanel DuplicatePanelForTest => _duplicates;

    /// <summary>Test hook: selects the folder in the tree and searches it, as the tree's context menu does.</summary>
    internal void FindDuplicatesInForTest(string path)
    {
        if (_rows?.Store.FindPath(path) is int node && node != NodeStore.None)
        {
            _tree.SelectNode(node);
            FindDuplicatesIn(SelectedTreeFolder());
        }
    }
    internal void ShowDuplicatesForTest() => ShowBottom(_duplicateTab);
    internal OrphanPanel OrphanPanelForTest => _orphans;
    internal void ShowOrphansForTest() => ShowBottom(_orphanTab);
}
