using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>The settings menu: Explorer integration and the saved data window.</summary>
sealed partial class MainForm
{
    readonly ToolStripDropDownButton _settingsMenu = new() { Alignment = ToolStripItemAlignment.Right };
    readonly ToolStripMenuItem _explorerItem = new() { CheckOnClick = true };
    readonly ToolStripMenuItem _dataItem = new();
    readonly ExplorerIntegration _explorer = new();

    void BuildSettings()
    {
        _settingsMenu.DropDownItems.AddRange([_explorerItem, new ToolStripSeparator(), _dataItem]);
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
        // The menu text is stored in the registry, so a language switch rewrites it.
        try
        {
            if (_explorer.IsRegistered) _explorer.Register(Environment.ProcessPath!, L.T("ExplorerVerb"));
        }
        catch (Exception)
        {
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
