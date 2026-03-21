using System.Runtime.Versioning;
using SyncIt.Core.Models;

namespace SyncIt.App.Forms;

/// <summary>
/// Shown when the deletion safety threshold is exceeded.
/// Lists the files that would be deleted and asks the user to approve or cancel.
///
/// DialogResult.Yes   = user approved → deletions proceed
/// DialogResult.No    = user cancelled → deletions are permanently cancelled for this batch
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeletionWarningForm : Form
{
    public DeletionWarningForm(string syncPairId, SyncStatus? status)
    {
        Text            = "SyncIt — Deletion Safety Warning";
        Size            = new Size(600, 500);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;

        var layout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            Padding     = new Padding(16),
            RowCount    = 4,
        };

        // Warning header
        layout.Controls.Add(new Label
        {
            Text      = "⚠ Deletion Safety Warning",
            Font      = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.DarkOrange,
            AutoSize  = true,
        });

        layout.Controls.Add(new Label
        {
            Text = "SyncIt detected that more files than expected would be deleted in one sync cycle. " +
                   "This could indicate a misconfigured sync folder, accidental deletion, or a sync error.\n\n" +
                   "Review the files below and decide whether to proceed.",
            AutoSize  = true,
            MaximumSize = new Size(550, 0),
        });

        // File list
        var listBox = new ListBox
        {
            Dock           = DockStyle.Fill,
            SelectionMode  = SelectionMode.None,
            Height         = 200,
            Font           = new Font("Consolas", 9),
            ScrollAlwaysVisible = true,
        };

        // Populate from status
        if (status?.PairStatuses.FirstOrDefault(p => p.SyncPairId == syncPairId) is { } pairStatus)
            listBox.Items.Add($"[{pairStatus.PendingDeletions} files — see delete-journal.db for full list]");
        else
            listBox.Items.Add("(File list unavailable — check delete-journal.db)");

        layout.Controls.Add(listBox);

        // Buttons
        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock          = DockStyle.Fill,
            Padding       = new Padding(0, 8, 0, 0),
        };

        var cancelBtn = new Button
        {
            Text         = "Cancel Deletions (Safe)",
            DialogResult = DialogResult.No,
            Width        = 180,
            BackColor    = Color.FromArgb(0, 120, 215),
            ForeColor    = Color.White,
            FlatStyle    = FlatStyle.Flat,
        };

        var approveBtn = new Button
        {
            Text         = "Approve — Delete These Files",
            DialogResult = DialogResult.Yes,
            Width        = 200,
            BackColor    = Color.DarkRed,
            ForeColor    = Color.White,
            FlatStyle    = FlatStyle.Flat,
        };

        buttonPanel.Controls.Add(approveBtn);
        buttonPanel.Controls.Add(cancelBtn);
        layout.Controls.Add(buttonPanel);

        Controls.Add(layout);

        // Default to Cancel (safe)
        AcceptButton = cancelBtn;
        CancelButton = cancelBtn;
    }
}
