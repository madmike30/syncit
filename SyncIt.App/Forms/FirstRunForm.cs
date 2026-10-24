using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using SyncIt.Core.Helpers;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>
/// First-run setup, written for non-technical users.
/// Default: one "Sign in with Google" button using SyncIt's built-in client (when the build has one).
/// Advanced: a guided wizard to create and import the user's own Google OAuth Desktop client.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirstRunForm : Form
{
    private static readonly Color BgColor     = Color.FromArgb(250, 251, 253);
    private static readonly Color CardBg      = Color.White;
    private static readonly Color CardBorder  = Color.FromArgb(225, 228, 232);
    private static readonly Color AccentBlue  = Color.FromArgb(0, 102, 204);
    private static readonly Color AccentGreen = Color.FromArgb(16, 163, 74);
    private static readonly Color AccentRed   = Color.FromArgb(200, 40, 40);
    private static readonly Color SubtleText  = Color.FromArgb(95, 105, 115);
    private static readonly Color BodyText    = Color.FromArgb(35, 35, 35);

    private const string GuideUrl = "https://github.com/madmike30/syncit/blob/main/docs/SETUP-GOOGLE-OAUTH.md";
    private const int ContentWidth = 560;

    private readonly bool _hasBuiltIn = OAuthClientResolver.HasBuiltInClient;
    private Panel _welcomePage = null!;
    private Panel _advancedPage = null!;
    private Label _welcomeStatus = null!;
    private LinkLabel _welcomeFallbackLink = null!;
    private Button _signInBtn = null!;
    private TextBox _projectIdBox = null!;
    private TextBox _clientIdBox = null!;
    private TextBox _clientSecretBox = null!;
    private Label _advancedStatus = null!;
    private Button _advancedSignInBtn = null!;

    public FirstRunForm()
    {
        Text            = "SyncIt Setup";
        ClientSize      = new Size(620, 640);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = BgColor;
        Font            = new Font("Segoe UI", 9.5f);

        _welcomePage  = BuildWelcomePage();
        _advancedPage = BuildAdvancedPage();
        Controls.Add(_welcomePage);
        Controls.Add(_advancedPage);
        ShowPage(advanced: !_hasBuiltIn);
    }

    private void ShowPage(bool advanced)
    {
        _welcomePage.Visible  = !advanced;
        _advancedPage.Visible = advanced;
    }

    // ── Page 1: simple sign-in ─────────────────────────────────────────────────

    private Panel BuildWelcomePage()
    {
        var page = NewPage();
        var y = 0;
        y = AddText(page, "Welcome to SyncIt", y, 18f, FontStyle.Regular, BodyText, "Segoe UI Semibold") + 6;
        y = AddText(page, "SyncIt keeps a folder on this PC and a folder in your Google Drive the same, automatically.", y, 10.5f) + 18;

        _signInBtn = new Button
        {
            Text = "Sign in with Google", Size = new Size(300, 48), Location = new Point(0, y),
            BackColor = AccentBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 12f), Cursor = Cursors.Hand,
        };
        _signInBtn.FlatAppearance.BorderSize = 0;
        _signInBtn.Click += async (_, _) => await SignInAsync(builtIn: true);
        page.Controls.Add(_signInBtn);
        y += 60;

        _welcomeStatus = new Label { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Location = new Point(0, y), Font = new Font("Segoe UI", 9.5f) };
        page.Controls.Add(_welcomeStatus);
        _welcomeFallbackLink = new LinkLabel { Text = "Advanced: use my own Google client", AutoSize = true, Visible = false, Font = new Font("Segoe UI Semibold", 9.5f) };
        _welcomeFallbackLink.LinkClicked += (_, _) => ShowPage(advanced: true);
        page.Controls.Add(_welcomeFallbackLink);
        y += 70;

        var card = Card(page, y, 300);
        var cy = 14;
        cy = AddText(card, "What happens next", cy, 10.5f, FontStyle.Bold, BodyText, x: 16) + 6;
        cy = AddText(card, "1. Your web browser opens a Google page. Pick the Google account whose Drive you want to sync.", cy, 9.5f, x: 16, width: ContentWidth - 40) + 4;
        cy = AddText(card, "2. Google may say \"Google hasn't verified this app\". SyncIt is a free, open-source app that hasn't finished Google's review yet. Click \"Advanced\", then \"Go to SyncIt (unsafe)\".", cy, 9.5f, x: 16, width: ContentWidth - 40) + 4;
        cy = AddText(card, "3. Click \"Continue\" to allow access, then come back here.", cy, 9.5f, x: 16, width: ContentWidth - 40) + 12;
        cy = AddText(card, "What SyncIt asks for, and why", cy, 10.5f, FontStyle.Bold, BodyText, x: 16) + 6;
        cy = AddText(card, "SyncIt asks to see and edit your Google Drive files, because it has to read and update the Drive folder you choose to sync. Your files go directly between this PC and Google. There is no SyncIt server, and SyncIt never sees your Google password. You can remove access anytime at myaccount.google.com/permissions.", cy, 9.5f, SubtleText, x: 16, width: ContentWidth - 40);

        var adv = new LinkLabel { Text = "Advanced: use my own Google client", AutoSize = true, Location = new Point(0, y + 312) };
        adv.LinkClicked += (_, _) => ShowPage(advanced: true);
        page.Controls.Add(adv);
        return page;
    }

    // ── Page 2: advanced own-client wizard ─────────────────────────────────────

    private Panel BuildAdvancedPage()
    {
        var page = NewPage();
        page.AutoScroll = true;
        // Drag-and-drop needs an STA thread; without it, importing still works via the button.
        page.AllowDrop = ApartmentGuard.CanUseOleFeatures();
        page.DragEnter += OnDragEnter;
        page.DragDrop  += OnDragDrop;

        var y = 0;
        if (_hasBuiltIn)
        {
            var back = new LinkLabel { Text = "< Back to simple sign-in", AutoSize = true, Location = new Point(0, y) };
            back.LinkClicked += (_, _) => ShowPage(advanced: false);
            page.Controls.Add(back);
            y += 26;
        }
        y = AddText(page, "Use your own Google sign-in client", y, 15f, FontStyle.Regular, BodyText, "Segoe UI Semibold") + 4;
        y = AddText(page, "A one-time setup of about 10 minutes, free. Follow the steps in order. Each \"Open\" button takes you to the right Google page.", y, 9.5f, SubtleText) + 8;
        var guide = new LinkLabel { Text = "Open the illustrated guide", AutoSize = true, Location = new Point(0, y) };
        guide.LinkClicked += (_, _) => OpenUrl(GuideUrl);
        page.Controls.Add(guide);
        y += 28;

        y = Step(page, y, "1", "Create a Google Cloud project",
            "Sign in, give it any name (for example \"SyncIt\") and click Create. Then paste the project ID below so the other buttons open the right project.",
            "https://console.cloud.google.com/projectcreate");
        AddText(page, "Project ID:", y + 4, 9f, SubtleText, x: 36);
        _projectIdBox = new TextBox { Location = new Point(120, y), Width = 300, PlaceholderText = "e.g. syncit-123456" };
        page.Controls.Add(_projectIdBox);
        y += 36;

        y = Step(page, y, "2", "Turn on the Google Drive API",
            "Click \"Enable\".", "https://console.cloud.google.com/apis/library/drive.googleapis.com");
        y = Step(page, y, "3", "Name your app (Branding)",
            "Click \"Get started\". App name: SyncIt. Support email: your email. Audience: External. Contact email: your email. Agree and click Create.",
            "https://console.cloud.google.com/auth/branding");
        y = Step(page, y, "4", "Let your account sign in (Audience)",
            "Under \"Test users\", click \"Add users\" and add the Gmail address you'll sync. (Or click \"Publish app\" so you don't need test users and sign-in doesn't expire every 7 days.)",
            "https://console.cloud.google.com/auth/audience");
        y = Step(page, y, "5", "Create the Desktop client",
            "Application type: \"Desktop app\". Name: SyncIt. Click Create, then click \"Download JSON\".",
            "https://console.cloud.google.com/auth/clients/create");

        y = AddText(page, "6. Import the file you downloaded", y, 10.5f, FontStyle.Bold, BodyText) + 4;
        var import = new Button { Text = "Import client file (.json)...", Size = new Size(230, 34), Location = new Point(36, y), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
        import.Click += OnImportClick;
        page.Controls.Add(import);
        AddText(page, "or drag the client_secret_....json file onto this window", y + 8, 9f, SubtleText, x: 276);
        y += 44;

        y = AddText(page, "Or paste the values by hand:", y, 9f, SubtleText, x: 36) + 2;
        _clientIdBox = new TextBox { Location = new Point(36, y), Width = 500, PlaceholderText = "Client ID (ends with .apps.googleusercontent.com)" };
        page.Controls.Add(_clientIdBox);
        y += 30;
        _clientSecretBox = new TextBox { Location = new Point(36, y), Width = 500, UseSystemPasswordChar = true, PlaceholderText = "Client secret (starts with GOCSPX-)" };
        page.Controls.Add(_clientSecretBox);
        y += 40;

        _advancedSignInBtn = new Button
        {
            Text = "7. Sign in with Google", Size = new Size(240, 42), Location = new Point(36, y),
            BackColor = AccentBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 11f), Cursor = Cursors.Hand,
        };
        _advancedSignInBtn.FlatAppearance.BorderSize = 0;
        _advancedSignInBtn.Click += async (_, _) => await SaveCustomAndSignInAsync();
        page.Controls.Add(_advancedSignInBtn);
        y += 52;

        _advancedStatus = new Label { AutoSize = true, MaximumSize = new Size(ContentWidth - 40, 0), Location = new Point(36, y) };
        page.Controls.Add(_advancedStatus);
        page.Controls.Add(new Label { Location = new Point(0, y + 80), Size = new Size(1, 1) }); // scroll padding
        return page;
    }

    private int Step(Panel page, int y, string num, string title, string body, string url)
    {
        AddText(page, num + ".", y, 10.5f, FontStyle.Bold, AccentBlue);
        AddText(page, title, y, 10.5f, FontStyle.Bold, BodyText, x: 24);
        var open = new Button { Text = "Open this page", Size = new Size(120, 28), Location = new Point(ContentWidth - 120, y - 4), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
        open.Click += (_, _) => OpenUrl(WithProject(url));
        page.Controls.Add(open);
        var bottom = AddText(page, body, y + 24, 9.5f, SubtleText, x: 24, width: ContentWidth - 30);
        return bottom + 14;
    }

    private string WithProject(string url)
    {
        var project = _projectIdBox?.Text.Trim();
        if (string.IsNullOrEmpty(project) || url.Contains("projectcreate")) return url;
        return url + (url.Contains('?') ? "&" : "?") + "project=" + Uri.EscapeDataString(project);
    }

    // ── Import ─────────────────────────────────────────────────────────────────

    private void OnImportClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose the client_secret_....json file you downloaded",
            Filter = "Google client file (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) ImportFile(dlg.FileName);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && files[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) ImportFile(files[0]);
    }

    private void ImportFile(string path)
    {
        var r = ClientSecretFileParser.ParseFile(path);
        if (!r.Ok)
        {
            SetStatus(_advancedStatus, r.Error!, AccentRed);
            return;
        }
        _clientIdBox.Text = r.ClientId;
        _clientSecretBox.Text = r.ClientSecret;
        if (!string.IsNullOrEmpty(r.ProjectId) && string.IsNullOrWhiteSpace(_projectIdBox.Text)) _projectIdBox.Text = r.ProjectId;
        SetStatus(_advancedStatus, "Client file imported. Click \"Sign in with Google\".", AccentGreen);
    }

    // ── Sign-in ────────────────────────────────────────────────────────────────

    private async Task SaveCustomAndSignInAsync()
    {
        var id = _clientIdBox.Text.Trim();
        var secret = _clientSecretBox.Text.Trim();
        var err = OAuthClientValidator.ValidateClientId(id) ?? OAuthClientValidator.ValidateClientSecret(secret);
        if (err is not null) { SetStatus(_advancedStatus, err, AccentRed); return; }
        try { CredentialHelper.SaveClientCredentials(id, secret); }
        catch (Exception ex) { SetStatus(_advancedStatus, $"Could not save the client: {ex.Message}", AccentRed); return; }
        await SignInAsync(builtIn: false);
    }

    private async Task SignInAsync(bool builtIn)
    {
        var status = builtIn ? _welcomeStatus : _advancedStatus;
        var button = builtIn ? _signInBtn : _advancedSignInBtn;
        button.Enabled = false;
        _welcomeFallbackLink.Visible = false;
        SetStatus(status, "Your browser is opening. Finish signing in there, then come back to this window.", AccentBlue);

        try
        {
            // Simple mode must use the built-in client even if an old custom client was saved earlier.
            if (builtIn && CredentialHelper.CredentialsExist()) CredentialHelper.DeleteClientCredentials();

            var oauthService = new OAuthService(LoadConfig(), NullLogger<OAuthService>.Instance);
            await oauthService.InitializeAsync();

            SetStatus(status, "You're signed in. Next, choose which folders to sync.", AccentGreen);
            ReplaceClickWithContinue(button);
        }
        catch (Exception ex)
        {
            var kind = OAuthErrorClassifier.Classify(ex);
            SetStatus(status, OAuthErrorClassifier.Message(kind, builtIn), AccentRed);
            if (builtIn && OAuthErrorClassifier.SuggestOwnClient(kind))
            {
                _welcomeFallbackLink.Location = new Point(0, _welcomeStatus.Bottom + 6);
                _welcomeFallbackLink.Visible = true;
            }
            button.Enabled = true;
        }
    }

    private void ReplaceClickWithContinue(Button button)
    {
        // Drop earlier sign-in handlers so "Continue" only closes the dialog.
        var fresh = new Button
        {
            Text = "Continue", Size = button.Size, Location = button.Location, BackColor = AccentGreen,
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = button.Font, Cursor = Cursors.Hand,
        };
        fresh.FlatAppearance.BorderSize = 0;
        fresh.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var parent = button.Parent!;
        parent.Controls.Remove(button);
        parent.Controls.Add(fresh);
    }

    private static SyncIt.Core.Models.SyncConfig LoadConfig()
    {
        var configPath = PathHelper.Expand(@"%APPDATA%\SyncIt\syncit.json");
        var config = new SyncIt.Core.Models.SyncConfig();
        if (!File.Exists(configPath)) return config;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<SyncIt.Core.Models.SyncConfig>(
                File.ReadAllText(configPath),
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) ?? config;
        }
        catch { return config; }
    }

    // ── UI helpers ─────────────────────────────────────────────────────────────

    private Panel NewPage() => new() { Location = new Point(28, 24), Size = new Size(ClientSize.Width - 40, ClientSize.Height - 40) };

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static void SetStatus(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
        label.Refresh();
    }

    private static int AddText(Control parent, string text, int y, float size, Color? color = null, int x = 0, int width = ContentWidth)
        => AddText(parent, text, y, size, FontStyle.Regular, color ?? BodyText, x: x, width: width);

    private static int AddText(Control parent, string text, int y, float size, FontStyle style, Color color, string family = "Segoe UI", int x = 0, int width = ContentWidth)
    {
        var lbl = new Label
        {
            Text = text, Font = new Font(family, size, style), ForeColor = color, AutoSize = true,
            MaximumSize = new Size(width, 0), Location = new Point(x, y), BackColor = Color.Transparent,
        };
        parent.Controls.Add(lbl);
        return lbl.Bottom;
    }

    private static Panel Card(Control parent, int y, int height)
    {
        var card = new Panel { Location = new Point(0, y), Size = new Size(ContentWidth, height) };
        Theme.MakeCard(card);
        parent.Controls.Add(card);
        return card;
    }
    // Fluent theme (see Theme.cs): remaps colors/fonts by role, dark mode, rounded window corners.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);
    }
}
