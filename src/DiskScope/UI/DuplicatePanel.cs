using System.ComponentModel;
using DiskScope.Core;
using DiskScope.Core.Duplicates;

namespace DiskScope.UI;

/// <summary>Duplicate files, searched only when asked (it reads file contents and can take long).</summary>
sealed class DuplicatePanel : UserControl
{
    const int MaxGroupsShown = 2000;
    static readonly long[] MinSizes = [1L << 20, 10L << 20, 100L << 20, 1L << 30];

    readonly ComboBox _minSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly CheckBox _inView = new() { AutoSize = true };
    readonly Button _start = new() { AutoSize = true };
    readonly Button _markCopies = new() { AutoSize = true, Enabled = false };
    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 2) };
    readonly ProgressBar _progressBar = new() { Width = 220, Visible = false };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
        BorderStyle = BorderStyle.None, ShowGroups = true,
    };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 2) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 300 };

    NodeStore? _store;
    CancellationTokenSource? _cancel;
    DuplicateProgress? _progress;
    List<DuplicateGroup> _groups = [];

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int>? CurrentFolder { get; set; }

    public event Action<int>? RevealRequested;

    /// <summary>Raised when a search ends; used by the test hooks.</summary>
    public event Action? Finished;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? ResultMenu
    {
        get => _list.ContextMenuStrip;
        set => _list.ContextMenuStrip = value;
    }

    public DuplicatePanel()
    {
        _bar.Controls.AddRange([_minSize, _inView, _start, _markCopies, _progressBar, _summary]);
        foreach (Control c in _bar.Controls) c.Margin = new Padding(3, 4, 3, 2);
        Controls.Add(_list);
        Controls.Add(_bar);
        foreach (var w in new[] { 300, 95, 140, 640 }) _list.Columns.Add("", w);
        _list.Columns[1].TextAlign = HorizontalAlignment.Right;
        _list.DoubleClick += (_, _) =>
        {
            if (FocusedNode is int n && n != NodeStore.None) RevealRequested?.Invoke(n);
        };
        _start.Click += (_, _) =>
        {
            if (_cancel != null) _cancel.Cancel();
            else Start();
        };
        _markCopies.Click += (_, _) => MarkCopies();
        _timer.Tick += (_, _) => ShowProgress();
        _minSize.SelectedIndex = -1;
        ApplyTexts();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (ColumnHeader c in _list.Columns) c.Width = LogicalToDeviceUnits(c.Width);
        _minSize.Width = LogicalToDeviceUnits(_minSize.Width);
        _progressBar.Width = LogicalToDeviceUnits(_progressBar.Width);
    }

    public void ApplyTexts()
    {
        int selected = Math.Max(0, _minSize.SelectedIndex);
        _minSize.Items.Clear();
        _minSize.Items.AddRange(MinSizes.Select(s => "≥ " + Format.Size(s, L.Culture)).ToArray<object>());
        _minSize.SelectedIndex = selected;
        _inView.Text = L.T("OnlyInView");
        _start.Text = L.T(_cancel != null ? "Stop" : "DupStart");
        _markCopies.Text = L.T("DupMarkCopies");
        string[] cols = ["ColName", "ColOnDisk", "ColModified", "ColFolder"];
        for (int i = 0; i < cols.Length; i++) _list.Columns[i].Text = L.T(cols[i]);
        if (_cancel == null && _store != null && _groups.Count > 0) Fill();
        else if (_cancel == null && _groups.Count == 0) _summary.Text = L.T("DupHint");
    }

    public void SetStore(NodeStore? store)
    {
        _cancel?.Cancel();
        _store = store;
        _groups = [];
        _list.Items.Clear();
        _list.Groups.Clear();
        _markCopies.Enabled = false;
        _summary.Text = L.T("DupHint");
    }

    public int FocusedNode => _list.FocusedItem?.Tag is int n ? n : SelectedNodes().FirstOrDefault(NodeStore.None);

    public List<int> SelectedNodes() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (int)i.Tag!).ToList();

    /// <summary>Starts the search; public for the test hooks.</summary>
    public async void Start()
    {
        if (_store == null || _store.Root == NodeStore.None || _cancel != null) return;
        var store = _store;
        var finder = new DuplicateFinder
        {
            MinSize = MinSizes[Math.Max(0, _minSize.SelectedIndex)],
            Scope = _inView.Checked && CurrentFolder != null ? CurrentFolder() : NodeStore.None,
        };
        var cancel = _cancel = new CancellationTokenSource();
        _progress = new DuplicateProgress();
        var progress = _progress;
        _start.Text = L.T("Stop");
        _progressBar.Visible = true;
        _markCopies.Enabled = false;
        _list.Items.Clear();
        _list.Groups.Clear();
        _timer.Start();
        try
        {
            _groups = await Task.Run(() => finder.Find(store, progress, cancel.Token));
            if (store == _store) Fill();
        }
        catch (OperationCanceledException)
        {
            _summary.Text = L.T("DupCancelled");
        }
        finally
        {
            _timer.Stop();
            _cancel = null;
            _progressBar.Visible = false;
            _start.Text = L.T("DupStart");
            Finished?.Invoke();
        }
    }

    void ShowProgress()
    {
        if (_progress is not DuplicateProgress p) return;
        switch (p.Stage)
        {
            case DuplicateStage.Collecting:
                _summary.Text = L.T("DupCollecting");
                _progressBar.Style = ProgressBarStyle.Marquee;
                break;
            case DuplicateStage.Sampling:
                _summary.Text = L.T("DupSampling", Format.Count(p.FilesDone, L.Culture), Format.Count(p.FilesTotal, L.Culture));
                SetBar(p.FilesDone, p.FilesTotal);
                break;
            case DuplicateStage.Hashing:
                _summary.Text = L.T("DupHashing", Format.Count(p.FilesDone, L.Culture), Format.Count(p.FilesTotal, L.Culture),
                    Format.Size(p.BytesDone, L.Culture), Format.Size(p.BytesTotal, L.Culture));
                SetBar(p.BytesDone, p.BytesTotal);
                break;
        }
    }

    void SetBar(long done, long total)
    {
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
    }

    void Fill()
    {
        var store = _store!;
        _groups = _groups.Where(g => g.Nodes.Count(store.IsLive) > 1).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();
        foreach (var g in _groups.Take(MaxGroupsShown))
        {
            var live = g.Nodes.Where(store.IsLive).OrderBy(n => store[n].MTime).ToList();
            var group = new ListViewGroup(L.T("DupGroup", live.Count, Format.Size(g.Size, L.Culture), Format.Size(g.Size * (live.Count - 1), L.Culture)));
            _list.Groups.Add(group);
            foreach (int n in live)
            {
                int parent = store[n].Parent;
                _list.Items.Add(new ListViewItem([
                    store.GetName(n), Format.Size(store[n].Size, L.Culture), TreeListView.Date(store[n].MTime),
                    parent == NodeStore.None ? "" : store.GetPath(parent),
                ], group) { Tag = n });
            }
        }
        _list.EndUpdate();
        long wasted = _groups.Sum(g => g.Size * (g.Nodes.Count(store.IsLive) - 1));
        _summary.Text = _groups.Count == 0 ? L.T("DupNone") : L.T("DupSummary", Format.Count(_groups.Count, L.Culture), Format.Size(wasted, L.Culture));
        _markCopies.Enabled = _groups.Count > 0;
    }

    /// <summary>Selects every file except the oldest of each group, ready to be deleted.</summary>
    void MarkCopies()
    {
        _list.SelectedItems.Clear();
        foreach (ListViewGroup g in _list.Groups)
            foreach (ListViewItem item in g.Items.Cast<ListViewItem>().Skip(1)) item.Selected = true;
        _list.Focus();
    }

    public void RemoveDead()
    {
        if (_cancel == null && _store != null && _groups.Count > 0) Fill();
    }
}
