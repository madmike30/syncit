using System.Runtime.Versioning;
using SyncIt.Core.Helpers;
using SyncIt.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace SyncIt.App.Forms;

/// <summary>
/// First-run wizard that guides the user through:
///   1. Entering Google OAuth2 clientId and clientSecret
///   2. Saving credentials to Windows Credential Manager
///   3. Completing the browser-based OAuth consent flow
///   4. Optionally configuring the first sync pair
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirstRunForm : Form
{
    private TextBox _clientIdBox   = null!;
    private TextBox _clientSecretBox = null!;
    private Label   _statusLabel   = null!;
    private Button  _authorizeBtn  = null!;

    public FirstRunForm()
    {
        Text            = "SyncIt — First Run Setup";
        Size            = new Size(540, 420);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;

        BuildUI();
    }

    private void BuildUI()
    {
        var layout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            Padding     = new Padding(16),
            RowCount    = 8,
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Header
        var header = new Label
        {
            Text      = "SyncIt — First Run Setup",
            Font      = new Font("Segoe UI", 13, FontStyle.Bold),
            AutoSize  = true,
            Dock      = DockStyle.Fill,
        };
        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);

        // Instructions
        var instructions = new Label
        {
            Text = "You need a Google Cloud project with Drive API enabled.\n" +
                   "1. Go to https://console.cloud.google.com\n" +
                   "2. Create OAuth 2.0 credentials (Desktop app type)\n" +
                   "3. Paste the Client ID and Client Secret below.\n\n" +
                   "Your credentials are stored in Windows Credential Manager — never in plain text.",
            AutoSize    = true,
            MaximumSize = new Size(490, 0),
            Dock        = DockStyle.Fill,
        };
        layout.Controls.Add(instructions, 0, 1);
        layout.SetColumnSpan(instructions, 2);

        // Client ID
        layout.Controls.Add(new Label { Text = "Client ID:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        _clientIdBox = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_clientIdBox, 1, 2);

        // Client Secret
        layout.Controls.Add(new Label { Text = "Client Secret:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        _clientSecretBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        layout.Controls.Add(_clientSecretBox, 1, 3);

        // Status label
        _statusLabel = new Label
        {
            Text      = string.Empty,
            AutoSize  = true,
            ForeColor = Color.DarkBlue,
            Dock      = DockStyle.Fill,
        };
        layout.Controls.Add(_statusLabel, 0, 4);
        layout.SetColumnSpan(_statusLabel, 2);

        // Authorize button
        _authorizeBtn = new Button
        {
            Text      = "Save Credentials & Authorize with Google",
            Dock      = DockStyle.Fill,
            Height    = 40,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        _authorizeBtn.Click += OnAuthorize;
        layout.Controls.Add(_authorizeBtn, 0, 5);
        layout.SetColumnSpan(_authorizeBtn, 2);

        // Done button
        var doneBtn = new Button
        {
            Text         = "Done",
            DialogResult = DialogResult.OK,
            Dock         = DockStyle.Fill,
        };
        layout.Controls.Add(doneBtn, 0, 6);
        layout.SetColumnSpan(doneBtn, 2);

        Controls.Add(layout);
    }

    private async void OnAuthorize(object? sender, EventArgs e)
    {
        var clientId     = _clientIdBox.Text.Trim();
        var clientSecret = _clientSecretBox.Text.Trim();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            SetStatus("Please enter both Client ID and Client Secret.", Color.DarkRed);
            return;
        }

        _authorizeBtn.Enabled = false;
        SetStatus("Saving credentials to Windows Credential Manager...", Color.DarkBlue);

        try
        {
            CredentialHelper.SaveClientCredentials(clientId, clientSecret);
            SetStatus("Credentials saved. Opening browser for Google authorization...", Color.DarkBlue);

            // Load a dummy config to get token path
            var configPath = PathHelper.Expand(@"%APPDATA%\SyncIt\syncit.json");
            var config = new SyncIt.Core.Models.SyncConfig();
            if (File.Exists(configPath))
            {
                try
                {
                    config = System.Text.Json.JsonSerializer.Deserialize<SyncIt.Core.Models.SyncConfig>(
                        File.ReadAllText(configPath),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? config;
                }
                catch { }
            }

            var oauthService = new OAuthService(config, NullLogger<OAuthService>.Instance);
            await oauthService.InitializeAsync();

            SetStatus("✓ Authorization complete! SyncIt is now configured.", Color.DarkGreen);
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", Color.DarkRed);
            _authorizeBtn.Enabled = true;
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text      = text;
        _statusLabel.ForeColor = color;
    }
}
