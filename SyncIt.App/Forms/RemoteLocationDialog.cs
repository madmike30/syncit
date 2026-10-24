using System.Runtime.Versioning;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>Add folder, step 2: choose the Google account and the drive (My Drive or a shared drive).</summary>
[SupportedOSPlatform("windows")]
internal sealed class RemoteLocationDialog : Form
{
    private readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly ComboBox _location = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Func<string, IGoogleDriveService?> _driveFor;
    private readonly List<GoogleAccount> _accounts;

    public string? AccountId { get; private set; }
    public SharedDriveInfo? SharedDrive { get; private set; }

    public RemoteLocationDialog(IEnumerable<GoogleAccount> accounts, Func<string, IGoogleDriveService?> driveFor)
    {
        _accounts = accounts.ToList();
        _driveFor = driveFor;
        Text = "Add folder – Google Drive location";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 250);
        Font = Theme.Body(10f);

        var title = new Label { Text = "Sync with", Font = Theme.Display(14f), AutoSize = true, Location = new Point(24, 18) };
        var accLabel = new Label { Text = "Google account", AutoSize = true, Location = new Point(24, 62) };
        _account.Location = new Point(24, 84);
        var locLabel = new Label { Text = "Drive", AutoSize = true, Location = new Point(24, 120) };
        _location.Location = new Point(24, 142);
        _status.Location = new Point(24, 172);
        _status.ForeColor = Theme.SubtleText;

        var ok = new Button { Text = "Next", DialogResult = DialogResult.OK, Size = new Size(96, 32), Location = new Point(184, 202) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(96, 32), Location = new Point(288, 202) };
        Theme.StyleButton(ok, primary: true);
        Theme.StyleButton(cancel, primary: false);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            AccountId = (_account.SelectedItem as AccountItem)?.Account.Id;
            SharedDrive = (_location.SelectedItem as LocationItem)?.Drive;
        };

        Controls.AddRange(new Control[] { title, accLabel, _account, locLabel, _location, _status, ok, cancel });
        foreach (var a in _accounts) _account.Items.Add(new AccountItem(a));
        _account.SelectedIndexChanged += async (_, _) => await LoadLocationsAsync();
        Theme.Apply(this);
        if (_account.Items.Count > 0) _account.SelectedIndex = 0;
    }

    private async Task LoadLocationsAsync()
    {
        _location.Items.Clear();
        _location.Items.Add(new LocationItem(null));
        _location.SelectedIndex = 0;
        if (_account.SelectedItem is not AccountItem item) return;
        var drive = _driveFor(item.Account.Id);
        if (drive is null) { _status.Text = "This account is not signed in."; return; }
        _status.Text = "Loading shared drives…";
        try
        {
            var drives = await drive.ListSharedDrivesAsync();
            foreach (var d in drives) _location.Items.Add(new LocationItem(d));
            _status.Text = drives.Count == 0 ? "No shared drives for this account." : $"{drives.Count} shared drive(s) available.";
        }
        catch (Exception ex) { _status.Text = "Shared drives unavailable: " + ex.Message; }
        finally { (drive as IDisposable)?.Dispose(); }
    }

    private sealed record AccountItem(GoogleAccount Account)
    {
        public override string ToString() => string.IsNullOrEmpty(Account.Email) ? Account.Id : Account.Email;
    }

    private sealed record LocationItem(SharedDriveInfo? Drive)
    {
        public override string ToString() => Drive is null ? "My Drive" : "Shared drive: " + Drive.Name;
    }
}
