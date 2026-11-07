using DiskScope.Core;
using DiskScope.Core.Cleanup;

namespace DiskScope.UI;

/// <summary>The cleanup panel: known locations from the catalog and actions on them.</summary>
sealed partial class MainForm
{
    readonly CleanupPanel _cleanup = new() { Dock = DockStyle.Fill };
    readonly ToolStripButton _cleanupButton = new();
    readonly ContextMenuStrip _cleanupMenu = new();
    readonly ToolStripMenuItem _cmShow = new() { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
    readonly ToolStripMenuItem _cmReveal = new();
    readonly ToolStripMenuItem _cmCopy = new();
    readonly ToolStripMenuItem _cmAi = new();
    readonly ToolStripMenuItem _cmClean = new();
    readonly ToolStripMenuItem _cmCleanPermanent = new();

    void BuildCleanup()
    {
        _cleanup.UserCatalogPath = Path.Combine(AppPaths.DataDir, CleanupCatalog.FileName);
        _cleanup.LoadCatalog();
        _cleanup.RevealRequested += RevealInTree;
        _cleanupButton.Click += (_, _) => ToggleBottom(_cleanupTab);

        _cleanupMenu.Items.AddRange([_cmShow, _cmReveal, _cmCopy, _cmAi, new ToolStripSeparator(), _cmClean, _cmCleanPermanent]);
        _cmShow.Click += (_, _) => { if (_cleanup.FocusedMatch is CleanupMatch m) RevealInTree(m.Node); };
        _cmReveal.Click += (_, _) => { if (_cleanup.FocusedMatch is CleanupMatch m) RevealNode(m.Node); };
        _cmCopy.Click += (_, _) => CopyNodePaths(_cleanup.SelectedMatches().Select(m => m.Node).ToList());
        _cmAi.Click += (_, _) => { if (_cleanup.FocusedMatch is CleanupMatch m) CopyAiPrompt(m.Node); };
        _cmClean.Click += (_, _) => CleanSelected(permanent: false);
        _cmCleanPermanent.Click += (_, _) => CleanSelected(permanent: true);
        _cleanupMenu.Opening += (_, e) =>
        {
            var matches = _cleanup.SelectedMatches();
            if (matches.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            bool single = matches.Count == 1;
            _cmShow.Enabled = _cmReveal.Enabled = _cmAi.Enabled = single;
            bool cleanable = !IsBusy && matches.All(m => m.Entry.Action != CleanupAction.None && m.Entry.Safety != CleanupSafety.System);
            _cmClean.Enabled = _cmCleanPermanent.Enabled = cleanable;
            bool contents = matches.All(m => m.Entry.Action == CleanupAction.DeleteContents);
            _cmClean.Text = L.T(contents ? "CleanContents" : "Recycle");
            _cmCleanPermanent.Text = L.T(contents ? "CleanContentsPermanent" : "DeletePermanent");
        };
        _cleanup.ResultMenu = _cleanupMenu;
    }

    void ApplyCleanupTexts()
    {
        _cleanupButton.Text = "🧹 " + L.T("CleanupButton");
        _cleanupButton.ToolTipText = L.T("CleanupButtonTip");
        _cleanupTab.Text = L.T("CleanupButton");
        _cmShow.Text = L.T("ShowInTree");
        _cmReveal.Text = L.T("Reveal");
        _cmCopy.Text = L.T("CopyPath");
        _cmAi.Text = L.T("CopyAiPrompt");
        _cleanup.ApplyTexts();
    }

    /// <summary>Deletes the selected locations: a folder's contents or the item itself, as the catalog says.</summary>
    void CleanSelected(bool permanent)
    {
        if (_rows == null) return;
        var store = _rows.Store;
        var nodes = new List<int>();
        foreach (var m in _cleanup.SelectedMatches())
        {
            if (m.Entry.Action == CleanupAction.None || m.Entry.Safety == CleanupSafety.System) continue;
            if (m.Entry.Action == CleanupAction.Delete) nodes.Add(m.Node);
            else
                for (int c = store[m.Node].FirstChild; c != NodeStore.None; c = store[c].NextSibling)
                    nodes.Add(c);
        }
        if (nodes.Count > 0) DeleteNodes(nodes, permanent);
    }
}
