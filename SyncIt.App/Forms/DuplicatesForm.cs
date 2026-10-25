using System.Runtime.Versioning;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>Duplicate finder (1.4): scan, review, trash only what the user checks.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DuplicatesForm : Form
{
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(8, 6, 8, 0) };
    private readonly Func<SyncPair, IGoogleDriveService?> _driveFor;
    private readonly Func<SyncPair, Task<ISet<string>>> _linkedIds;
    private IReadOnlyList<DuplicateGroup> _groups = Array.Empty<DuplicateGroup>();
    private IGoogleDriveService? _drive;

    public DuplicatesForm(IEnumerable<SyncPair> pairs, SyncConfig config, Func<SyncPair, IGoogleDriveService?> driveFor, Func<SyncPair, Task<ISet<string>>> linkedIds)
    {
        _driveFor = driveFor; _linkedIds = linkedIds;
        Text = "SyncIt — find duplicates on Drive";
        Size = new Size(860, 540); MinimumSize = new Size(600, 360); StartPosition = FormStartPosition.CenterScreen;
        foreach (var p in pairs.Where(p => !p.Encrypted)) _target.Items.Add(new PairItem(p, config));
        if (_target.Items.Count > 0) _target.SelectedIndex = 0;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
        var scan = new Button { Text = "Scan", AutoSize = true, Tag = "accent" };
        scan.Click += async (_, _) => await ScanAsync();
        top.Controls.Add(new Label { Text = "Folder:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(_target); top.Controls.Add(scan);

        _list.Columns.Add("File on Drive", 420); _list.Columns.Add("Created", 150); _list.Columns.Add("Size", 90, HorizontalAlignment.Right); _list.Columns.Add("", 160);
        _list.ItemCheck += (_, e) => { if (_list.Items[e.Index].Tag is DuplicateCopy { Protected: true }) e.NewValue = CheckState.Unchecked; };

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var trash = new Button { Text = "Move checked to Drive trash…", AutoSize = true };
        trash.Click += async (_, _) => await TrashAsync();
        bottom.Controls.Add(trash);
        Controls.Add(_list); Controls.Add(top); Controls.Add(bottom); Controls.Add(_status);
        _status.Text = "Finds files with the same name AND identical content (MD5) in the same Drive folder. Nothing is removed unless you check it.";
    }

    private async Task ScanAsync()
    {
        if (_target.SelectedItem is not PairItem item || (_drive = _driveFor(item.Pair)) is null) { _status.Text = "That account is not signed in."; return; }
        _status.Text = "Scanning Drive…"; _list.Items.Clear();
        try
        {
            var tree = await _drive.ListTreeAsync(item.Pair.RemoteFolderId);
            Populate(DuplicateFinder.Find(tree, await _linkedIds(item.Pair)));
        }
        catch (Exception ex) { _status.Text = "Scan failed: " + ex.Message; }
    }

    /// <summary>Shows scan results (also used by the screenshot harness with example data).</summary>
    internal void Populate(IReadOnlyList<DuplicateGroup> groups)
    {
        _groups = groups; _list.Items.Clear(); _list.Groups.Clear();
        {
            _list.BeginUpdate();
            foreach (var g in _groups)
            {
                var grp = new ListViewGroup($"{(g.FolderPath.Length == 0 ? "" : g.FolderPath + "/")}{g.Name} — {g.Copies.Count} copies");
                _list.Groups.Add(grp);
                foreach (var c in g.Copies)
                    _list.Items.Add(new ListViewItem(new[] { c.RelativePath, c.CreatedUtc?.ToLocalTime().ToString("g") ?? "", SyncPreviewForm.FormatSize(c.SizeBytes), c.ProtectedReason ?? "" }, grp)
                        { Tag = c, Checked = false, ForeColor = c.Protected ? SystemColors.GrayText : SystemColors.WindowText });
            }
            _list.EndUpdate();
            _status.Text = _groups.Count == 0 ? "No duplicates found." :
                $"{_groups.Count} duplicate group(s), {SyncPreviewForm.FormatSize(_groups.Sum(g => g.WastedBytes))} in extra copies. Check the copies you want to remove.";
        }
    }

    private async Task TrashAsync()
    {
        if (_drive is null) return;
        var ids = _list.CheckedItems.Cast<ListViewItem>().Select(i => ((DuplicateCopy)i.Tag!).DriveFileId).ToHashSet();
        var picks = DuplicateFinder.SafeToTrash(_groups, ids);
        if (picks.Count == 0) { _status.Text = "Nothing checked."; return; }
        if (MessageBox.Show($"Move {picks.Count} duplicate copy(ies) to the Google Drive trash?\nYou can restore them from the Drive trash for 30 days.",
                "Remove duplicates", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        int ok = 0;
        foreach (var c in picks) { try { await _drive.TrashFileAsync(c.DriveFileId); ok++; } catch (Exception ex) { _status.Text = ex.Message; } }
        _status.Text = $"{ok} copy(ies) moved to the Drive trash.";
        await ScanAsync();
    }

    private sealed class PairItem(SyncPair pair, SyncConfig config)
    {
        public SyncPair Pair { get; } = pair;
        public override string ToString() => PairSettingsEditor.DescribePair(Pair, config);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Apply(this); }
}
