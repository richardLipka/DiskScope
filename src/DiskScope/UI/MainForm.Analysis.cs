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

    void BuildAnalysis()
    {
        _analysisMenu.DropDownItems.AddRange([_orphanItem, _duplicateItem, _mapItem]);
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
        _duplicates.CurrentFolder = () => _rows?.ViewRoot ?? NodeStore.None;
        _duplicates.RevealRequested += RevealInTree;
        _duplicates.ResultMenu = BuildNodeListMenu(() => _duplicates.SelectedNodes(), () => _duplicates.FocusedNode);

        _bottomTabs.TabPages.AddRange([_orphanTab, _duplicateTab, _mapTab]);
    }

    bool _selectingFromMap;

    /// <summary>Shows the folder selected in the tree (or the folder of the selected file) in the map.</summary>
    void FollowTreeInMap()
    {
        // A click in the map selects in the tree, but must not move the map away from what the user is looking at.
        if (_selectingFromMap || !BottomVisible(_mapTab) || _rows == null) return;
        int node = _tree.FocusedNode;
        if (node == NodeStore.None) return;
        int folder = _rows.Store[node].IsDirectory ? node : _rows.Store[node].Parent;
        if (folder != NodeStore.None && folder != _map.Folder) _map.ShowFolder(folder);
    }

    void ApplyAnalysisTexts()
    {
        _analysisMenu.Text = "📊 " + L.T("Analyses");
        _orphanItem.Text = _orphanTab.Text = L.T("OrphanTab");
        _duplicateItem.Text = L.T("DupItem");
        _duplicateTab.Text = L.T("DupTab");
        _mapItem.Text = _mapTab.Text = L.T("MapTab");
        _orphans.ApplyTexts();
        _duplicates.ApplyTexts();
        _map.ApplyTexts();
    }

    void SetAnalysisStore(NodeStore store)
    {
        _orphans.SetStore(store);
        _duplicates.SetStore(store);
        _map.SetStore(store);
    }

    /// <summary>Fills a panel when it is shown; duplicates are searched only on request.</summary>
    void RefreshAnalysisTab(TabPage? tab)
    {
        if (tab == _orphanTab) _orphans.Run();
        else if (tab == _mapTab && _rows != null)
        {
            int node = _tree.FocusedNode;
            _map.ShowFolder(node == NodeStore.None ? _rows.ViewRoot : node);
        }
    }

    void RefreshAnalysisAfterChange()
    {
        if (BottomVisible(_orphanTab)) _orphans.RemoveDead();
        else if (BottomVisible(_duplicateTab)) _duplicates.RemoveDead();
        else if (BottomVisible(_mapTab)) _map.Relayout();
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
    internal void ShowMapForTest() => ShowBottom(_mapTab);
    internal DuplicatePanel DuplicatePanelForTest => _duplicates;
    internal void ShowDuplicatesForTest() => ShowBottom(_duplicateTab);
    internal OrphanPanel OrphanPanelForTest => _orphans;
    internal void ShowOrphansForTest() => ShowBottom(_orphanTab);
}
