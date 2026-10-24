using System.Diagnostics;
using System.Runtime.Versioning;
using SyncIt.Core.Helpers;

namespace SyncIt.App.Forms;

/// <summary>
/// Shows the currently connected Google account and lets the user switch to a different one.
/// Flow: Stop engines → delete token → browser OAuth → restart engines.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AccountForm : Form
{
    private static readonly Color BgColor    = Color.FromArgb(250, 251, 253);
    private static readonly Color CardBg     = Color.White;
    private static readonly Color CardBorder = Color.FromArgb(225, 228, 232);
    private static readonly Color AccentBlue = Color.FromArgb(0, 102, 204);
    private static readonly Color AccentRed  = Color.FromArgb(215, 40, 40);
    private static readonly Color SubtleText = Color.FromArgb(100, 110, 120);

    private readonly string? _currentEmail;
    private readonly long _storageUsed;
    private readonly long _storageTotal;
    private readonly Func<Task> _switchAccount;

    private Label _statusLabel = null!;
    private Button _switchBtn = null!;
    private Button _cancelBtn = null!;

    /// <summary>
    /// Result is DialogResult.Yes if the user completed an account switch.
    /// </summary>
    public AccountForm(string? currentEmail, long storageUsed, long storageTotal, Func<Task> switchAccount)
    {
        _currentEmail  = currentEmail;
        _storageUsed   = storageUsed;
        _storageTotal  = storageTotal;
        _switchAccount = switchAccount;

        BuildUI();
    }

    private void BuildUI()
    {
        Text            = "SyncIt — Google Account";
        Size            = new Size(440, 340);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = BgColor;
        Font            = new Font("Segoe UI", 9.5f);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
        Controls.Add(root);

        // Title
        var title = new Label
        {
            Text      = "Google Account",
            Font      = new Font("Segoe UI Semibold", 16f),
            ForeColor = Color.FromArgb(30, 30, 30),
            AutoSize  = true,
            Location  = new Point(0, 0),
        };
        root.Controls.Add(title);

        // ── Current account card ──
        var card = new Panel
        {
            Location  = new Point(0, 44),
            Size      = new Size(376, 100),
            BackColor = CardBg,
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(CardBorder);
            var r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            DrawRoundedRect(e.Graphics, pen, r, 6);
        };
        root.Controls.Add(card);

        // Avatar circle with initial
        var initial = string.IsNullOrEmpty(_currentEmail) ? "?" : _currentEmail[0].ToString().ToUpper();
        var avatar = new Label
        {
            Text      = initial,
            Font      = new Font("Segoe UI Semibold", 16f),
            ForeColor = Color.White,
            BackColor = AccentBlue,
            TextAlign = ContentAlignment.MiddleCenter,
            Size      = new Size(48, 48),
            Location  = new Point(16, 26),
        };
        avatar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(AccentBlue);
            e.Graphics.FillEllipse(brush, 0, 0, avatar.Width - 1, avatar.Height - 1);
            var textSize = e.Graphics.MeasureString(initial, avatar.Font);
            e.Graphics.DrawString(initial, avatar.Font, Brushes.White,
                (avatar.Width - textSize.Width) / 2,
                (avatar.Height - textSize.Height) / 2);
        };
        card.Controls.Add(avatar);

        // Email
        var emailLabel = new Label
        {
            Text      = _currentEmail ?? "Not signed in",
            Font      = new Font("Segoe UI Semibold", 11f),
            ForeColor = Color.FromArgb(30, 30, 30),
            AutoSize  = true,
            Location  = new Point(76, 24),
        };
        card.Controls.Add(emailLabel);

        // Storage info
        var storageText = _storageTotal > 0
            ? $"{FormatBytes(_storageUsed)} of {FormatBytes(_storageTotal)} used"
            : "Storage info unavailable";
        var storageLabel = new Label
        {
            Text      = storageText,
            Font      = new Font("Segoe UI", 9f),
            ForeColor = SubtleText,
            AutoSize  = true,
            Location  = new Point(76, 50),
        };
        card.Controls.Add(storageLabel);

        // Connected status
        var connectedLabel = new Label
        {
            Text      = _currentEmail is not null ? "Connected" : "Not connected",
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = _currentEmail is not null ? Color.FromArgb(16, 163, 74) : AccentRed,
            AutoSize  = true,
            Location  = new Point(76, 70),
        };
        card.Controls.Add(connectedLabel);

        // ── Description ──
        var desc = new Label
        {
            Text = "Switch to a different Google account to sync your files.\n" +
                   "Your browser will open to sign in with the new account.\n" +
                   "SyncIt will restart syncing automatically after switching.",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = SubtleText,
            AutoSize  = true,
            MaximumSize = new Size(376, 0),
            Location  = new Point(0, 156),
        };
        root.Controls.Add(desc);

        // ── Status label ──
        _statusLabel = new Label
        {
            Text      = "",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = AccentBlue,
            AutoSize  = true,
            MaximumSize = new Size(376, 0),
            Location  = new Point(0, 218),
        };
        root.Controls.Add(_statusLabel);

        // ── Buttons ──
        _switchBtn = new Button
        {
            Text      = "Switch Account",
            Size      = new Size(140, 36),
            Location  = new Point(106, 248),
            BackColor = AccentBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
            Font      = new Font("Segoe UI Semibold", 10f),
        };
        _switchBtn.FlatAppearance.BorderSize = 0;
        _switchBtn.Click += OnSwitchAccount;
        root.Controls.Add(_switchBtn);

        _cancelBtn = new Button
        {
            Text      = "Cancel",
            Size      = new Size(100, 36),
            Location  = new Point(254, 248),
            BackColor = CardBg,
            ForeColor = Color.FromArgb(60, 60, 60),
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
            Font      = new Font("Segoe UI", 10f),
        };
        _cancelBtn.FlatAppearance.BorderColor = CardBorder;
        _cancelBtn.Click += (_, _) => Close();
        root.Controls.Add(_cancelBtn);
    }

    private async void OnSwitchAccount(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "This will sign you out of the current Google account and open your browser to sign in with a new one.\n\n" +
            "Syncing will pause during the switch.\n\n" +
            "Continue?",
            "Switch Google Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes) return;

        _switchBtn.Enabled = false;
        _cancelBtn.Enabled = false;

        try
        {
            SetStatus("Stopping sync engines...", AccentBlue);
            await Task.Yield(); // let UI update

            SetStatus("Opening browser to sign in with new account...", AccentBlue);
            await _switchAccount();

            SetStatus("Account switched successfully!", Color.FromArgb(16, 163, 74));
            DialogResult = DialogResult.Yes;

            await Task.Delay(1500);
            Close();
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to switch account: {ex.Message}", AccentRed);
            _switchBtn.Enabled = true;
            _cancelBtn.Enabled = true;
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text      = text;
        _statusLabel.ForeColor = color;
        _statusLabel.Refresh();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private static void DrawRoundedRect(Graphics g, Pen pen, Rectangle rect, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.DrawPath(pen, path);
    }
    // Fluent theme (see Theme.cs): remaps colors/fonts by role, dark mode, rounded window corners.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);
    }
}
