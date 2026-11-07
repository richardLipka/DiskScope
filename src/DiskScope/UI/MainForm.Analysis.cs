using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>Analysis panels below the tree (abandoned data, duplicates, treemap), opened from the Analyses menu.</summary>
sealed partial class MainForm
{
    readonly ToolStripDropDownButton _analysisMenu = new();
    readonly ToolStripMenuItem _orphanItem = new();
    readonly TabPage _orphanTab = new();
    readonly OrphanPanel _orphans = new() { Dock = DockStyle.Fill };

    void BuildAnalysis()
    {
        _analysisMenu.DropDownItems.Add(_orphanItem);
        _orphanItem.Click += (_, _) => ShowBottom(_orphanTab);
        _orphanTab.Controls.Add(_orphans);
        _bottomTabs.TabPages.Add(_orphanTab);
        _orphans.Catalog = () => _cleanup.Catalog;
        _orphans.RevealRequested += RevealInTree;
        _orphans.ResultMenu = BuildNodeListMenu(() => _orphans.SelectedNodes(), () => _orphans.FocusedNode);
    }

    void ApplyAnalysisTexts()
    {
        _analysisMenu.Text = "📊 " + L.T("Analyses");
        _orphanItem.Text = L.T("OrphanTab");
        _orphanTab.Text = L.T("OrphanTab");
        _orphans.ApplyTexts();
    }

    void SetAnalysisStore(NodeStore store)
    {
        _orphans.SetStore(store);
    }

    void RefreshAnalysisTab(TabPage? tab)
    {
        if (tab == _orphanTab) _orphans.Run();
    }

    void RefreshAnalysisAfterChange()
    {
        if (BottomVisible(_orphanTab)) _orphans.RemoveDead();
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

    internal OrphanPanel OrphanPanelForTest => _orphans;
    internal void ShowOrphansForTest() => ShowBottom(_orphanTab);
}
