using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>The panels below the tree (search, cleanup) and the actions on their results.</summary>
sealed partial class MainForm
{
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Panel2Collapsed = true };
    readonly TabControl _bottomTabs = new() { Dock = DockStyle.Fill };
    readonly TabPage _searchTab = new();
    readonly TabPage _cleanupTab = new();
    readonly SearchPanel _search = new() { Dock = DockStyle.Fill };
    readonly ToolStripButton _searchButton = new();
    readonly ContextMenuStrip _resultMenu = new();
    readonly ToolStripMenuItem _rmShow = new() { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
    readonly ToolStripMenuItem _rmOpen = new();
    readonly ToolStripMenuItem _rmReveal = new();
    readonly ToolStripMenuItem _rmCopy = new() { ShortcutKeyDisplayString = "Ctrl+C" };
    readonly ToolStripMenuItem _rmAi = new();
    readonly ToolStripMenuItem _rmRecycle = new() { ShortcutKeyDisplayString = "Del" };
    readonly ToolStripMenuItem _rmDelete = new() { ShortcutKeyDisplayString = "Shift+Del" };

    void BuildSearch()
    {
        _split.Panel1.Controls.Add(_tree);
        _searchTab.Controls.Add(_search);
        _cleanupTab.Controls.Add(_cleanup);
        _bottomTabs.TabPages.AddRange([_searchTab, _cleanupTab]);
        _bottomTabs.SelectedIndexChanged += (_, _) => OnBottomTabChanged();
        _split.Panel2.Controls.Add(_bottomTabs);
        _searchButton.Click += (_, _) => ToggleBottom(_searchTab);
        _search.CurrentFolder = () => _rows?.ViewRoot ?? NodeStore.None;
        _search.RevealRequested += RevealInTree;
        _search.DeleteRequested += permanent => DeleteNodes(_search.SelectedNodes(), permanent);

        _resultMenu.Items.AddRange([_rmShow, _rmOpen, _rmReveal, _rmCopy, _rmAi, new ToolStripSeparator(), _rmRecycle, _rmDelete]);
        _rmAi.Click += (_, _) => CopyAiPrompt(_search.FocusedNode);
        _rmShow.Click += (_, _) => RevealInTree(_search.FocusedNode);
        _rmOpen.Click += (_, _) => OpenNode(_search.FocusedNode);
        _rmReveal.Click += (_, _) => RevealNode(_search.FocusedNode);
        _rmCopy.Click += (_, _) => CopyNodePaths(_search.SelectedNodes());
        _rmRecycle.Click += (_, _) => DeleteNodes(_search.SelectedNodes(), permanent: false);
        _rmDelete.Click += (_, _) => DeleteNodes(_search.SelectedNodes(), permanent: true);
        _resultMenu.Opening += (_, e) =>
        {
            var nodes = _search.SelectedNodes();
            if (nodes.Count == 0) e.Cancel = true;
            _rmShow.Enabled = _rmOpen.Enabled = _rmAi.Enabled = nodes.Count == 1;
            _rmRecycle.Enabled = _rmDelete.Enabled = !IsBusy;
        };
        _search.ResultMenu = _resultMenu;
    }

    void ApplySearchTexts()
    {
        _searchButton.Text = "🔍 " + L.T("SearchButton");
        _searchButton.ToolTipText = L.T("SearchButtonTip");
        _searchTab.Text = L.T("SearchButton");
        ApplyCleanupTexts();
        _rmShow.Text = L.T("ShowInTree");
        _rmOpen.Text = L.T("Open");
        _rmReveal.Text = L.T("Reveal");
        _rmCopy.Text = L.T("CopyPath");
        _rmAi.Text = L.T("CopyAiPrompt");
        _rmRecycle.Text = L.T("Recycle");
        _rmDelete.Text = L.T("DeletePermanent");
        _search.ApplyTexts();
    }

    bool BottomVisible(TabPage tab) => !_split.Panel2Collapsed && _bottomTabs.SelectedTab == tab;

    /// <summary>The toolbar buttons open their panel, or close it when it is already showing.</summary>
    void ToggleBottom(TabPage tab) => ShowBottom(BottomVisible(tab) ? null : tab);

    void ShowBottom(TabPage? tab)
    {
        bool wasCollapsed = _split.Panel2Collapsed;
        _split.Panel2Collapsed = tab == null;
        if (tab != null)
        {
            if (wasCollapsed) _split.SplitterDistance = _split.Height * 55 / 100;
            if (_bottomTabs.SelectedTab != tab) _bottomTabs.SelectedTab = tab; // OnBottomTabChanged fills it
            else if (wasCollapsed) OnBottomTabChanged();
        }
        _searchButton.Checked = BottomVisible(_searchTab);
        _cleanupButton.Checked = BottomVisible(_cleanupTab);
    }

    void OnBottomTabChanged()
    {
        _searchButton.Checked = BottomVisible(_searchTab);
        _cleanupButton.Checked = BottomVisible(_cleanupTab);
        if (_split.Panel2Collapsed) return;
        if (_bottomTabs.SelectedTab == _searchTab) _search.FocusName();
        RefreshBottomPanel();
    }

    /// <summary>Re-runs whichever bottom panel is showing, e.g. after a new tree was loaded.</summary>
    void RefreshBottomPanel()
    {
        if (_split.Panel2Collapsed) return;
        var tab = _bottomTabs.SelectedTab;
        if (tab == _searchTab) _search.RunSearch();
        else if (tab == _cleanupTab) _cleanup.FindLocations();
        else RefreshAnalysisTab(tab);
    }

    internal SearchPanel SearchPanelForTest => _search;
    internal CleanupPanel CleanupPanelForTest => _cleanup;
    internal void ShowSearchForTest() => ShowBottom(_searchTab);
    internal void ShowCleanupForTest() => ShowBottom(_cleanupTab);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F))
        {
            if (!BottomVisible(_searchTab)) ShowBottom(_searchTab);
            else _search.FocusName();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void RevealInTree(int node)
    {
        if (_rows == null || node == NodeStore.None || !_rows.Store.IsLive(node)) return;
        _tree.SelectNode(node);
        UpdateBreadcrumb();
        _tree.Focus();
    }

    /// <summary>Keeps the search results in step with the tree after it changed.</summary>
    void RefreshSearchAfterChange()
    {
        if (BottomVisible(_searchTab)) _search.RemoveDead();
        else if (BottomVisible(_cleanupTab)) _cleanup.RemoveDead();
        else RefreshAnalysisAfterChange();
    }
}
