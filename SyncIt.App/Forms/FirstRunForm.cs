using System.Diagnostics;
using System.Runtime.Versioning;
using SyncIt.Core.Helpers;
using SyncIt.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace SyncIt.App.Forms;

/// <summary>
/// Step-by-step setup wizard for non-technical users.
/// Guides through: Google Cloud Console → Create credentials → Paste → Authorize.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirstRunForm : Form
{
    private static readonly Color BgColor    = Color.FromArgb(250, 251, 253);
    private static readonly Color CardBg     = Color.White;
    private static readonly Color CardBorder = Color.FromArgb(225, 228, 232);
    private static readonly Color AccentBlue = Color.FromArgb(0, 102, 204);
    private static readonly Color AccentGreen = Color.FromArgb(16, 163, 74);
    private static readonly Color SubtleText = Color.FromArgb(100, 110, 120);

    private Panel _stepsPanel = null!;
    private Panel _step1Panel = null!;
    private Panel _step2Panel = null!;
    private Panel _step3Panel = null!;
    private TextBox _clientIdBox = null!;
    private TextBox _clientSecretBox = null!;
    private Label _statusLabel = null!;
    private Button _nextBtn = null!;
    private Button _backBtn = null!;
    private int _currentStep = 1;

    public FirstRunForm()
    {
        Text            = "SyncIt Setup";
        Size            = new Size(580, 520);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = BgColor;
        Font            = new Font("Segoe UI", 9.5f);

        BuildUI();
        ShowStep(1);
    }

    private void BuildUI()
    {
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28) };
        Controls.Add(root);

        // Title
        var title = new Label
        {
            Text      = "Welcome to SyncIt",
            Font      = new Font("Segoe UI Semibold", 18f),
            ForeColor = Color.FromArgb(30, 30, 30),
            AutoSize  = true,
            Location  = new Point(0, 0),
        };
        root.Controls.Add(title);

        var subtitle = new Label
        {
            Text      = "Let's connect your Google Drive. This takes about 2 minutes.",
            Font      = new Font("Segoe UI", 9.5f),
            ForeColor = SubtleText,
            AutoSize  = true,
            Location  = new Point(0, 36),
        };
        root.Controls.Add(subtitle);

        // Step indicator
        var stepIndicator = new Label
        {
            Name      = "stepIndicator",
            Text      = "Step 1 of 3",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = AccentBlue,
            AutoSize  = true,
            Location  = new Point(0, 64),
        };
        root.Controls.Add(stepIndicator);

        // Steps container
        _stepsPanel = new Panel
        {
            Location = new Point(0, 90),
            Size     = new Size(510, 300),
        };
        root.Controls.Add(_stepsPanel);

        BuildStep1();
        BuildStep2();
        BuildStep3();

        // Status label
        _statusLabel = new Label
        {
            Text      = "",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = AccentBlue,
            AutoSize  = true,
            MaximumSize = new Size(510, 40),
            Location  = new Point(0, 394),
        };
        root.Controls.Add(_statusLabel);

        // Navigation buttons
        _backBtn = new Button
        {
            Text      = "Back",
            Size      = new Size(100, 36),
            Location  = new Point(300, 426),
            BackColor = CardBg,
            ForeColor = Color.FromArgb(60, 60, 60),
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 10f),
            Cursor    = Cursors.Hand,
        };
        _backBtn.FlatAppearance.BorderColor = CardBorder;
        _backBtn.Click += (_, _) => GoBack();
        root.Controls.Add(_backBtn);

        _nextBtn = new Button
        {
            Text      = "Next",
            Size      = new Size(100, 36),
            Location  = new Point(408, 426),
            BackColor = AccentBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI Semibold", 10f),
            Cursor    = Cursors.Hand,
        };
        _nextBtn.FlatAppearance.BorderSize = 0;
        _nextBtn.Click += OnNext;
        root.Controls.Add(_nextBtn);
    }

    private void BuildStep1()
    {
        _step1Panel = new Panel { Location = Point.Empty, Size = _stepsPanel.Size };

        var card = MakeCard(_step1Panel, 0, 280);

        int y = 16;
        AddLabel(card, "First, you need to create a free Google API project.", 10f, FontStyle.Bold, 16, y);
        y += 30;

        AddLabel(card, "This is a one-time setup that lets SyncIt access your Google Drive.", 9f, FontStyle.Regular, 16, y, SubtleText);
        y += 30;

        var steps = new[]
        {
            ("1.", "Click the button below to open Google Cloud Console"),
            ("2.", "Sign in with your Google account"),
            ("3.", "Click \"Select a project\" at the top, then \"New Project\""),
            ("4.", "Name it anything (e.g. \"SyncIt\") and click Create"),
            ("5.", "In the left menu, go to \"APIs & Services\" > \"Library\""),
            ("6.", "Search for \"Google Drive API\" and click Enable"),
        };

        foreach (var (num, text) in steps)
        {
            var numLabel = new Label
            {
                Text = num, Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = AccentBlue, AutoSize = true,
                Location = new Point(20, y),
            };
            card.Controls.Add(numLabel);

            var textLabel = new Label
            {
                Text = text, Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(40, 40, 40), AutoSize = true,
                MaximumSize = new Size(430, 0),
                Location = new Point(44, y),
            };
            card.Controls.Add(textLabel);
            y += 24;
        }

        y += 12;
        var openBtn = new Button
        {
            Text      = "Open Google Cloud Console",
            Size      = new Size(240, 34),
            Location  = new Point(16, y),
            BackColor = AccentBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
            Font      = new Font("Segoe UI Semibold", 9.5f),
        };
        openBtn.FlatAppearance.BorderSize = 0;
        openBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("https://console.cloud.google.com") { UseShellExecute = true });
        card.Controls.Add(openBtn);

        _stepsPanel.Controls.Add(_step1Panel);
    }

    private void BuildStep2()
    {
        _step2Panel = new Panel { Location = Point.Empty, Size = _stepsPanel.Size, Visible = false };

        var card = MakeCard(_step2Panel, 0, 280);

        int y = 16;
        AddLabel(card, "Now create OAuth credentials for SyncIt.", 10f, FontStyle.Bold, 16, y);
        y += 30;

        var steps = new[]
        {
            ("1.", "In Google Cloud Console, go to \"APIs & Services\" > \"Credentials\""),
            ("2.", "Click \"+ Create Credentials\" > \"OAuth client ID\""),
            ("3.", "If asked to configure consent screen, choose \"External\", fill in app name"),
            ("4.", "For Application type, choose \"Desktop app\""),
            ("5.", "Click Create — you'll see a Client ID and Client Secret"),
            ("6.", "Copy and paste them below:"),
        };

        foreach (var (num, text) in steps)
        {
            var numLabel = new Label
            {
                Text = num, Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = AccentBlue, AutoSize = true,
                Location = new Point(20, y),
            };
            card.Controls.Add(numLabel);

            var textLabel = new Label
            {
                Text = text, Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(40, 40, 40), AutoSize = true,
                MaximumSize = new Size(430, 0),
                Location = new Point(44, y),
            };
            card.Controls.Add(textLabel);
            y += 24;
        }

        y += 8;
        AddLabel(card, "Client ID", 9f, FontStyle.Regular, 20, y, SubtleText);
        y += 18;
        _clientIdBox = new TextBox
        {
            Location = new Point(20, y),
            Size     = new Size(440, 26),
            Font     = new Font("Segoe UI", 9.5f),
            PlaceholderText = "Paste your Client ID here",
        };
        card.Controls.Add(_clientIdBox);

        y += 32;
        AddLabel(card, "Client Secret", 9f, FontStyle.Regular, 20, y, SubtleText);
        y += 18;
        _clientSecretBox = new TextBox
        {
            Location = new Point(20, y),
            Size     = new Size(440, 26),
            Font     = new Font("Segoe UI", 9.5f),
            UseSystemPasswordChar = true,
            PlaceholderText = "Paste your Client Secret here",
        };
        card.Controls.Add(_clientSecretBox);

        _stepsPanel.Controls.Add(_step2Panel);
    }

    private void BuildStep3()
    {
        _step3Panel = new Panel { Location = Point.Empty, Size = _stepsPanel.Size, Visible = false };

        var card = MakeCard(_step3Panel, 0, 200);

        int y = 16;
        AddLabel(card, "Almost done! Sign in with Google.", 10f, FontStyle.Bold, 16, y);
        y += 30;

        AddLabel(card, "When you click \"Connect\", your browser will open to a Google sign-in page.", 9.5f, FontStyle.Regular, 16, y, Color.FromArgb(40, 40, 40), 440);
        y += 40;

        AddLabel(card, "Sign in with the Google account whose Drive you want to sync.", 9.5f, FontStyle.Regular, 16, y, Color.FromArgb(40, 40, 40), 440);
        y += 40;

        AddLabel(card, "If you see a \"This app isn't verified\" warning, click \"Advanced\" then \"Go to SyncIt (unsafe)\" — this is normal for personal projects.", 9f, FontStyle.Regular, 16, y, SubtleText, 440);

        _stepsPanel.Controls.Add(_step3Panel);
    }

    private void ShowStep(int step)
    {
        _currentStep = step;
        _step1Panel.Visible = step == 1;
        _step2Panel.Visible = step == 2;
        _step3Panel.Visible = step == 3;

        _backBtn.Visible = step > 1;

        _nextBtn.Text = step switch
        {
            1 => "Next",
            2 => "Next",
            3 => "Connect",
            _ => "Done",
        };

        var indicator = Controls.Find("stepIndicator", true).FirstOrDefault() as Label;
        if (indicator is not null)
            indicator.Text = $"Step {step} of 3";

        _statusLabel.Text = "";
    }

    private void GoBack()
    {
        if (_currentStep > 1) ShowStep(_currentStep - 1);
    }

    private async void OnNext(object? sender, EventArgs e)
    {
        if (_currentStep == 1)
        {
            ShowStep(2);
        }
        else if (_currentStep == 2)
        {
            var clientId     = _clientIdBox.Text.Trim();
            var clientSecret = _clientSecretBox.Text.Trim();

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                SetStatus("Please paste both the Client ID and Client Secret from Google Cloud Console.", Color.FromArgb(215, 40, 40));
                return;
            }

            // Save credentials before moving to step 3
            try
            {
                CredentialHelper.SaveClientCredentials(clientId, clientSecret);
                ShowStep(3);
            }
            catch (Exception ex)
            {
                SetStatus($"Could not save credentials: {ex.Message}", Color.FromArgb(215, 40, 40));
            }
        }
        else if (_currentStep == 3)
        {
            _nextBtn.Enabled = false;
            _backBtn.Enabled = false;
            SetStatus("Opening your browser... Please sign in with Google.", AccentBlue);

            try
            {
                var configPath = PathHelper.Expand(@"%APPDATA%\SyncIt\syncit.json");
                var config = new SyncIt.Core.Models.SyncConfig();
                if (File.Exists(configPath))
                {
                    try
                    {
                        config = System.Text.Json.JsonSerializer.Deserialize<SyncIt.Core.Models.SyncConfig>(
                            File.ReadAllText(configPath),
                            new System.Text.Json.JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true,
                                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                                AllowTrailingCommas = true,
                            }) ?? config;
                    }
                    catch { }
                }

                var oauthService = new OAuthService(config, NullLogger<OAuthService>.Instance);
                await oauthService.InitializeAsync();

                SetStatus("Connected successfully! You can close this window.", AccentGreen);
                _nextBtn.Text = "Done";
                _nextBtn.Enabled = true;
                _nextBtn.Click -= OnNext;
                _nextBtn.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
            }
            catch (Exception ex)
            {
                SetStatus($"Connection failed: {ex.Message}\n\nPlease try again.", Color.FromArgb(215, 40, 40));
                _nextBtn.Enabled = true;
                _backBtn.Enabled = true;
            }
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text      = text;
        _statusLabel.ForeColor = color;
        _statusLabel.Refresh();
    }

    // ── UI Helpers ──

    private static Panel MakeCard(Panel parent, int y, int height)
    {
        var card = new Panel
        {
            Location  = new Point(0, y),
            Size      = new Size(510, height),
            BackColor = CardBg,
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(CardBorder);
            var r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            DrawRoundedRect(e.Graphics, pen, r, 6);
        };
        parent.Controls.Add(card);
        return card;
    }

    private static void AddLabel(Panel parent, string text, float fontSize, FontStyle style, int x, int y, Color? color = null, int maxWidth = 0)
    {
        var lbl = new Label
        {
            Text      = text,
            Font      = new Font("Segoe UI", fontSize, style),
            ForeColor = color ?? Color.FromArgb(30, 30, 30),
            AutoSize  = true,
            Location  = new Point(x, y),
        };
        if (maxWidth > 0) lbl.MaximumSize = new Size(maxWidth, 0);
        parent.Controls.Add(lbl);
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
