using System.ComponentModel;
using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>Search criteria bar and a virtual list of the largest matching items.</summary>
sealed class SearchPanel : UserControl
{
    enum ResultCol
    {
        Name,
        OnDisk,
        Size,
        Modified,
        Accessed,
        Folder,
    }

    static readonly long[] MinSizes = [0, 1L << 20, 10L << 20, 100L << 20, 1L << 30, 10L << 30];

    /// <summary>Age filters: unchanged or unopened for at least the given number of days.</summary>
    static readonly (int Days, bool Access, string Key)[] Ages =
    [
        (0, false, "AnyAge"),
        (30, false, "Older30d"), (182, false, "Older6m"), (365, false, "Older1y"), (3 * 365, false, "Older3y"),
        (30, true, "Unopened30d"), (182, true, "Unopened6m"), (365, true, "Unopened1y"), (3 * 365, true, "Unopened3y"),
    ];

    readonly TextBox _name = new() { Width = 260 };
    readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    readonly ComboBox _minSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly ComboBox _age = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly CheckBox _inView = new() { AutoSize = true };
    readonly Button _go = new() { AutoSize = true };
    readonly Label _nameLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, FullRowSelect = true, HideSelection = false,
        MultiSelect = true, BorderStyle = BorderStyle.None,
    };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 2) };

    NodeStore? _store;
    List<int> _results = [];
    CancellationTokenSource? _cancel;

    /// <summary>The node whose subtree "only in the current folder" restricts the search to.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int>? CurrentFolder { get; set; }

    /// <summary>Double-click or Enter on a result.</summary>
    public event Action<int>? RevealRequested;

    public event Action<bool>? DeleteRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? ResultMenu
    {
        get => _list.ContextMenuStrip;
        set => _list.ContextMenuStrip = value;
    }

    public SearchPanel()
    {
        _bar.Controls.AddRange([_nameLabel, _name, _kind, _minSize, _age, _inView, _go, _summary]);
        foreach (Control c in _bar.Controls) c.Margin = new Padding(3, 4, 3, 2);
        Controls.Add(_list);
        Controls.Add(_bar);

        _go.Click += (_, _) => RunSearch();
        _name.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            RunSearch();
        };
        foreach (var combo in new[] { _kind, _minSize, _age }) combo.SelectionChangeCommitted += (_, _) => RunSearch();
        _inView.CheckedChanged += (_, _) => RunSearch();

        foreach (ResultCol c in Enum.GetValues<ResultCol>())
            _list.Columns.Add("", c switch { ResultCol.Name => 300, ResultCol.Folder => 520, ResultCol.Modified or ResultCol.Accessed => 135, _ => 95 },
                c is ResultCol.OnDisk or ResultCol.Size ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        _list.RetrieveVirtualItem += (_, e) => e.Item = MakeItem(e.ItemIndex);
        _list.DoubleClick += (_, _) => RevealFocused();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) RevealFocused();
            else if (e.KeyCode == Keys.Delete) DeleteRequested?.Invoke(e.Shift);
            else if (e.KeyCode == Keys.C && e.Control) Clipboard.SetText(string.Join(Environment.NewLine, SelectedNodes().Select(_store!.GetPath)));
            else return;
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        ApplyTexts();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (ColumnHeader c in _list.Columns) c.Width = LogicalToDeviceUnits(c.Width);
        foreach (Control c in new Control[] { _name, _kind, _minSize, _age }) c.Width = LogicalToDeviceUnits(c.Width);
    }

    public void ApplyTexts()
    {
        _nameLabel.Text = L.T("SearchName");
        _name.PlaceholderText = L.T("SearchHint");
        Refill(_kind, [L.T("KindFiles"), L.T("KindFoldersOwn"), L.T("KindFolders"), L.T("KindAll")]);
        Refill(_minSize, MinSizes.Select(s => s == 0 ? L.T("AnySize") : "≥ " + Format.Size(s, L.Culture)).ToArray());
        Refill(_age, Ages.Select(a => L.T(a.Key)).ToArray());
        _inView.Text = L.T("OnlyInView");
        _go.Text = L.T("Find");
        string[] cols = ["ColName", "ColOnDisk", "ColSize", "ColModified", "ColAccessed", "ColFolder"];
        for (int i = 0; i < cols.Length; i++) _list.Columns[i].Text = L.T(cols[i]);
        _list.Invalidate();
    }

    static void Refill(ComboBox combo, string[] items)
    {
        int selected = Math.Max(0, combo.SelectedIndex);
        combo.Items.Clear();
        combo.Items.AddRange(items);
        combo.SelectedIndex = Math.Min(selected, items.Length - 1);
    }

    public void FocusName()
    {
        _name.Focus();
        _name.SelectAll();
    }

    public void SetStore(NodeStore? store)
    {
        _cancel?.Cancel();
        _store = store;
        _results = [];
        _list.VirtualListSize = 0;
        _summary.Text = "";
    }

    public List<int> SelectedNodes()
    {
        var list = new List<int>();
        foreach (int i in _list.SelectedIndices)
            if (i < _results.Count) list.Add(_results[i]);
        return list;
    }

    public int FocusedNode => _list.FocusedItem is { } item && item.Index < _results.Count ? _results[item.Index] : SelectedNodes().FirstOrDefault(NodeStore.None);

    SearchQuery BuildQuery()
    {
        var age = Ages[Math.Max(0, _age.SelectedIndex)];
        DateTime? before = age.Days > 0 ? DateTime.UtcNow.AddDays(-age.Days) : null;
        return new SearchQuery
        {
            Name = _name.Text,
            Kind = (SearchKind)Math.Max(0, _kind.SelectedIndex),
            MinSize = MinSizes[Math.Max(0, _minSize.SelectedIndex)],
            ModifiedBefore = age.Access ? null : before,
            AccessedBefore = age.Access ? before : null,
            Scope = _inView.Checked && CurrentFolder != null ? CurrentFolder() : NodeStore.None,
        };
    }

    public async void RunSearch()
    {
        if (_store == null || _store.Root == NodeStore.None) return;
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        var store = _store;
        var query = BuildQuery();
        _summary.Text = L.T("Searching");
        try
        {
            var result = await Task.Run(() => Search.Run(store, query, cancel.Token));
            if (IsDisposed || cancel.IsCancellationRequested || store != _store) return;
            _results = result.Nodes;
            _list.SelectedIndices.Clear();
            _list.VirtualListSize = _results.Count;
            _list.Invalidate();
            _summary.Text = L.T("SearchSummary", Format.Count(result.TotalMatches, L.Culture), Format.Size(result.TotalSize, L.Culture),
                Format.Count(_results.Count, L.Culture), (int)result.Elapsed.TotalMilliseconds);
            SearchCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Raised when results are shown; used by the test hooks.</summary>
    public event Action? SearchCompleted;

    /// <summary>Drops results that are no longer in the tree (deleted, rescanned).</summary>
    public void RemoveDead()
    {
        if (_store == null) return;
        int before = _results.Count;
        _results = _results.Where(_store.IsLive).ToList();
        if (_results.Count != before)
        {
            _list.SelectedIndices.Clear();
            _list.VirtualListSize = _results.Count;
        }
        _list.Invalidate();
    }

    void RevealFocused()
    {
        int node = FocusedNode;
        if (node != NodeStore.None) RevealRequested?.Invoke(node);
    }

    ListViewItem MakeItem(int index)
    {
        if (_store == null || index >= _results.Count) return new ListViewItem(["", "", "", "", "", ""]);
        int node = _results[index];
        ref var n = ref _store[node];
        var kind = (SearchKind)Math.Max(0, _kind.SelectedIndex);
        long onDisk = Search.RankSize(_store, node, kind);
        int parent = n.Parent;
        return new ListViewItem([
            _store.GetName(node),
            Format.Size(onDisk, L.Culture),
            Format.Size(n.Length, L.Culture),
            TreeListView.Date(n.MTime),
            TreeListView.Date(n.ATime),
            parent == NodeStore.None ? "" : _store.GetPath(parent),
        ]);
    }
}
