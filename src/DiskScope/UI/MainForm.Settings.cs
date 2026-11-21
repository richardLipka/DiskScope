using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>The settings menu: Explorer integration and the saved data window.</summary>
sealed partial class MainForm
{
    readonly ToolStripDropDownButton _settingsMenu = new() { Alignment = ToolStripItemAlignment.Right };
    readonly ToolStripMenuItem _explorerItem = new() { CheckOnClick = true };
    readonly ToolStripMenuItem _dataItem = new();
    readonly ToolStripMenuItem _themeItem = new();
    readonly ExplorerIntegration _explorer = new();

    void BuildSettings()
    {
        _settingsMenu.DropDownItems.AddRange([_explorerItem, _themeItem, new ToolStripSeparator(), _dataItem]);
        foreach (var mode in Enum.GetValues<ThemeMode>())
        {
            var item = new ToolStripMenuItem { Tag = mode };
            item.Click += (_, _) => SetTheme(mode);
            _themeItem.DropDownItems.Add(item);
        }
        _settingsMenu.DropDownOpening += (_, _) => _explorerItem.Checked = _explorer.IsRegistered;
        _explorerItem.Click += (_, _) => SetExplorerIntegration(_explorerItem.Checked);
        _dataItem.Click += (_, _) => ShowDataManager();

        // A portable exe may have moved since the entry was added: keep the entry pointing at this copy.
        try
        {
            if (_explorer.IsRegistered && !string.Equals(_explorer.RegisteredExecutable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                _explorer.Register(Environment.ProcessPath!, L.T("ExplorerVerb"));
        }
        catch (Exception)
        {
            // Not being able to refresh the entry must not stop the app from starting.
        }
    }

    void ApplySettingsTexts()
    {
        _settingsMenu.Text = "⚙";
        _settingsMenu.ToolTipText = L.T("Settings");
        _explorerItem.Text = L.T("ExplorerItem");
        _dataItem.Text = L.T("ManageData");
        _themeItem.Text = L.T("ThemeMenu");
        foreach (ToolStripMenuItem item in _themeItem.DropDownItems)
        {
            var mode = (ThemeMode)item.Tag!;
            item.Text = L.T("Theme" + mode);
            item.Checked = mode == _settings.Theme;
        }
        // The menu text is stored in the registry, so a language switch rewrites it.
        try
        {
            if (_explorer.IsRegistered) _explorer.Register(Environment.ProcessPath!, L.T("ExplorerVerb"));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Windows Forms sets its color mode once per process, so a new theme applies after a restart.</summary>
    void SetTheme(ThemeMode mode)
    {
        if (mode == _settings.Theme) return;
        _settings.Theme = mode;
        _settings.Save();
        foreach (ToolStripMenuItem item in _themeItem.DropDownItems) item.Checked = (ThemeMode)item.Tag! == mode;
        if (MessageBox.Show(this, L.T("ThemeRestart"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes && !IsBusy)
        {
            string args = SelectedTarget is string p ? $"--path \"{p.TrimEnd('\\')}\"" : "";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
                Close();
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    void SetExplorerIntegration(bool on)
    {
        try
        {
            if (on) _explorer.Register(Environment.ProcessPath!, L.T("ExplorerVerb"));
            else _explorer.Unregister();
            _status.Text = L.T(on ? "ExplorerAdded" : "ExplorerRemoved");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _explorerItem.Checked = _explorer.IsRegistered;
    }
}
