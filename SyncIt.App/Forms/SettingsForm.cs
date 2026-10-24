using System.Runtime.Versioning;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>Everything the Settings window needs from the app (keeps the form free of engine code).</summary>
internal sealed class SettingsHost
{
    public required Func<SyncConfig> LoadConfig { get; init; }
    public required Action<SyncConfig> SaveConfig { get; init; }
    public required Func<Task> AddAccount { get; init; }
    public required Func<string, Task> DisconnectAccount { get; init; }
    public required Action AddFolder { get; init; }
    public required Func<Task<string>> CheckForUpdatesNow { get; init; }
}

/// <summary>Windows 11-style Settings: navigation on the left, one page on the right.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SettingsForm : Form
{
    private static readonly string[] Pages = { "Accounts", "Folders", "Appearance", "Updates", "General" };

    private readonly SettingsHost _host;
    private SyncConfig _config;
    private readonly ListBox _nav = new() { Dock = DockStyle.Left, Width = 170, BorderStyle = BorderStyle.None, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 40 };
    private readonly Panel _page = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 20), AutoScroll = true };

    public SettingsForm(SettingsHost host, string startPage = "Accounts")
    {
        _host = host;
        _config = host.LoadConfig();
        Text = "SyncIt Settings";
        ClientSize = new Size(760, 540);
        MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body(10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _nav.Items.AddRange(Pages);
        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedItem as string ?? Pages[0]);
        Controls.Add(_page);
        Controls.Add(_nav);
        Theme.Apply(this);
        _nav.BackColor = Theme.SurfaceAlt;
        _page.BackColor = Theme.Background;
        _nav.SelectedIndex = Math.Max(0, Array.IndexOf(Pages, startPage));
    }

    private void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var bg = new SolidBrush(selected ? Theme.Hover : Theme.SurfaceAlt);
        e.Graphics.FillRectangle(bg, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.X + 4, e.Bounds.Y + 10, 3, e.Bounds.Height - 20);
        }
        TextRenderer.DrawText(e.Graphics, _nav.Items[e.Index].ToString(), Theme.Body(10.5f, selected ? FontStyle.Bold : FontStyle.Regular),
            new Rectangle(e.Bounds.X + 18, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private void ShowPage(string name)
    {
        _page.SuspendLayout();
        _page.Controls.Clear();
        var y = 0;
        Add(new Label { Text = name, Font = Theme.Display(18f), AutoSize = true }, ref y, 48);
        switch (name)
        {
            case "Accounts": BuildAccounts(ref y); break;
            case "Folders": BuildFolders(ref y); break;
            case "Appearance": BuildAppearance(ref y); break;
            case "Updates": BuildUpdates(ref y); break;
            default: BuildGeneral(ref y); break;
        }
        Theme.Apply(this);
        _nav.BackColor = Theme.SurfaceAlt;
        _page.ResumeLayout();
    }

    private void Add(Control c, ref int y, int advance)
    {
        c.Location = new Point(0, y);
        _page.Controls.Add(c);
        y += advance;
    }

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.SubtleText, MaximumSize = new Size(640, 0) };

    private static Button Btn(string text, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(120, 34), Padding = new Padding(8, 0, 8, 0) };
        Theme.StyleButton(b, primary);
        return b;
    }

    // ── Accounts ────────────────────────────────────────────────────────────
    private void BuildAccounts(ref int y)
    {
        Add(Hint("Sync folders from several Google accounts at the same time. Each folder belongs to one account."), ref y, 34);
        var list = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Size = new Size(640, 180), BorderStyle = BorderStyle.FixedSingle };
        list.Columns.Add("Account", 360);
        list.Columns.Add("Folders", 120);
        list.Columns.Add("Status", 140);
        foreach (var a in _config.Accounts)
        {
            var pairs = _config.SyncPairs.Count(p => AccountRegistry.ForPair(_config, p)?.Id == a.Id);
            var item = new ListViewItem(string.IsNullOrEmpty(a.Email) ? a.Id : a.Email) { Tag = a.Id };
            item.SubItems.Add(pairs.ToString());
            item.SubItems.Add("Signed in");
            list.Items.Add(item);
        }
        if (list.Items.Count > 0) list.Items[0].Selected = true;
        Add(list, ref y, 194);

        var add = Btn("Add account…", primary: true);
        add.Click += async (_, _) =>
        {
            add.Enabled = false;
            try { await _host.AddAccount(); }
            finally { _config = _host.LoadConfig(); ShowPage("Accounts"); }
        };
        var disconnect = Btn("Disconnect…");
        disconnect.Click += async (_, _) =>
        {
            if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not string id) return;
            var email = list.SelectedItems[0].Text;
            if (MessageBox.Show(this,
                    $"Disconnect {email}?\n\nSyncIt stops syncing its folders and removes its sign-in. Files on this PC and in Google Drive are not deleted.",
                    "Disconnect account", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            disconnect.Enabled = false;
            try { await _host.DisconnectAccount(id); }
            finally { _config = _host.LoadConfig(); if (!IsDisposed) ShowPage("Accounts"); }
        };
        add.Location = new Point(0, y);
        disconnect.Location = new Point(140, y);
        _page.Controls.Add(add);
        _page.Controls.Add(disconnect);
        y += 50;
    }

    // ── Folders ─────────────────────────────────────────────────────────────
    private void BuildFolders(ref int y)
    {
        Add(Hint("Choose a folder to change its filters, conflict rule or pause it. Use Add folder to sync another folder, account or shared drive."), ref y, 40);
        var pairs = new ListBox { Size = new Size(640, 96), IntegralHeight = false };
        foreach (var p in _config.SyncPairs) pairs.Items.Add(PairSettingsEditor.DescribePair(p, _config));
        Add(pairs, ref y, 108);

        var conflictLabel = new Label { Text = "When a file changed on both sides", AutoSize = true };
        Add(conflictLabel, ref y, 24);
        var conflict = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        conflict.Items.AddRange(Enum.GetNames<ConflictResolution>());
        Add(conflict, ref y, 40);
        Add(new Label { Text = "Exclude patterns (one per line, e.g. *.bak or node_modules/**)", AutoSize = true }, ref y, 24);
        var excludes = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Size = new Size(640, 90) };
        Add(excludes, ref y, 100);
        var paused = new CheckBox { Text = "Pause this folder", AutoSize = true };
        Add(paused, ref y, 40);

        var save = Btn("Save folder settings", primary: true);
        var addFolder = Btn("Add folder…");
        save.Location = new Point(0, y);
        addFolder.Location = new Point(190, y);
        _page.Controls.Add(save);
        _page.Controls.Add(addFolder);
        y += 50;

        void Load()
        {
            var on = pairs.SelectedIndex >= 0;
            conflict.Enabled = excludes.Enabled = paused.Enabled = save.Enabled = on;
            if (!on) return;
            var p = _config.SyncPairs[pairs.SelectedIndex];
            conflict.SelectedItem = p.ConflictPolicy.Resolution.ToString();
            excludes.Text = PairSettingsEditor.FormatPatterns(p.ExcludeGlobs);
            paused.Checked = p.Paused;
        }
        pairs.SelectedIndexChanged += (_, _) => Load();
        if (pairs.Items.Count > 0) pairs.SelectedIndex = 0; else Load();

        save.Click += (_, _) =>
        {
            if (pairs.SelectedIndex < 0) return;
            var p = _config.SyncPairs[pairs.SelectedIndex];
            var resolution = Enum.Parse<ConflictResolution>(conflict.SelectedItem as string ?? nameof(ConflictResolution.LastWriteWins));
            if (PairSettingsEditor.Apply(p, resolution, excludes.Text, paused.Checked))
            {
                _host.SaveConfig(_config);
                MessageBox.Show(this, "Saved. SyncIt applies the change right away.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            var index = pairs.SelectedIndex;
            pairs.Items[index] = PairSettingsEditor.DescribePair(p, _config);
        };
        addFolder.Click += (_, _) => { _host.AddFolder(); _config = _host.LoadConfig(); ShowPage("Folders"); };
    }

    // ── Appearance ──────────────────────────────────────────────────────────
    private void BuildAppearance(ref int y)
    {
        Add(Hint("Light is the default. Dark and System (follow Windows) are available too."), ref y, 34);
        Add(new Label { Text = "Theme", AutoSize = true }, ref y, 24);
        var theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        theme.Items.AddRange(Theme.Modes);
        var settings = TraySettings.Load();
        var idx = Array.FindIndex(Theme.Modes, m => m.Equals(settings.Theme, StringComparison.OrdinalIgnoreCase));
        theme.SelectedIndex = idx < 0 ? 0 : idx;
        Add(theme, ref y, 44);
        var notifications = new CheckBox { Text = "Show notifications", AutoSize = true, Checked = settings.NotificationsEnabled };
        Add(notifications, ref y, 44);
        var apply = Btn("Apply", primary: true);
        apply.Click += (_, _) =>
        {
            var s = TraySettings.Load();
            s.Theme = theme.SelectedItem as string ?? "Light";
            s.NotificationsEnabled = notifications.Checked;
            s.Save();
            Theme.Initialize(s.Theme);
            ShowPage("Appearance");
        };
        Add(apply, ref y, 50);
    }

    // ── Updates ─────────────────────────────────────────────────────────────
    private void BuildUpdates(ref int y)
    {
        Add(Hint($"You have SyncIt {AppInfo.Version}. Updates come from the project's GitHub Releases; the installer is checked against the release's SHA256SUMS before it runs, and nothing installs without your OK."), ref y, 56);
        var auto = new CheckBox { Text = "Check for updates automatically (daily)", AutoSize = true, Checked = _config.CheckForUpdates };
        auto.CheckedChanged += (_, _) => { _config.CheckForUpdates = auto.Checked; _host.SaveConfig(_config); };
        Add(auto, ref y, 44);
        var result = Hint("");
        var check = Btn("Check now", primary: true);
        check.Click += async (_, _) =>
        {
            check.Enabled = false;
            result.Text = "Checking…";
            try { result.Text = await _host.CheckForUpdatesNow(); }
            catch (Exception ex) { result.Text = "Could not check: " + ex.Message; }
            finally { check.Enabled = true; }
        };
        Add(check, ref y, 48);
        Add(result, ref y, 30);
    }

    // ── General ─────────────────────────────────────────────────────────────
    private void BuildGeneral(ref int y)
    {
        var startup = new CheckBox { Text = "Start SyncIt when I sign in to Windows", AutoSize = true, Checked = AutoStartHelper.IsEnabled };
        startup.CheckedChanged += (_, _) => AutoStartHelper.SetEnabled(startup.Checked);
        Add(startup, ref y, 48);

        Add(new Label { Text = "Bandwidth (KB/s, 0 = unlimited)", Font = Theme.Body(11f, FontStyle.Bold), AutoSize = true }, ref y, 32);
        Add(new Label { Text = "Upload limit", AutoSize = true }, ref y, 24);
        var up = new NumericUpDown { Maximum = 1_048_576, Width = 140, Value = Math.Max(0, _config.MaxUploadKBps), Increment = 128 };
        Add(up, ref y, 40);
        Add(new Label { Text = "Download limit", AutoSize = true }, ref y, 24);
        var down = new NumericUpDown { Maximum = 1_048_576, Width = 140, Value = Math.Max(0, _config.MaxDownloadKBps), Increment = 128 };
        Add(down, ref y, 44);

        var save = Btn("Save", primary: true);
        save.Click += (_, _) =>
        {
            _config.MaxUploadKBps = PairSettingsEditor.ClampBandwidth(up.Value);
            _config.MaxDownloadKBps = PairSettingsEditor.ClampBandwidth(down.Value);
            _host.SaveConfig(_config);
            up.Value = _config.MaxUploadKBps;
            down.Value = _config.MaxDownloadKBps;
        };
        var advanced = Btn("Advanced…");
        advanced.Click += (_, _) => { using var f = new PreferencesForm(); f.ShowDialog(this); _config = _host.LoadConfig(); };
        save.Location = new Point(0, y);
        advanced.Location = new Point(140, y);
        _page.Controls.Add(save);
        _page.Controls.Add(advanced);
        y += 50;
    }
}
