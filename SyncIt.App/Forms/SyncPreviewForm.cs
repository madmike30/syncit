using System.Runtime.Versioning;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>Dry-run of a folder's first sync. OK = start syncing.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SyncPreviewForm : Form
{
    public SyncPreviewForm(string title, SyncPreviewResult preview)
    {
        Text = "SyncIt — preview first sync";
        Size = new Size(860, 520); MinimumSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;

        var header = new Label
        {
            Dock = DockStyle.Top, Height = 64, Padding = new Padding(12, 10, 12, 0),
            Text = $"{title}\n{preview.Summary}. Nothing will be deleted on either side.",
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        list.Columns.Add("Action", 120); list.Columns.Add("File", 320); list.Columns.Add("Size", 80, HorizontalAlignment.Right); list.Columns.Add("Note", 300);
        list.BeginUpdate();
        foreach (var i in preview.Items.Take(5000))
            list.Items.Add(new ListViewItem(new[] { Label(i.Action), i.RelativePath, FormatSize(i.SizeBytes), i.Note ?? "" }));
        if (preview.Items.Count > 5000) list.Items.Add(new ListViewItem(new[] { "…", $"{preview.Items.Count - 5000} more", "", "" }));
        list.EndUpdate();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var start = new Button { Text = "Start syncing", DialogResult = DialogResult.OK, AutoSize = true, Tag = "accent" };
        var later = new Button { Text = "Not now", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(start); buttons.Controls.Add(later);
        Controls.Add(list); Controls.Add(header); Controls.Add(buttons);
        AcceptButton = start; CancelButton = later;
    }

    private static string Label(PreviewAction a) => a switch
    {
        PreviewAction.Upload => "↑ Upload",
        PreviewAction.Download => "↓ Download",
        PreviewAction.Match => "= Identical",
        PreviewAction.Conflict => "≠ Conflict",
        PreviewAction.KeepOnDrive => "Stays on Drive",
        _ => "Skipped",
    };

    internal static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.#} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Apply(this); }
}
