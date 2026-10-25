using System.Runtime.Versioning;

namespace SyncIt.App.Forms;

/// <summary>Passphrase for a new encrypted backup folder (Experimental).</summary>
[SupportedOSPlatform("windows")]
internal sealed class PassphraseDialog : Form
{
    private readonly TextBox _a = new() { UseSystemPasswordChar = true, Width = 360 };
    private readonly TextBox _b = new() { UseSystemPasswordChar = true, Width = 360 };
    private readonly CheckBox _ack = new() { AutoSize = true, Text = "I understand: if I lose this passphrase, my encrypted backup cannot be recovered." };
    public string Passphrase => _a.Text;

    public PassphraseDialog(bool confirm = true)
    {
        Text = "SyncIt — encrypted folder (beta)";
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16), WrapContents = false };
        p.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(460, 0), Text =
            "BETA. Files are encrypted on this PC (AES-256-GCM, names too) before upload, so Google can't read them. " +
            "This is a one-way backup: nothing on this PC is ever changed or deleted. Restore any time from the tray menu (Tools) into an empty folder." });
        p.Controls.Add(new Label { AutoSize = true, Text = "Passphrase (10+ characters):" }); p.Controls.Add(_a);
        if (confirm) { p.Controls.Add(new Label { AutoSize = true, Text = "Repeat:" }); p.Controls.Add(_b); p.Controls.Add(_ack); }
        var ok = new Button { Text = "OK", AutoSize = true, Tag = "accent" };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (_a.Text.Length < 10) { MessageBox.Show("Use at least 10 characters."); return; }
            if (confirm && _a.Text != _b.Text) { MessageBox.Show("The two passphrases differ."); return; }
            if (confirm && !_ack.Checked) { MessageBox.Show("Please confirm you have stored the passphrase safely."); return; }
            DialogResult = DialogResult.OK;
        };
        var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.Add(ok); row.Controls.Add(cancel);
        p.Controls.Add(row); Controls.Add(p); AcceptButton = ok; CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Apply(this); }
}
