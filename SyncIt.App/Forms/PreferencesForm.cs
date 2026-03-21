using System.Runtime.Versioning;
using System.Text.Json;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.App.Forms;

[SupportedOSPlatform("windows")]
public sealed class PreferencesForm : Form
{
    private static readonly Color BgColor    = Color.FromArgb(250, 251, 253);
    private static readonly Color CardBg     = Color.White;
    private static readonly Color CardBorder = Color.FromArgb(225, 228, 232);
    private static readonly Color AccentBlue = Color.FromArgb(0, 102, 204);
    private static readonly Color SubtleText = Color.FromArgb(100, 110, 120);

    private static readonly string ConfigPath =
        PathHelper.Expand(@"%APPDATA%\SyncIt\syncit.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        WriteIndented          = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private SyncConfig _config = null!;

    // Controls
    private NumericUpDown _pollInterval = null!;
    private NumericUpDown _maxDeletions = null!;
    private NumericUpDown _graceCycles = null!;
    private NumericUpDown _uploadLimit = null!;
    private NumericUpDown _downloadLimit = null!;
    private NumericUpDown _activityRetention = null!;
    private CheckBox _startWithWindows = null!;

    public PreferencesForm()
    {
        LoadConfig();
        BuildUI();
    }

    private void LoadConfig()
    {
        if (File.Exists(ConfigPath))
        {
            var json = File.ReadAllText(ConfigPath);
            _config = JsonSerializer.Deserialize<SyncConfig>(json, JsonOpts) ?? new SyncConfig();
        }
        else
        {
            _config = new SyncConfig();
        }
    }

    private void SaveConfig()
    {
        _config.PollIntervalSeconds     = (int)_pollInterval.Value;
        _config.MaxDeletionsPerCycle     = (int)_maxDeletions.Value;
        _config.DeletionGraceCycles     = (int)_graceCycles.Value;
        _config.MaxUploadKBps           = (int)_uploadLimit.Value;
        _config.MaxDownloadKBps         = (int)_downloadLimit.Value;
        _config.ActivityLogRetentionDays = (int)_activityRetention.Value;

        var dir = Path.GetDirectoryName(ConfigPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, JsonOpts));
    }

    private void BuildUI()
    {
        Text            = "SyncIt Preferences";
        Size            = new Size(480, 560);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = BgColor;
        Font            = new Font("Segoe UI", 9.5f);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };
        Controls.Add(root);

        // Title
        var title = new Label
        {
            Text      = "Preferences",
            Font      = new Font("Segoe UI Semibold", 16f),
            ForeColor = Color.FromArgb(30, 30, 30),
            AutoSize  = true,
            Location  = new Point(0, 0),
        };
        root.Controls.Add(title);

        int y = 40;

        // ── Sync Settings Card ──
        y = AddSectionHeader(root, "Sync", y);
        var syncCard = MakeCard(root, ref y, 100);

        _pollInterval = AddNumericRow(syncCard, "Poll interval (seconds)", _config.PollIntervalSeconds, 5, 600, 10);
        _startWithWindows = new CheckBox
        {
            Text    = "Start SyncIt with Windows",
            Checked = AutoStartHelper.IsEnabled,
            AutoSize = true,
            Location = new Point(16, 66),
            FlatStyle = FlatStyle.Flat,
        };
        syncCard.Controls.Add(_startWithWindows);

        // ── Bandwidth Card ──
        y += 8;
        y = AddSectionHeader(root, "Bandwidth", y);
        var bwCard = MakeCard(root, ref y, 76);

        _uploadLimit   = AddNumericRow(bwCard, "Max upload (KB/s, 0 = unlimited)", _config.MaxUploadKBps, 0, 100000, 0);
        _downloadLimit = AddNumericRow(bwCard, "Max download (KB/s, 0 = unlimited)", _config.MaxDownloadKBps, 0, 100000, 36);

        // ── Safety Card ──
        y += 8;
        y = AddSectionHeader(root, "Deletion Safety", y);
        var safetyCard = MakeCard(root, ref y, 76);

        _maxDeletions = AddNumericRow(safetyCard, "Freeze threshold (file count)", _config.MaxDeletionsPerCycle, 1, 10000, 0);
        _graceCycles  = AddNumericRow(safetyCard, "Grace period (poll cycles)", _config.DeletionGraceCycles, 0, 50, 36);

        // ── Maintenance Card ──
        y += 8;
        y = AddSectionHeader(root, "Maintenance", y);
        var maintCard = MakeCard(root, ref y, 40);

        _activityRetention = AddNumericRow(maintCard, "Activity log retention (days)", _config.ActivityLogRetentionDays, 1, 365, 0);

        // ── Buttons ──
        y += 16;
        var btnPanel = new Panel
        {
            Location = new Point(20, y),
            Size     = new Size(420, 40),
        };
        root.Controls.Add(btnPanel);

        var saveBtn = new Button
        {
            Text      = "Save",
            Size      = new Size(100, 34),
            Location  = new Point(btnPanel.Width - 210, 0),
            BackColor = AccentBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
            Font      = new Font("Segoe UI Semibold", 10f),
        };
        saveBtn.FlatAppearance.BorderSize = 0;
        saveBtn.Click += (_, _) =>
        {
            SaveConfig();

            AutoStartHelper.SetEnabled(_startWithWindows.Checked);

            MessageBox.Show(this, "Settings saved. Some changes take effect on next sync cycle.",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        };
        btnPanel.Controls.Add(saveBtn);

        var cancelBtn = new Button
        {
            Text      = "Cancel",
            Size      = new Size(100, 34),
            Location  = new Point(btnPanel.Width - 104, 0),
            BackColor = CardBg,
            ForeColor = Color.FromArgb(60, 60, 60),
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
            Font      = new Font("Segoe UI", 10f),
        };
        cancelBtn.FlatAppearance.BorderColor = CardBorder;
        cancelBtn.Click += (_, _) => Close();
        btnPanel.Controls.Add(cancelBtn);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private int AddSectionHeader(Panel parent, string text, int y)
    {
        var lbl = new Label
        {
            Text      = text,
            Font      = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = Color.FromArgb(50, 50, 50),
            AutoSize  = true,
            Location  = new Point(0, y),
        };
        parent.Controls.Add(lbl);
        return y + 24;
    }

    private Panel MakeCard(Panel parent, ref int y, int height)
    {
        var card = new Panel
        {
            Location  = new Point(0, y),
            Size      = new Size(420, height),
            BackColor = CardBg,
            Padding   = new Padding(16, 10, 16, 10),
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(CardBorder);
            var r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            DrawRoundedRect(e.Graphics, pen, r, 6);
        };
        parent.Controls.Add(card);
        y += height;
        return card;
    }

    private NumericUpDown AddNumericRow(Panel card, string label, int value, int min, int max, int yOffset)
    {
        var lbl = new Label
        {
            Text      = label,
            AutoSize  = true,
            ForeColor = SubtleText,
            Location  = new Point(16, 12 + yOffset),
            Font      = new Font("Segoe UI", 9f),
        };
        card.Controls.Add(lbl);

        var nud = new NumericUpDown
        {
            Value    = Math.Clamp(value, min, max),
            Minimum  = min,
            Maximum  = max,
            Location = new Point(320, 8 + yOffset),
            Size     = new Size(80, 26),
            Font     = new Font("Segoe UI", 9.5f),
            BorderStyle = BorderStyle.FixedSingle,
        };
        card.Controls.Add(nud);
        return nud;
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
}
