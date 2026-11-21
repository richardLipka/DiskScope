using DiskScope.Core;
using DiskScope.Core.Scanning;
using DiskScope.Core.Storage;

namespace DiskScope.UI;

/// <summary>The drive overview: the start page, and a toolbar button to return to it.</summary>
sealed partial class MainForm
{
    readonly DriveOverviewPanel _overview = new() { Dock = DockStyle.Fill, Visible = false };
    readonly ToolStripButton _drivesButton = new();
    readonly DriveUsageLog _usageLog = new(Path.Combine(AppPaths.DataDir, "drives.json"));

    void BuildOverview()
    {
        _overview.Catalog = _catalog;
        _overview.UsageLog = _usageLog;
        _split.Panel1.Controls.Add(_overview);
        _overview.OpenRequested += info =>
        {
            if (IsBusy) return;
            ShowOverview(false);
            SelectTarget(info.RootPath);
            LoadSnapshot(info);
        };
        _overview.ScanRequested += root =>
        {
            if (IsBusy) return;
            ShowOverview(false);
            SelectTarget(root);
            StartScan();
        };
        _drivesButton.Click += (_, _) => ShowOverview(!_overview.Visible);
    }

    void ApplyOverviewTexts()
    {
        _drivesButton.Text = "🏠 " + L.T("OverviewButton");
        _drivesButton.ToolTipText = L.T("OverviewTip");
        _overview.ApplyTexts();
    }

    /// <summary>Shows or hides the overview; it stays when there is no tree to go back to, unless forced.</summary>
    void ShowOverview(bool show, bool force = false)
    {
        if (!show && !force && _rows == null) return;
        _overview.Visible = show;
        _drivesButton.Checked = show;
        if (show)
        {
            _overview.BringToFront();
            _overview.Fill(SelectedTarget);
            _overview.FocusList();
        }
    }

    /// <summary>Remembers how full a drive was when its scan finished, for the overview's "change since" column.</summary>
    void RecordDriveUsage(ScanSession session)
    {
        if (session.State != ScanState.Completed || session.RootPath.TrimEnd('\\').Length != 2) return;
        try
        {
            var drive = new DriveInfo(session.RootPath);
            _usageLog.Record(session.RootPath, drive.TotalSize - drive.TotalFreeSpace, drive.TotalSize, session.StartedUtc, force: true);
            _usageLog.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    internal void ShowOverviewForTest() => ShowOverview(true);
    internal bool OverviewVisibleForTest => _overview.Visible;
}
