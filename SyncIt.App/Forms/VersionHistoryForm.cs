using System.Runtime.Versioning;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>Version history of one file + files deleted on Drive for its folder (1.4).</summary>
[SupportedOSPlatform("windows")]
internal sealed class VersionHistoryForm : Form
{
    private readonly VersionHistoryService _svc;
    private readonly SyncPair _pair;
    private readonly string? _relativePath;
    private readonly ListView _versions = NewList();
    private readonly ListView _trash = NewList();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(8, 6, 8, 0) };
    private IReadOnlyList<DriveRevisionInfo> _revs = Array.Empty<DriveRevisionInfo>();
    private IReadOnlyList<TrashedItem> _trashed = Array.Empty<TrashedItem>();

    public VersionHistoryForm(VersionHistoryService svc, SyncPair pair, string? relativePath)
    {
        _svc = svc; _pair = pair; _relativePath = relativePath;
        Text = relativePath is null ? "SyncIt — deleted files" : $"SyncIt — history of {Path.GetFileName(relativePath)}";
        Size = new Size(760, 480); MinimumSize = new Size(520, 340); StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var vTab = new TabPage("Versions");
        var tTab = new TabPage("Deleted on Drive");
        _versions.Columns.Add("Saved", 200); _versions.Columns.Add("Size", 100, HorizontalAlignment.Right); _versions.Columns.Add("", 160);
        _trash.Columns.Add("File", 380); _trash.Columns.Add("Deleted", 170); _trash.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _trash.CheckBoxes = true;
        var restoreV = new Button { Text = "Restore this version", AutoSize = true, Dock = DockStyle.Bottom, Tag = "accent" };
        var restoreT = new Button { Text = "Restore checked files", AutoSize = true, Dock = DockStyle.Bottom, Tag = "accent" };
        restoreV.Click += async (_, _) => await RestoreVersionAsync();
        restoreT.Click += async (_, _) => await RestoreTrashAsync();
        vTab.Controls.Add(_versions); vTab.Controls.Add(restoreV);
        tTab.Controls.Add(_trash); tTab.Controls.Add(restoreT);
        if (relativePath is not null) tabs.TabPages.Add(vTab);
        tabs.TabPages.Add(tTab);
        Controls.Add(tabs); Controls.Add(_status);
        Shown += async (_, _) => { if (AutoLoad) await LoadAsync(); };
    }

    private static ListView NewList() => new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };

    private async Task LoadAsync()
    {
        _status.Text = "Loading from Google Drive…";
        try
        {
            Populate(_relativePath is not null ? await _svc.GetRevisionsAsync(_pair, _relativePath) : Array.Empty<DriveRevisionInfo>(),
                     await _svc.ListTrashAsync(_pair));
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    internal bool AutoLoad { get; set; } = true;

    /// <summary>Fills both lists (also used by the screenshot harness with example data).</summary>
    internal void Populate(IReadOnlyList<DriveRevisionInfo> revs, IReadOnlyList<TrashedItem> trashed)
    {
        {
            _revs = revs;
            {
                _versions.Items.Clear();
                for (int i = _revs.Count - 1; i >= 0; i--)
                {
                    var r = _revs[i];
                    _versions.Items.Add(new ListViewItem(new[] { r.ModifiedUtc.ToLocalTime().ToString("g"), SyncPreviewForm.FormatSize(r.SizeBytes),
                        i == _revs.Count - 1 ? "current" : r.KeepForever ? "kept forever" : "" }) { Tag = r });
                }
            }
            _trashed = trashed;
            _trash.Items.Clear();
            foreach (var t in _trashed)
                _trash.Items.Add(new ListViewItem(new[] { t.RelativePath + (t.IsFolder ? "\\" : ""), t.TrashedUtc?.ToLocalTime().ToString("g") ?? "", t.IsFolder ? "" : SyncPreviewForm.FormatSize(t.SizeBytes) }) { Tag = t });
            _status.Text = $"{_revs.Count} version(s), {_trashed.Count} deleted item(s) in this folder's Drive trash. Drive keeps old versions ~30 days (or 100 versions).";
        }
    }

    private async Task RestoreVersionAsync()
    {
        if (_versions.SelectedItems.Count == 0 || _versions.SelectedItems[0].Tag is not DriveRevisionInfo rev || _relativePath is null) return;
        if (MessageBox.Show($"Replace the file on this PC with the version from {rev.ModifiedUtc.ToLocalTime():g}?\n\n" +
                "The current copy is moved to the SyncIt trash first, so nothing is lost. The restored version then syncs to Drive as the newest version.",
                "Restore version", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            _status.Text = "Restoring…";
            var saved = await _svc.RestoreRevisionAsync(_pair, _relativePath, rev);
            _status.Text = saved is null ? "Restored." : $"Restored. Previous copy saved in the SyncIt trash.";
        }
        catch (Exception ex) { _status.Text = "Restore failed: " + ex.Message; }
    }

    private async Task RestoreTrashAsync()
    {
        var picks = _trash.CheckedItems.Cast<ListViewItem>().Select(i => (TrashedItem)i.Tag!).ToList();
        if (picks.Count == 0) { _status.Text = "Check the files to restore first."; return; }
        int ok = 0;
        foreach (var t in picks)
        {
            try { await _svc.RestoreFromTrashAsync(t); ok++; }
            catch (Exception ex) { _status.Text = $"{t.RelativePath}: {ex.Message}"; }
        }
        _status.Text = $"{ok} item(s) taken out of the Drive trash; they will sync back to this PC.";
        await LoadAsync();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Apply(this); }
}
