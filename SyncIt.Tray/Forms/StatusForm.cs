using System.Runtime.Versioning;
using SyncIt.Core.Models;

namespace SyncIt.Tray.Forms;

/// <summary>
/// Popup status window showing the current sync status for all pairs.
/// Opened by double-clicking the tray icon or the "Open Status" menu item.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StatusForm : Form
{
    private readonly SyncStatus? _status;
    private readonly IReadOnlyList<FileTransferProgress>? _activeTransfers;

    public StatusForm(SyncStatus? status, IReadOnlyList<FileTransferProgress>? activeTransfers = null)
    {
        _status = status;
        _activeTransfers = activeTransfers;
        InitializeComponent();
        PopulateStatus();
    }

    private void InitializeComponent()
    {
        Text            = "SyncIt Status";
        Size            = new Size(520, 420);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
    }

    private void PopulateStatus()
    {
        var panel = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            Padding     = new Padding(12),
            AutoScroll  = true,
        };

        if (_status is null)
        {
            panel.Controls.Add(new Label
            {
                Text     = "Not connected to SyncIt service.\nMake sure the service is running.",
                AutoSize = true,
                ForeColor = Color.DarkRed,
            });
            Controls.Add(panel);
            return;
        }

        // Global state
        var stateLabel = new Label
        {
            Text      = $"Status: {_status.State}",
            Font      = new Font(Font, FontStyle.Bold),
            AutoSize  = true,
            ForeColor = StateColor(_status.State),
        };
        panel.Controls.Add(stateLabel);

        if (_status.LastSuccessfulSyncUtc.HasValue)
            panel.Controls.Add(new Label
            {
                Text     = $"Last sync: {_status.LastSuccessfulSyncUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                AutoSize = true,
            });

        // Session stats
        panel.Controls.Add(new Label
        {
            Text     = $"Session: ↑{_status.FilesUploadedSession} uploaded   ↓{_status.FilesDownloadedSession} downloaded   ⚡{_status.ConflictsResolvedSession} conflicts   ✗{_status.ErrorsSession} errors",
            AutoSize = true,
            Padding  = new Padding(0, 8, 0, 8),
        });

        // Active transfers
        if (_activeTransfers is not null && _activeTransfers.Count > 0)
        {
            panel.Controls.Add(new Label
            {
                Text     = "Active Transfers:",
                Font     = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Padding  = new Padding(0, 4, 0, 2),
            });

            foreach (var transfer in _activeTransfers)
            {
                var direction = transfer.Direction == TransferDirection.Upload ? "↑" : "↓";
                var fileName  = Path.GetFileName(transfer.RelativePath);
                var sizeText  = FormatBytes(transfer.TotalBytes);
                var pctText   = $"{transfer.ProgressPercent:F0}%";

                var transferPanel = new Panel
                {
                    Dock   = DockStyle.Top,
                    Height = 36,
                };

                var label = new Label
                {
                    Text     = $"{direction} {fileName}  ({pctText} of {sizeText})",
                    AutoSize = true,
                    Location = new Point(0, 0),
                };

                var bar = new ProgressBar
                {
                    Location = new Point(0, 18),
                    Size     = new Size(460, 14),
                    Minimum  = 0,
                    Maximum  = 100,
                    Value    = Math.Min(100, (int)transfer.ProgressPercent),
                };

                transferPanel.Controls.Add(label);
                transferPanel.Controls.Add(bar);
                panel.Controls.Add(transferPanel);
            }
        }

        // Pair details
        if (_status.PairStatuses.Count > 0)
        {
            panel.Controls.Add(new Label
            {
                Text     = "Sync Pairs:",
                Font     = new Font(Font, FontStyle.Bold),
                AutoSize = true,
            });

            foreach (var pair in _status.PairStatuses)
            {
                var pairBox = new GroupBox
                {
                    Text    = $"{pair.RemoteFolderName} [{pair.State}]",
                    Dock    = DockStyle.Top,
                    Height  = 70,
                    Padding = new Padding(8),
                };

                var pairLabel = new Label
                {
                    Text     = $"Local: {pair.LocalPath}\nPending: {pair.PendingChanges} changes, {pair.PendingDeletions} deletions",
                    AutoSize = true,
                    Dock     = DockStyle.Fill,
                };

                if (pair.LastError is not null)
                    pairLabel.Text += $"\nError: {pair.LastError}";

                pairBox.Controls.Add(pairLabel);
                panel.Controls.Add(pairBox);
            }
        }

        if (_status.LastError is not null)
            panel.Controls.Add(new Label
            {
                Text      = $"Last error: {_status.LastError}",
                AutoSize  = true,
                ForeColor = Color.DarkRed,
            });

        // Close button
        var closeBtn = new Button
        {
            Text        = "Close",
            DialogResult = DialogResult.Cancel,
            Anchor      = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        closeBtn.Click += (_, _) => Close();
        panel.Controls.Add(closeBtn);

        Controls.Add(panel);
    }

    private static Color StateColor(SyncState state) => state switch
    {
        SyncState.Idle    => Color.DarkGreen,
        SyncState.Syncing => Color.Blue,
        SyncState.Paused  => Color.Gray,
        SyncState.Error or SyncState.SafetyHalt => Color.DarkRed,
        SyncState.DeletionFrozen => Color.DarkOrange,
        _ => Color.Black,
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
