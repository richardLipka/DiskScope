using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>The search panel below the tree and its result actions.</summary>
sealed partial class MainForm
{
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Panel2Collapsed = true };
    readonly SearchPanel _search = new() { Dock = DockStyle.Fill };
    readonly ToolStripButton _searchButton = new() { CheckOnClick = true };
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
        _split.Panel2.Controls.Add(_search);
        _searchButton.CheckedChanged += (_, _) => ShowSearch(_searchButton.Checked);
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
        _rmShow.Text = L.T("ShowInTree");
        _rmOpen.Text = L.T("Open");
        _rmReveal.Text = L.T("Reveal");
        _rmCopy.Text = L.T("CopyPath");
        _rmAi.Text = L.T("CopyAiPrompt");
        _rmRecycle.Text = L.T("Recycle");
        _rmDelete.Text = L.T("DeletePermanent");
        _search.ApplyTexts();
    }

    void ShowSearch(bool show)
    {
        if (_searchButton.Checked != show)
        {
            _searchButton.Checked = show; // raises CheckedChanged, which comes back here
            return;
        }
        if (show == !_split.Panel2Collapsed) return;
        _split.Panel2Collapsed = !show;
        if (!show) return;
        _split.SplitterDistance = _split.Height * 55 / 100;
        _search.FocusName();
        _search.RunSearch();
    }

    internal SearchPanel SearchPanelForTest => _search;
    internal void ShowSearchForTest() => ShowSearch(true);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F))
        {
            if (_split.Panel2Collapsed) ShowSearch(true);
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
        if (!_split.Panel2Collapsed) _search.RemoveDead();
    }
}
