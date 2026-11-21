using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>The tree filter: by name patterns and by minimum size, on the right of the breadcrumb bar.</summary>
sealed partial class MainForm
{
    static readonly long[] FilterSizes = [0, 1L << 20, 10L << 20, 100L << 20, 1L << 30, 10L << 30];

    readonly ToolStripLabel _filterLabel = new() { Alignment = ToolStripItemAlignment.Right };
    readonly ToolStripTextBox _filterBox = new() { Alignment = ToolStripItemAlignment.Right, AutoSize = false };
    readonly ToolStripComboBox _filterSize = new() { Alignment = ToolStripItemAlignment.Right, DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false };
    readonly ToolStripButton _filterClear = new("✕") { Alignment = ToolStripItemAlignment.Right, Visible = false };
    readonly System.Windows.Forms.Timer _filterTimer = new() { Interval = 450 };
    readonly List<ToolStripItem> _crumbs = [];

    /// <summary>The active view filter, or null; combined with the comparison filter in <see cref="UpdateRowFilter"/>.</summary>
    TreeFilter? _viewFilter;
    Func<int, bool>? _compareRowFilter;
    CancellationTokenSource? _filterCancel;
    int _filterVersion = -1;
    DateTime _filterPrepared;

    void BuildFilter()
    {
        _filterBox.Width = LogicalToDeviceUnits(260);
        _filterSize.Width = LogicalToDeviceUnits(120);
        // Right-aligned items are laid out from the right edge in collection order.
        _breadcrumb.Items.AddRange([_filterClear, _filterSize, _filterBox, _filterLabel]);
        _filterBox.TextChanged += (_, _) =>
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        };
        _filterBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) ClearFilter();
            else if (e.KeyCode == Keys.Enter)
            {
                _filterTimer.Stop();
                ApplyViewFilter();
                _tree.Focus();
            }
            else return;
            e.Handled = e.SuppressKeyPress = true;
        };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyViewFilter();
        };
        _filterSize.SelectedIndexChanged += (_, _) => ApplyViewFilter();
        _filterClear.Click += (_, _) => ClearFilter();
    }

    void ApplyFilterTexts()
    {
        _filterLabel.Text = L.T("FilterLabel");
        _filterBox.TextBox.PlaceholderText = L.T("FilterCue");
        _filterBox.ToolTipText = L.T("TreeFilterTip");
        _filterClear.ToolTipText = L.T("FilterClear");
        int selected = Math.Max(0, _filterSize.SelectedIndex);
        _filterSize.Items.Clear();
        _filterSize.Items.AddRange(FilterSizes.Select(s => s == 0 ? L.T("FilterAllSizes") : "≥ " + Format.Size(s, L.Culture)).ToArray<object>());
        _filterSize.SelectedIndex = selected;
        _filterSize.ToolTipText = L.T("FilterSizeTip");
    }

    void ClearFilter()
    {
        _filterBox.Text = "";
        _filterSize.SelectedIndex = 0;
        _filterTimer.Stop();
        ApplyViewFilter();
    }

    /// <summary>Builds the filter from the box and the size list; name patterns are prepared in the background.</summary>
    async void ApplyViewFilter()
    {
        _filterCancel?.Cancel();
        var filter = new TreeFilter(_filterBox.Text, FilterSizes[Math.Max(0, _filterSize.SelectedIndex)]);
        bool active = filter.IsActive;
        _filterClear.Visible = active;
        _filterBox.BackColor = active && filter.HasNames ? SystemColors.Info : SystemColors.Window;
        if (!active || _rows == null)
        {
            _viewFilter = active ? filter : null;
            UpdateRowFilter();
            return;
        }
        var store = _rows.Store;
        int version = store.Version;
        if (filter.HasNames)
        {
            var cancel = _filterCancel = new CancellationTokenSource();
            try
            {
                await Task.Run(() => filter.Prepare(store, cancel.Token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (IsDisposed || cancel.IsCancellationRequested || _rows?.Store != store) return;
        }
        _filterVersion = version;
        _filterPrepared = DateTime.UtcNow;
        _viewFilter = filter;
        UpdateRowFilter();
        if (filter.HasNames && filter.FoldersWithMatches == 0) _status.Text = L.T("FilterNoMatch");
    }

    /// <summary>
    /// While a scan or a deletion changes the tree, a name filter would miss new matches: it is prepared again,
    /// at most every few seconds. Called from the refresh timer.
    /// </summary>
    void RefreshViewFilterIfStale()
    {
        if (_viewFilter is not { HasNames: true } || _rows == null) return;
        if (_rows.Store.Version == _filterVersion || (DateTime.UtcNow - _filterPrepared).TotalSeconds < 3) return;
        ApplyViewFilter();
    }

    /// <summary>Applies the comparison filter and the view filter together.</summary>
    void UpdateRowFilter()
    {
        if (_rows == null) return;
        var store = _rows.Store;
        var compare = _compareRowFilter;
        var view = _viewFilter;
        _rows.Filter = (compare, view) switch
        {
            (null, null) => null,
            (_, null) => compare,
            (null, _) => n => view.Shows(store, n),
            _ => n => compare(n) && view.Shows(store, n),
        };
        _tree.Reload();
        UpdateDetail();
    }

    void FocusFilter()
    {
        _filterBox.Focus();
        _filterBox.SelectAll();
    }

    internal void SetFilterForTest(string names, int sizeIndex)
    {
        _filterBox.Text = names;
        _filterSize.SelectedIndex = sizeIndex;
        _filterTimer.Stop();
        ApplyViewFilter();
    }
}
