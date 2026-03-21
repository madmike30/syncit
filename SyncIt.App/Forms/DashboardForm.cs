using System.Diagnostics;
using System.Runtime.Versioning;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>
/// Main dashboard window — the primary UI surface for SyncIt.
/// Shows account, storage, sync pairs, recent activity, and quick actions.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DashboardForm : Form
{
    // ── Colors ──────────────────────────────────────────────────────────
    private static readonly Color BgColor       = Color.FromArgb(250, 251, 253);
    private static readonly Color HeaderBg      = Color.FromArgb(0, 102, 204);
    private static readonly Color HeaderFg      = Color.White;
    private static readonly Color CardBg        = Color.White;
    private static readonly Color CardBorder    = Color.FromArgb(225, 228, 232);
    private static readonly Color AccentBlue    = Color.FromArgb(0, 102, 204);
    private static readonly Color AccentGreen   = Color.FromArgb(16, 163, 74);
    private static readonly Color AccentOrange  = Color.FromArgb(234, 140, 0);
    private static readonly Color AccentRed     = Color.FromArgb(215, 40, 40);
    private static readonly Color SubtleText    = Color.FromArgb(100, 110, 120);
    private static readonly Color BarBg         = Color.FromArgb(230, 233, 237);

    // ── Callbacks ───────────────────────────────────────────────────────
    private readonly Func<SyncStatus?> _getStatus;
    private readonly Func<IReadOnlyList<FileTransferProgress>?> _getTransfers;
    private readonly Action _syncNow;
    private readonly Action _pauseResume;
    private readonly Func<bool> _isPaused;
    private readonly Func<Task<List<ActivityRecord>?>> _fetchActivity;
    private readonly Action<string> _changeSyncFolder;       // pairId
    private readonly Action<string> _changeDriveFolder;     // pairId
    private readonly Action<string> _selectSubfolders;      // pairId
    private readonly Action _openSyncFolder;
    private readonly Action? _showAccountForm;

    // ── Dynamic controls ────────────────────────────────────────────────
    private Label _accountLabel = null!;
    private Label _storageLabel = null!;
    private Panel _storageBar = null!;
    private Panel _storageBarFill = null!;
    private Label _statusDot = null!;
    private Label _statusText = null!;
    private Panel _pairsContainer = null!;
    private ListView _activityList = null!;
    private Button _syncNowBtn = null!;
    private Button _pauseBtn = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;

    public DashboardForm(
        Func<SyncStatus?> getStatus,
        Func<IReadOnlyList<FileTransferProgress>?> getTransfers,
        Action syncNow,
        Action pauseResume,
        Func<bool> isPaused,
        Func<Task<List<ActivityRecord>?>> fetchActivity,
        Action<string> changeSyncFolder,
        Action openSyncFolder,
        Action? showAccountForm = null,
        Action<string>? changeDriveFolder = null,
        Action<string>? selectSubfolders = null)
    {
        _getStatus        = getStatus;
        _getTransfers     = getTransfers;
        _syncNow          = syncNow;
        _pauseResume      = pauseResume;
        _isPaused         = isPaused;
        _fetchActivity    = fetchActivity;
        _changeSyncFolder = changeSyncFolder;
        _changeDriveFolder = changeDriveFolder ?? (_ => { });
        _selectSubfolders  = selectSubfolders ?? (_ => { });
        _openSyncFolder   = openSyncFolder;
        _showAccountForm  = showAccountForm;

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BuildUI();
    }

    // =====================================================================
    // Layout
    // =====================================================================

    private void BuildUI()
    {
        Text            = "SyncIt";
        Size            = new Size(560, 640);
        MinimumSize     = new Size(480, 500);
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = BgColor;
        Font            = new Font("Segoe UI", 9.5f);

        var root = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0) };

        // ── Header ──────────────────────────────────────────────────
        var header = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = HeaderBg };

        var titleLabel = new Label
        {
            Text      = "SyncIt",
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = HeaderFg,
            AutoSize  = true,
            Location  = new Point(20, 12),
        };

        _accountLabel = new Label
        {
            Text      = "Connecting...",
            Font      = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(200, 220, 255),
            AutoSize  = true,
            Location  = new Point(20, 48),
            Cursor    = _showAccountForm is not null ? Cursors.Hand : Cursors.Default,
        };
        if (_showAccountForm is not null)
            _accountLabel.Click += (_, _) => _showAccountForm();

        var switchLabel = new LinkLabel
        {
            Text      = "Switch account",
            Font      = new Font("Segoe UI", 8.5f),
            LinkColor = Color.FromArgb(180, 200, 255),
            AutoSize  = true,
            Anchor    = AnchorStyles.Top | AnchorStyles.Right,
            Location  = new Point(header.Width - 120, 52),
            Cursor    = Cursors.Hand,
        };
        if (_showAccountForm is not null)
            switchLabel.Click += (_, _) => _showAccountForm();

        header.Controls.Add(titleLabel);
        header.Controls.Add(_accountLabel);
        if (_showAccountForm is not null)
            header.Controls.Add(switchLabel);

        // ── Status + Storage card ───────────────────────────────────
        var statusCard = MakeCard(90);

        _statusDot = new Label
        {
            Text      = "\u25CF",
            Font      = new Font("Segoe UI", 14f),
            ForeColor = AccentGreen,
            AutoSize  = true,
            Location  = new Point(16, 12),
        };

        _statusText = new Label
        {
            Text      = "Starting...",
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 30, 30),
            AutoSize  = true,
            Location  = new Point(38, 14),
        };

        _storageLabel = new Label
        {
            Text      = "",
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = SubtleText,
            AutoSize  = true,
            Location  = new Point(16, 44),
        };

        _storageBar = new Panel
        {
            Location  = new Point(16, 66),
            Size      = new Size(300, 8),
            BackColor = BarBg,
        };
        _storageBar.Paint += (_, e) =>
        {
            using var path = RoundedRect(new Rectangle(0, 0, _storageBar.Width, _storageBar.Height), 4);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(BarBg);
            e.Graphics.FillPath(bg, path);
        };

        _storageBarFill = new Panel
        {
            Location  = new Point(0, 0),
            Size      = new Size(0, 8),
            BackColor = AccentBlue,
        };
        _storageBar.Controls.Add(_storageBarFill);

        statusCard.Controls.Add(_statusDot);
        statusCard.Controls.Add(_statusText);
        statusCard.Controls.Add(_storageLabel);
        statusCard.Controls.Add(_storageBar);

        // ── Action buttons ──────────────────────────────────────────
        var actionPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            Height        = 48,
            Padding       = new Padding(16, 6, 16, 6),
            FlowDirection = FlowDirection.LeftToRight,
        };

        _syncNowBtn = MakeActionButton("Sync Now", AccentBlue, () => _syncNow());
        _pauseBtn   = MakeActionButton("Pause", SubtleText, () =>
        {
            _pauseResume();
            RefreshUI();
        });
        var openFolderBtn = MakeActionButton("Open Folder", SubtleText, () => _openSyncFolder());

        actionPanel.Controls.Add(_syncNowBtn);
        actionPanel.Controls.Add(_pauseBtn);
        actionPanel.Controls.Add(openFolderBtn);

        // ── Sync Pairs section ──────────────────────────────────────
        var pairsHeader = MakeSectionHeader("Sync Pairs");
        _pairsContainer = new Panel
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Padding   = new Padding(16, 0, 16, 8),
        };

        // ── Recent Activity section ─────────────────────────────────
        var activityHeader = MakeSectionHeader("Recent Activity");

        _activityList = new ListView
        {
            Dock          = DockStyle.Top,
            Height        = 160,
            View          = View.Details,
            FullRowSelect = true,
            HeaderStyle   = ColumnHeaderStyle.Nonclickable,
            BorderStyle   = BorderStyle.None,
            BackColor     = CardBg,
            Font          = new Font("Segoe UI", 8.5f),
        };
        _activityList.Columns.Add("Time",    110);
        _activityList.Columns.Add("Type",    70);
        _activityList.Columns.Add("File",    220);
        _activityList.Columns.Add("Size",    70);

        var activityCard = MakeCard(168);
        activityCard.Controls.Add(_activityList);
        _activityList.Dock = DockStyle.Fill;

        // ── Bottom links ────────────────────────────────────────────
        var bottomPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            Height        = 36,
            Padding       = new Padding(16, 6, 16, 6),
            FlowDirection = FlowDirection.LeftToRight,
        };

        bottomPanel.Controls.Add(MakeLinkLabel("Activity History", () =>
        {
            using var form = new ActivityHistoryForm(_fetchActivity);
            form.ShowDialog(this);
        }));
        bottomPanel.Controls.Add(MakeLinkLabel("Open Logs", () =>
        {
            var logDir = PathHelper.Expand(@"%APPDATA%\SyncIt\logs");
            if (Directory.Exists(logDir)) Process.Start("explorer.exe", logDir);
        }));
        bottomPanel.Controls.Add(MakeLinkLabel("Preferences", () =>
        {
            using var form = new PreferencesForm();
            form.ShowDialog(this);
        }));

        // ── Assemble (reverse order because DockStyle.Top stacks top-down) ──
        root.Controls.Add(bottomPanel);
        root.Controls.Add(activityCard);
        root.Controls.Add(activityHeader);
        root.Controls.Add(_pairsContainer);
        root.Controls.Add(pairsHeader);
        root.Controls.Add(actionPanel);
        root.Controls.Add(statusCard);
        root.Controls.Add(header);

        // Reverse child order so header is on top
        var children = new Control[root.Controls.Count];
        for (int i = 0; i < root.Controls.Count; i++)
            children[i] = root.Controls[i];
        root.Controls.Clear();
        for (int i = children.Length - 1; i >= 0; i--)
            root.Controls.Add(children[i]);

        Controls.Add(root);
    }

    // =====================================================================
    // Lifecycle
    // =====================================================================

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshUI();
        _ = LoadActivityAsync();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshUI();
            _ = LoadActivityAsync();
        };
        _refreshTimer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        base.OnFormClosing(e);
    }

    // =====================================================================
    // Refresh
    // =====================================================================

    private void OnChangeFolderClick(string pairId)
    {
        _refreshTimer?.Stop();
        try
        {
            _changeSyncFolder(pairId);
        }
        finally
        {
            _refreshTimer?.Start();
        }
    }

    private void OnSelectSubfoldersClick(string pairId)
    {
        _refreshTimer?.Stop();
        try
        {
            _selectSubfolders(pairId);
        }
        finally
        {
            _refreshTimer?.Start();
        }
    }

    private void OnChangeDriveFolderClick(string pairId)
    {
        _refreshTimer?.Stop();
        try
        {
            _changeDriveFolder(pairId);
        }
        finally
        {
            _refreshTimer?.Start();
        }
    }

    private void RefreshUI()
    {
        var status = _getStatus();
        if (status is null) return;

        // Account
        _accountLabel.Text = status.AccountEmail ?? "Not signed in";

        // State
        var (dot, color, text) = status.State switch
        {
            SyncState.Idle           => ("\u25CF", AccentGreen,  "Up to date"),
            SyncState.Syncing        => ("\u25CF", AccentBlue,   "Syncing..."),
            SyncState.Paused         => ("\u25CF", SubtleText,   "Paused"),
            SyncState.Error          => ("\u25CF", AccentRed,    "Error"),
            SyncState.SafetyHalt     => ("\u25CF", AccentRed,    "Safety Halt"),
            SyncState.DeletionFrozen => ("\u25CF", AccentOrange, "Deletions Frozen"),
            _                        => ("\u25CF", SubtleText,   status.State.ToString()),
        };
        _statusDot.Text      = dot;
        _statusDot.ForeColor = color;
        _statusText.Text     = text;

        // Storage
        if (status.StorageTotalBytes > 0)
        {
            var pct = (double)status.StorageUsedBytes / status.StorageTotalBytes;
            _storageLabel.Text = $"{FormatBytes(status.StorageUsedBytes)} of {FormatBytes(status.StorageTotalBytes)} used ({pct:P0})";
            var fillWidth = (int)(pct * _storageBar.Width);
            _storageBarFill.Size = new Size(Math.Max(1, fillWidth), _storageBar.Height);
            _storageBarFill.BackColor = pct > 0.9 ? AccentRed : pct > 0.75 ? AccentOrange : AccentBlue;
            _storageBar.Visible  = true;
            _storageLabel.Visible = true;
        }
        else
        {
            _storageBar.Visible  = false;
            _storageLabel.Visible = false;
        }

        // Pause button
        _pauseBtn.Text = _isPaused() ? "\u25B6  Resume" : "\u23F8  Pause";

        // Sync pairs
        RebuildPairCards(status);
    }

    private void RebuildPairCards(SyncStatus status)
    {
        _pairsContainer.SuspendLayout();
        _pairsContainer.Controls.Clear();

        int y = 0;
        foreach (var pair in status.PairStatuses)
        {
            var card = new Panel
            {
                Location  = new Point(0, y),
                Size      = new Size(_pairsContainer.Width - 32, 96),
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = CardBg,
                Padding   = new Padding(12, 8, 12, 8),
            };
            card.Paint += PaintCardBorder;

            var (_, pairColor, pairStateText) = pair.State switch
            {
                SyncState.Idle           => ("", AccentGreen,  "Up to date"),
                SyncState.Syncing        => ("", AccentBlue,   "Syncing"),
                SyncState.Paused         => ("", SubtleText,   "Paused"),
                SyncState.Error          => ("", AccentRed,    "Error"),
                SyncState.DeletionFrozen => ("", AccentOrange, "Frozen"),
                _                        => ("", SubtleText,   pair.State.ToString()),
            };

            var pairDot = new Label
            {
                Text      = "\u25CF",
                Font      = new Font("Segoe UI", 9f),
                ForeColor = pairColor,
                AutoSize  = true,
                Location  = new Point(12, 10),
            };

            var nameLabel = new Label
            {
                Text      = pair.RemoteFolderName,
                Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 30),
                AutoSize  = true,
                Location  = new Point(28, 8),
            };

            var stateLabel = new Label
            {
                Text      = pairStateText,
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = pairColor,
                AutoSize  = true,
                Location  = new Point(28, 28),
            };

            var pathLabel = new Label
            {
                Text      = pair.LocalPath ?? "",
                Font      = new Font("Segoe UI", 8f),
                ForeColor = SubtleText,
                AutoSize  = true,
                Location  = new Point(28, 46),
                MaximumSize = new Size(card.Width - 140, 0),
            };

            // Subfolder summary
            var subs = pair.SelectedSubfolders;
            string subText;
            string? subTooltip = null;
            Color subColor;
            if (subs.Count == 0)
            {
                subText  = "Syncing all folders";
                subColor = AccentGreen;
            }
            else if (subs.Count <= 3)
            {
                subText  = "Syncing: " + string.Join(", ", subs);
                subColor = Color.FromArgb(30, 30, 30);
            }
            else
            {
                subText    = $"Syncing {subs.Count} folders";
                subTooltip = string.Join("\n", subs);
                subColor   = Color.FromArgb(30, 30, 30);
            }

            var subfolderLabel = new Label
            {
                Text      = subText,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = subColor,
                AutoSize  = true,
                Location  = new Point(28, 64),
                MaximumSize = new Size(card.Width - 280, 0),
            };
            if (subTooltip is not null)
            {
                var tip = new ToolTip();
                tip.SetToolTip(subfolderLabel, subTooltip);
            }

            var pairId = pair.SyncPairId;

            var selectFoldersBtn = new Button
            {
                Text      = "Select folders",
                Font      = new Font("Segoe UI", 8f),
                ForeColor = AccentBlue,
                BackColor = CardBg,
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(95, 24),
                Location  = new Point(Math.Max(140, card.Width - 346), 8),
                Anchor    = AnchorStyles.Top | AnchorStyles.Right,
                Cursor    = Cursors.Hand,
            };
            selectFoldersBtn.FlatAppearance.BorderColor = CardBorder;
            selectFoldersBtn.Click += (_, _) => OnSelectSubfoldersClick(pairId);

            var changeDriveFolderBtn = new Button
            {
                Text      = "Change Drive folder",
                Font      = new Font("Segoe UI", 8f),
                ForeColor = AccentBlue,
                BackColor = CardBg,
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(130, 24),
                Location  = new Point(Math.Max(240, card.Width - 246), 8),
                Anchor    = AnchorStyles.Top | AnchorStyles.Right,
                Cursor    = Cursors.Hand,
            };
            changeDriveFolderBtn.FlatAppearance.BorderColor = CardBorder;
            changeDriveFolderBtn.Click += (_, _) => OnChangeDriveFolderClick(pairId);

            var changeFolderBtn = new Button
            {
                Text      = "Change local folder",
                Font      = new Font("Segoe UI", 8f),
                ForeColor = AccentBlue,
                BackColor = CardBg,
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(110, 24),
                Location  = new Point(Math.Max(375, card.Width - 111), 8),
                Anchor    = AnchorStyles.Top | AnchorStyles.Right,
                Cursor    = Cursors.Hand,
            };
            changeFolderBtn.FlatAppearance.BorderColor = CardBorder;
            changeFolderBtn.Click += (_, _) => OnChangeFolderClick(pairId);

            if (pair.PendingChanges > 0 || pair.PendingDeletions > 0)
            {
                stateLabel.Text += $"  \u2022  {pair.PendingChanges} pending, {pair.PendingDeletions} deletions";
            }

            if (pair.LastError is not null)
            {
                stateLabel.Text += $"  \u2022  {pair.LastError}";
                stateLabel.ForeColor = AccentRed;
            }

            card.Controls.Add(pairDot);
            card.Controls.Add(nameLabel);
            card.Controls.Add(stateLabel);
            card.Controls.Add(pathLabel);
            card.Controls.Add(subfolderLabel);
            card.Controls.Add(selectFoldersBtn);
            card.Controls.Add(changeDriveFolderBtn);
            card.Controls.Add(changeFolderBtn);

            _pairsContainer.Controls.Add(card);
            y += 104;
        }

        if (status.PairStatuses.Count == 0)
        {
            var emptyLabel = new Label
            {
                Text      = "No sync pairs configured. Edit Preferences to add one.",
                ForeColor = SubtleText,
                AutoSize  = true,
                Location  = new Point(0, 4),
            };
            _pairsContainer.Controls.Add(emptyLabel);
            y = 28;
        }

        _pairsContainer.Height = y + 8;
        _pairsContainer.ResumeLayout();
    }

    private async Task LoadActivityAsync()
    {
        try
        {
            var records = await _fetchActivity();
            if (records is null || records.Count == 0) return;

            _activityList.BeginUpdate();
            _activityList.Items.Clear();

            foreach (var r in records.Take(20))
            {
                var item = new ListViewItem(r.TimestampUtc.ToLocalTime().ToString("MM/dd HH:mm"));
                item.SubItems.Add(r.Type.ToString());
                item.SubItems.Add(Path.GetFileName(r.RelativePath));
                item.SubItems.Add(FormatBytes(r.SizeBytes));

                item.ForeColor = r.Type switch
                {
                    ActivityType.Error            => AccentRed,
                    ActivityType.ConflictResolved  => AccentOrange,
                    ActivityType.Deleted           => SubtleText,
                    ActivityType.FolderDeleted     => SubtleText,
                    ActivityType.Renamed           => Color.FromArgb(80, 80, 180),
                    ActivityType.FolderCreated     => Color.FromArgb(40, 140, 80),
                    _                              => Color.FromArgb(30, 30, 30),
                };
                _activityList.Items.Add(item);
            }
            _activityList.EndUpdate();
        }
        catch { }
    }

    // =====================================================================
    // UI Helpers
    // =====================================================================

    private static Panel MakeCard(int height)
    {
        var card = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = height,
            Margin    = new Padding(16, 8, 16, 0),
            BackColor = CardBg,
            Padding   = new Padding(1),
        };
        card.Paint += PaintCardBorder;
        return card;
    }

    private static void PaintCardBorder(object? sender, PaintEventArgs e)
    {
        if (sender is not Control c) return;
        using var pen = new Pen(CardBorder, 1);
        var rect = new Rectangle(0, 0, c.Width - 1, c.Height - 1);
        using var path = RoundedRect(rect, 6);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    private static Label MakeSectionHeader(string text)
    {
        return new Label
        {
            Text      = text,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 55, 60),
            Dock      = DockStyle.Top,
            Height    = 30,
            Padding   = new Padding(16, 8, 0, 0),
        };
    }

    private static Button MakeActionButton(string text, Color accent, Action onClick)
    {
        var btn = new Button
        {
            Text      = text,
            Width     = 110,
            Height    = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = accent,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
            Margin    = new Padding(0, 0, 8, 0),
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static LinkLabel MakeLinkLabel(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Text      = text,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 9f),
            LinkColor = AccentBlue,
            Margin    = new Padding(0, 0, 16, 0),
            Padding   = new Padding(0, 2, 0, 0),
        };
        link.Click += (_, _) => onClick();
        return link;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
