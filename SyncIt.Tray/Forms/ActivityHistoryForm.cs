using System.Runtime.Versioning;
using SyncIt.Core.Models;

namespace SyncIt.Tray.Forms;

/// <summary>
/// Displays recent sync activity (uploads, downloads, conflicts, deletions, errors)
/// retrieved from the service's ActivityLog via IPC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActivityHistoryForm : Form
{
    private readonly ListView _listView;
    private readonly ComboBox _typeFilter;
    private readonly Button _refreshButton;
    private readonly Func<Task<List<ActivityRecord>?>> _fetchRecords;

    private List<ActivityRecord>? _allRecords;

    public ActivityHistoryForm(Func<Task<List<ActivityRecord>?>> fetchRecords)
    {
        _fetchRecords = fetchRecords;

        Text            = "SyncIt — Activity History";
        Size            = new Size(720, 480);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;

        // Filter bar
        var filterPanel = new Panel { Dock = DockStyle.Top, Height = 36 };

        var filterLabel = new Label
        {
            Text = "Filter:",
            AutoSize = true,
            Location = new Point(8, 10),
        };

        _typeFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(52, 7),
            Width = 140,
        };
        _typeFilter.Items.Add("All");
        _typeFilter.Items.Add("Upload");
        _typeFilter.Items.Add("Download");
        _typeFilter.Items.Add("Conflict");
        _typeFilter.Items.Add("Deleted");
        _typeFilter.Items.Add("Error");
        _typeFilter.SelectedIndex = 0;
        _typeFilter.SelectedIndexChanged += (_, _) => ApplyFilter();

        _refreshButton = new Button
        {
            Text = "Refresh",
            Location = new Point(200, 6),
            Width = 80,
        };
        _refreshButton.Click += async (_, _) => await LoadRecordsAsync();

        filterPanel.Controls.Add(filterLabel);
        filterPanel.Controls.Add(_typeFilter);
        filterPanel.Controls.Add(_refreshButton);

        // List view
        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        _listView.Columns.Add("Time", 140);
        _listView.Columns.Add("Type", 80);
        _listView.Columns.Add("File", 280);
        _listView.Columns.Add("Size", 80);
        _listView.Columns.Add("Details", 120);

        Controls.Add(_listView);
        Controls.Add(filterPanel);

        Load += async (_, _) => await LoadRecordsAsync();
    }

    private async Task LoadRecordsAsync()
    {
        _refreshButton.Enabled = false;
        _refreshButton.Text = "Loading...";

        try
        {
            _allRecords = await _fetchRecords();
            ApplyFilter();
        }
        catch
        {
            _allRecords = null;
            _listView.Items.Clear();
            _listView.Items.Add(new ListViewItem("Failed to load activity history."));
        }
        finally
        {
            _refreshButton.Enabled = true;
            _refreshButton.Text = "Refresh";
        }
    }

    private void ApplyFilter()
    {
        _listView.Items.Clear();
        if (_allRecords is null) return;

        var filterIndex = _typeFilter.SelectedIndex;
        var filtered = filterIndex == 0
            ? _allRecords
            : _allRecords.Where(r => (int)r.Type == filterIndex - 1).ToList();

        foreach (var record in filtered)
        {
            var item = new ListViewItem(record.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(record.Type.ToString());
            item.SubItems.Add(record.RelativePath);
            item.SubItems.Add(FormatBytes(record.SizeBytes));
            item.SubItems.Add(record.Details ?? "");

            item.ForeColor = record.Type switch
            {
                ActivityType.Error => Color.DarkRed,
                ActivityType.ConflictResolved => Color.DarkOrange,
                ActivityType.Deleted => Color.Gray,
                _ => Color.Black,
            };

            _listView.Items.Add(item);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
