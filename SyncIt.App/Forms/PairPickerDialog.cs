using System.Runtime.Versioning;
using SyncIt.Core.Models;

namespace SyncIt.App.Forms;

/// <summary>
/// Simple dialog that lets the user choose which sync pair to configure when multiple pairs exist.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PairPickerDialog : Form
{
    private readonly ListBox _listBox;
    public SyncPair? SelectedPair { get; private set; }

    public PairPickerDialog(List<SyncPair> pairs)
    {
        Text            = "Select Sync Pair";
        Size            = new Size(380, 240);
        MinimumSize     = new Size(300, 200);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;

        var label = new Label
        {
            Text    = "Which sync pair do you want to configure?",
            Dock    = DockStyle.Top,
            Height  = 30,
            Padding = new Padding(0, 6, 0, 0),
        };

        _listBox = new ListBox
        {
            Dock          = DockStyle.Fill,
            SelectionMode = SelectionMode.One,
        };

        foreach (var pair in pairs)
            _listBox.Items.Add(new PairItem(pair));

        if (_listBox.Items.Count > 0)
            _listBox.SelectedIndex = 0;

        _listBox.DoubleClick += OnOk;

        var ok = new Button
        {
            Text         = "OK",
            DialogResult = DialogResult.OK,
            Dock         = DockStyle.Bottom,
            Height       = 30,
        };
        ok.Click += OnOk;

        var cancel = new Button
        {
            Text         = "Cancel",
            DialogResult = DialogResult.Cancel,
            Dock         = DockStyle.Bottom,
            Height       = 30,
        };

        Controls.Add(_listBox);
        Controls.Add(label);
        Controls.Add(ok);
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (_listBox.SelectedItem is PairItem item)
            SelectedPair = item.Pair;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed class PairItem(SyncPair pair)
    {
        public SyncPair Pair { get; } = pair;
        public override string ToString() =>
            string.IsNullOrEmpty(Pair.RemoteFolderName) ? Pair.Id : Pair.RemoteFolderName;
    }
    // Fluent theme (see Theme.cs): remaps colors/fonts by role, dark mode, rounded window corners.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);
    }
}
