using System.ComponentModel;
using DiskScope.Core;
using DiskScope.Core.Cleanup;
using DiskScope.Core.Orphans;

namespace DiskScope.UI;

/// <summary>Folders of programs that are no longer installed, grouped by how sure the guess is.</summary>
sealed class OrphanPanel : UserControl
{
    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 2) };
    readonly Button _refresh = new() { AutoSize = true };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
        BorderStyle = BorderStyle.None, ShowGroups = true,
    };
    readonly TextBox _advice = new()
    {
        Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Info, ForeColor = SystemColors.InfoText,
    };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 2) };

    NodeStore? _store;
    List<InstalledProgram>? _programs;
    HashSet<string>? _families;
    bool _searched;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<CleanupCatalog>? Catalog { get; set; }

    public event Action<int>? RevealRequested;

    /// <summary>Raised when the list has been filled; used by the test hooks.</summary>
    public event Action? Found;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? ResultMenu
    {
        get => _list.ContextMenuStrip;
        set => _list.ContextMenuStrip = value;
    }

    public OrphanPanel()
    {
        _bar.Controls.AddRange([_summary, _refresh]);
        Controls.Add(_list);
        Controls.Add(_advice);
        Controls.Add(_bar);
        foreach (var w in new[] { 300, 95, 120, 140, 560 }) _list.Columns.Add("", w);
        _list.Columns[1].TextAlign = HorizontalAlignment.Right;
        _list.SelectedIndexChanged += (_, _) => ShowAdvice();
        _list.DoubleClick += (_, _) =>
        {
            if (FocusedNode is int n && n != NodeStore.None) RevealRequested?.Invoke(n);
        };
        _refresh.Click += (_, _) =>
        {
            _programs = null; // re-read the installed programs too
            Run();
        };
        ApplyTexts();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (ColumnHeader c in _list.Columns) c.Width = LogicalToDeviceUnits(c.Width);
        _advice.Height = LogicalToDeviceUnits(70);
    }

    public void ApplyTexts()
    {
        _refresh.Text = L.T("CleanupRefresh");
        string[] cols = ["OrphanColFolder", "ColOnDisk", "OrphanColKind", "OrphanColChanged", "ColLocation"];
        for (int i = 0; i < cols.Length; i++) _list.Columns[i].Text = L.T(cols[i]);
        if (_searched) Run();
    }

    public void SetStore(NodeStore? store)
    {
        _store = store;
        _searched = false;
        _list.Items.Clear();
        _summary.Text = "";
        _advice.Text = "";
    }

    public int FocusedNode => (_list.FocusedItem?.Tag as OrphanCandidate)?.Node ?? SelectedNodes().FirstOrDefault(NodeStore.None);

    public List<int> SelectedNodes() => _list.SelectedItems.Cast<ListViewItem>().Select(i => ((OrphanCandidate)i.Tag!).Node).ToList();

    public async void Run()
    {
        if (_store == null || _store.Root == NodeStore.None) return;
        var store = _store;
        var catalog = Catalog?.Invoke() ?? CleanupCatalog.Default();
        _summary.Text = L.T("Searching");
        var (candidates, programs, families) = await Task.Run(() =>
        {
            var programs = _programs ?? InstalledPrograms.Read();
            var families = _families ?? InstalledPrograms.ReadStoreFamilies();
            var known = CleanupFinder.Find(store, catalog).Select(m => m.Node);
            return (new OrphanFinder().Find(store, programs, families, known, DateTime.UtcNow), programs, families);
        });
        if (IsDisposed || store != _store) return;
        _programs = programs;
        _families = families;
        _searched = true;

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();
        foreach (OrphanConfidence confidence in Enum.GetValues<OrphanConfidence>())
        {
            var inGroup = candidates.Where(c => c.Confidence == confidence).ToList();
            if (inGroup.Count == 0) continue;
            var group = new ListViewGroup(L.T("Orphan" + confidence, Format.Size(inGroup.Sum(c => store[c.Node].Size), L.Culture)));
            _list.Groups.Add(group);
            foreach (var c in inGroup)
                _list.Items.Add(new ListViewItem([
                    store.GetName(c.Node),
                    Format.Size(store[c.Node].Size, L.Culture),
                    L.T("OrphanKind" + c.Kind),
                    TreeListView.Date(store[c.Node].MTime),
                    store.GetPath(c.Node),
                ], group) { Tag = c });
        }
        _list.EndUpdate();
        _summary.Text = L.T("OrphanSummary", candidates.Count, Format.Size(candidates.Sum(c => store[c.Node].Size), L.Culture),
            programs.Count, families.Count);
        _advice.Text = L.T("OrphanHint");
        Found?.Invoke();
    }

    public void RemoveDead()
    {
        if (_searched) Run();
    }

    void ShowAdvice()
    {
        if (_list.FocusedItem?.Tag is not OrphanCandidate c) return;
        string age = c.DaysSinceChange == int.MaxValue ? "?" : c.DaysSinceChange.ToString(L.Culture);
        _advice.Text = L.T("OrphanWhy" + c.Kind, age) + Environment.NewLine +
                       L.T(c.Confidence == OrphanConfidence.Likely ? "OrphanAdviceLikely" : "OrphanAdvicePossible");
    }
}
