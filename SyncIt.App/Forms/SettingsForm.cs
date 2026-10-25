using System.Runtime.Versioning;
using SyncIt.Core.Helpers;
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
    private readonly Panel _page = new() { Dock = DockStyle.Fill, AutoScroll = true };

    public SettingsForm(SettingsHost host, string startPage = "Accounts")
    {
        _host = host;
        _config = host.LoadConfig();
        Text = "SyncIt Settings";
        ClientSize = new Size(900, 600);
        MinimumSize = new Size(880, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body(10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _nav.Items.AddRange(Pages);
        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) =>
        {
            // Theme.Apply can recreate the list's handle and re-raise this event; never rebuild re-entrantly.
            var name = _nav.SelectedItem as string ?? Pages[0];
            if (_building || name == _currentPage) return;
            ShowPage(name);
        };
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

    private bool _building;
    private readonly List<(Label Badge, Color Color)> _badges = new();
    private string? _currentPage;

    private void ShowPage(string name)
    {
        if (_building) return;
        _building = true;
        _currentPage = name;
        try { BuildPage(name); }
        finally { _building = false; }
    }

    private void BuildPage(string name)
    {
        _page.SuspendLayout();
        _page.Controls.Clear();
        _badges.Clear();
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
        foreach (Control c in _page.Controls) c.Location = new Point(c.Left + 32, c.Top + 20); // page margins
        Theme.Apply(this);
        foreach (var (b, col) in _badges) { b.BackColor = col; b.ForeColor = Color.White; } // keep account colours
        _nav.BackColor = Theme.SurfaceAlt;
        _page.ResumeLayout();
    }

    private void RefreshPage(string name)
    {
        _currentPage = null;
        ShowPage(name);
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

        // Primary action sits right above the account list so it is impossible to miss.
        var add = Btn("+ Add Google account", primary: true);
        add.MinimumSize = new Size(200, 38);
        add.Click += async (_, _) =>
        {
            add.Enabled = false;
            try { await _host.AddAccount(); }
            finally { _config = _host.LoadConfig(); if (!IsDisposed) RefreshPage("Accounts"); }
        };
        Add(add, ref y, 54);

        if (_config.Accounts.Count == 0)
            Add(Hint("No Google account connected yet. Click \"+ Add Google account\" to sign in."), ref y, 34);

        foreach (var a in _config.Accounts)
        {
            var pairs = _config.SyncPairs.Count(p => AccountRegistry.ForPair(_config, p)?.Id == a.Id);
            var email = string.IsNullOrEmpty(a.Email) ? a.Id : a.Email;
            var color = ColorTranslator.FromHtml(AccountColors.For(_config, a.Id));
            var row = new Panel { Size = new Size(640, 56), BorderStyle = BorderStyle.FixedSingle };
            var badge = new Label
            {
                Text = AccountColors.Initial(email), Size = new Size(30, 30), Location = new Point(10, 12),
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, BackColor = color,
                Font = Theme.Body(10f, FontStyle.Bold),
            };
            row.Controls.Add(badge);
            row.Controls.Add(new Label { Text = email, Font = Theme.Body(10.5f, FontStyle.Bold), AutoSize = true, Location = new Point(50, 8) });
            row.Controls.Add(new Label
            {
                Text = $"{(a.Paused ? "Paused" : "Signed in")} · {pairs} folder{(pairs == 1 ? "" : "s")}",
                ForeColor = Theme.SubtleText, AutoSize = true, Location = new Point(50, 30),
            });
            var pause = Btn(a.Paused ? "Resume" : "Pause");
            pause.MinimumSize = new Size(100, 32);
            pause.Location = new Point(640 - 260, 11);
            var accId = a.Id;
            pause.Click += (_, _) =>
            {
                var acc = _config.Accounts.FirstOrDefault(x => x.Id == accId);
                if (acc is null) return;
                acc.Paused = !acc.Paused;
                _host.SaveConfig(_config);
                RefreshPage("Accounts");
            };
            row.Controls.Add(pause);
            _badges.Add((badge, color));
            var disconnect = Btn("Disconnect…");
            disconnect.MinimumSize = new Size(120, 32);
            disconnect.Location = new Point(640 - 140, 11);
            var id = a.Id;
            disconnect.Click += async (_, _) =>
            {
                if (MessageBox.Show(this,
                        $"Disconnect {email}?\n\nSyncIt stops syncing its folders and removes its sign-in. Files on this PC and in Google Drive are not deleted.",
                        "Disconnect account", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                disconnect.Enabled = false;
                try { await _host.DisconnectAccount(id); }
                finally { _config = _host.LoadConfig(); if (!IsDisposed) RefreshPage("Accounts"); }
            };
            row.Controls.Add(disconnect);
            Add(row, ref y, 64);
        }
        y += 10;
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
        Add(new Label { Text = "Skip files larger than (MB)", AutoSize = true }, ref y, 24);
        var maxSize = new NumericUpDown { Minimum = 1, Maximum = 5 * 1024 * 1024, Width = 140, Increment = 100 };
        Add(maxSize, ref y, 36);
        var paused = new CheckBox { Text = "Pause this folder", AutoSize = true };
        Add(paused, ref y, 34);
        Add(new Label { Text = "Sync direction", AutoSize = true }, ref y, 24);
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
        mode.Items.AddRange(new object[] { "Two-way (PC ↔ Drive)", "Backup only (PC → Drive, never changes files on this PC)" });
        Add(mode, ref y, 34);
        var keepDeleted = new CheckBox { Text = "Backup only: keep files on Drive when I delete them on this PC", AutoSize = true };
        Add(keepDeleted, ref y, 40);
        mode.SelectedIndexChanged += (_, _) => keepDeleted.Enabled = mode.Enabled && mode.SelectedIndex == 1;

        var exportOn = new CheckBox { Text = "Download Google Docs, Sheets and Slides as files:", AutoSize = true };
        Add(exportOn, ref y, 30);
        ComboBox Fmt(IEnumerable<string> keys) { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 }; c.Items.AddRange(keys.ToArray<object>()); return c; }
        var docF = Fmt(GoogleDocExportMap.DocFormats.Keys); var sheetF = Fmt(GoogleDocExportMap.SheetFormats.Keys); var slideF = Fmt(GoogleDocExportMap.SlideFormats.Keys);
        int x = 0;
        foreach (var (lbl, combo) in new[] { ("Docs", docF), ("Sheets", sheetF), ("Slides", slideF) })
        {
            var l = new Label { Text = lbl, AutoSize = true, Location = new Point(x, y + 4) };
            combo.Location = new Point(x + 52, y);
            _page.Controls.Add(l); _page.Controls.Add(combo); x += 170;
        }
        y += 34;
        var exportRo = new CheckBox { Text = "Make these copies read-only (edit them in Google; they are never uploaded back)", AutoSize = true };
        Add(exportRo, ref y, 40);
        exportOn.CheckedChanged += (_, _) => docF.Enabled = sheetF.Enabled = slideF.Enabled = exportRo.Enabled = exportOn.Enabled && exportOn.Checked;

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
            conflict.Enabled = excludes.Enabled = paused.Enabled = save.Enabled = mode.Enabled = maxSize.Enabled = on;
            keepDeleted.Enabled = on && mode.SelectedIndex == 1;
            if (!on) return;
            var p = _config.SyncPairs[pairs.SelectedIndex];
            conflict.SelectedItem = p.ConflictPolicy.Resolution.ToString();
            excludes.Text = PairSettingsEditor.FormatPatterns(p.ExcludeGlobs);
            paused.Checked = p.Paused;
            mode.SelectedIndex = p.Mode == SyncMode.BackupOnly ? 1 : 0;
            maxSize.Value = Math.Clamp(p.MaxFileSizeBytes / (1024 * 1024), 1, 5 * 1024 * 1024);
            keepDeleted.Checked = p.BackupKeepDeletedOnDrive;
            keepDeleted.Enabled = mode.SelectedIndex == 1;
            exportOn.Enabled = !p.Encrypted;
            exportOn.Checked = p.ExportGoogleDocs;
            docF.SelectedItem = p.DocsExportFormat; sheetF.SelectedItem = p.SheetsExportFormat; slideF.SelectedItem = p.SlidesExportFormat;
            if (docF.SelectedIndex < 0) docF.SelectedIndex = 0;
            if (sheetF.SelectedIndex < 0) sheetF.SelectedIndex = 0;
            if (slideF.SelectedIndex < 0) slideF.SelectedIndex = 0;
            exportRo.Checked = p.ExportReadOnly;
            docF.Enabled = sheetF.Enabled = slideF.Enabled = exportRo.Enabled = exportOn.Enabled && exportOn.Checked;
        }
        pairs.SelectedIndexChanged += (_, _) => Load();
        if (pairs.Items.Count > 0) pairs.SelectedIndex = 0; else Load();

        save.Click += (_, _) =>
        {
            if (pairs.SelectedIndex < 0) return;
            var p = _config.SyncPairs[pairs.SelectedIndex];
            var resolution = Enum.Parse<ConflictResolution>(conflict.SelectedItem as string ?? nameof(ConflictResolution.LastWriteWins));
            var newMode = mode.SelectedIndex == 1 ? SyncMode.BackupOnly : SyncMode.TwoWay;
            var modeChanged = PairSettingsEditor.ApplyMode(p, newMode, keepDeleted.Checked);
            var newMax = (long)maxSize.Value * 1024 * 1024;
            if (p.MaxFileSizeBytes != newMax) { p.MaxFileSizeBytes = newMax; modeChanged = true; }
            if (!p.Encrypted)
            {
                string Sel(ComboBox c, string cur) => c.SelectedItem as string ?? cur;
                var (d, s, sl) = (Sel(docF, p.DocsExportFormat), Sel(sheetF, p.SheetsExportFormat), Sel(slideF, p.SlidesExportFormat));
                if (p.ExportGoogleDocs != exportOn.Checked || p.DocsExportFormat != d || p.SheetsExportFormat != s || p.SlidesExportFormat != sl || p.ExportReadOnly != exportRo.Checked)
                {
                    p.ExportGoogleDocs = exportOn.Checked; p.DocsExportFormat = d; p.SheetsExportFormat = s; p.SlidesExportFormat = sl; p.ExportReadOnly = exportRo.Checked;
                    modeChanged = true;
                }
            }
            if (PairSettingsEditor.Apply(p, resolution, excludes.Text, paused.Checked) | modeChanged)
            {
                _host.SaveConfig(_config);
                MessageBox.Show(this, "Saved. SyncIt applies the change right away.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            var index = pairs.SelectedIndex;
            pairs.Items[index] = PairSettingsEditor.DescribePair(p, _config);
        };
        addFolder.Click += (_, _) => { _host.AddFolder(); _config = _host.LoadConfig(); RefreshPage("Folders"); };
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
            RefreshPage("Appearance");
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

        Add(new Label { Text = "Sync schedule", Font = Theme.Body(11f, FontStyle.Bold), AutoSize = true }, ref y, 32);
        var schedOn = new CheckBox { Text = "Only sync during active hours", AutoSize = true, Checked = _config.ActiveHoursEnabled };
        Add(schedOn, ref y, 32);
        var from = new NumericUpDown { Minimum = 0, Maximum = 23, Width = 70, Value = Math.Clamp(_config.ActiveHoursStart, 0, 23) };
        var to = new NumericUpDown { Minimum = 0, Maximum = 23, Width = 70, Value = Math.Clamp(_config.ActiveHoursEnd, 0, 23) };
        var fromL = new Label { Text = "From (hour)", AutoSize = true };
        var toL = new Label { Text = "to (hour)", AutoSize = true };
        fromL.Location = new Point(0, y + 4); from.Location = new Point(90, y); toL.Location = new Point(180, y + 4); to.Location = new Point(250, y);
        _page.Controls.Add(fromL); _page.Controls.Add(from); _page.Controls.Add(toL); _page.Controls.Add(to);
        y += 34;
        Add(Hint("Outside these hours SyncIt pauses and catches up when the window opens. An end before the start wraps past midnight (e.g. 22 to 6)."), ref y, 40);
        void SchedEnable() => from.Enabled = to.Enabled = schedOn.Checked;
        schedOn.CheckedChanged += (_, _) => SchedEnable(); SchedEnable();

        Add(new Label { Text = "Notifications", Font = Theme.Body(11f, FontStyle.Bold), AutoSize = true }, ref y, 32);
        var notifyConflicts = new CheckBox { Text = "Notify me when a conflict is resolved", AutoSize = true, Checked = _config.NotifyConflicts };
        Add(notifyConflicts, ref y, 30);
        var notifyDeletes = new CheckBox { Text = "Notify me about deletions and mass-deletion protection", AutoSize = true, Checked = _config.NotifyDeletions };
        Add(notifyDeletes, ref y, 40);

        Add(new Label { Text = "Experimental", Font = Theme.Body(11f, FontStyle.Bold), AutoSize = true }, ref y, 32);
        var experimental = new CheckBox { Text = "Enable experimental features (beta): encrypted backup folders", AutoSize = true, Checked = _config.ExperimentalFeatures };
        Add(experimental, ref y, 30);
        Add(Hint("Encrypted folders are a one-way, client-side encrypted backup (Tools menu). Beta: keep another copy of important data."), ref y, 40);

        var save = Btn("Save", primary: true);
        save.Click += (_, _) =>
        {
            _config.ActiveHoursEnabled = schedOn.Checked;
            _config.ActiveHoursStart = (int)from.Value;
            _config.ActiveHoursEnd = (int)to.Value;
            _config.NotifyConflicts = notifyConflicts.Checked;
            _config.NotifyDeletions = notifyDeletes.Checked;
            _config.ExperimentalFeatures = experimental.Checked;
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
