using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SyncIt.App.Forms;
using SyncIt.Core.Crypto;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;

namespace SyncIt.App;

/// <summary>1.4 tools: dry-run preview, version history, duplicates, Explorer commands, encrypted folders.</summary>
public sealed partial class SyncItApp
{
    private ToolStripMenuItem _encryptedAddItem = null!;
    private ToolStripMenuItem _encryptedRestoreItem = null!;

    // ── dry-run preview ───────────────────────────────────────────────────
    private DialogResult ConfirmFirstSync(SyncPair pair, string localPath, IGoogleDriveService? drive)
    {
        if (drive is null) return DialogResult.No;
        SyncPreviewResult? preview = null;
        Exception? error = null;
        using (var wait = new Form { Text = "SyncIt", Size = new Size(380, 120), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false })
        {
            wait.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Comparing this folder with Google Drive…" });
            wait.Shown += async (_, _) =>
            {
                try
                {
                    var filter = new GlobFilter();
                    var root = PathHelper.Expand(localPath);
                    var remote = await drive.ListTreeAsync(pair.RemoteFolderId);
                    preview = await SyncPreview.BuildAsync(pair, SyncPreview.ScanLocal(root).ToList(), remote,
                        rel => filter.IsIncluded(rel, pair) && (pair.SelectedSubfolders.Count == 0 || pair.SelectedSubfolders.Any(s => rel.StartsWith(s.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))),
                        rel => HashHelper.ComputeMd5Async(Path.Combine(root, rel)));
                }
                catch (Exception ex) { error = ex; }
                wait.Close();
            };
            wait.ShowDialog();
        }
        if (preview is null)
        {
            _logger.LogWarning(error, "Preview failed for pair {Id}", pair.Id);
            return MessageBox.Show($"Could not build a preview ({error?.Message}).\n\nStart syncing anyway? Files on both sides are matched by name and content; nothing is deleted on a first sync.",
                "SyncIt", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        }
        _logger.LogInformation("First-sync preview for {Id}: {Summary}", pair.Id, preview.Summary);
        using var form = new SyncPreviewForm($"{localPath}  ↔  {AccountColors.RemoteLocation(pair)}", preview);
        return form.ShowDialog() == DialogResult.OK ? DialogResult.Yes : DialogResult.No;
    }

    // ── version history / trash ───────────────────────────────────────────
    private VersionHistoryService? HistoryFor(SyncPair pair)
    {
        var drive = ResolveDrive(pair);
        if (drive is null) return null;
        return new VersionHistoryService(drive, CreateStateStore(), new LocalTrashManager(_config.LocalTrashPath, CreateLogger<LocalTrashManager>()), _logger);
    }

    internal void ShowHistory(string? absolutePath)
    {
        SyncPair? pair; string? rel = null;
        if (absolutePath is not null)
        {
            if (DriveLinks.Locate(_config, absolutePath) is not { } hit) { MessageBox.Show("This file is not in a SyncIt folder.", "SyncIt"); return; }
            pair = hit.Pair; rel = File.Exists(absolutePath) ? hit.RelativePath : null;
        }
        else pair = PickPair("Restore deleted files from which folder?");
        if (pair is null) return;
        var svc = HistoryFor(pair);
        if (svc is null) { MessageBox.Show("That folder's Google account is not signed in.", "SyncIt"); return; }
        new VersionHistoryForm(svc, pair, rel).Show();
    }

    private SyncPair? PickPair(string _)
    {
        var pairs = SyncPairPlanner.EnabledUnique(_config.SyncPairs).Where(p => !p.Encrypted).ToList();
        if (pairs.Count <= 1) return pairs.FirstOrDefault();
        using var dlg = new PairPickerDialog(pairs, _config);
        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPair : null;
    }

    private void ShowDuplicates()
    {
        var pairs = SyncPairPlanner.EnabledUnique(_config.SyncPairs);
        new DuplicatesForm(pairs, _config, p => ResolveDrive(p), async p =>
        {
            var store = CreateStateStore();
            await store.InitializeAsync(CancellationToken.None);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var r in store.StreamRecordsAsync(p.Id, SyncSide.Remote))
                if (r.DriveFileId is not null) ids.Add(r.DriveFileId);
            return (ISet<string>)ids;
        }).Show();
    }

    // ── Explorer commands (second instance → named pipe → this instance) ──
    internal static string ShellPipeName => "SyncIt-Shell-" + Environment.UserName.GetHashCode().ToString("X8");

    private void StartShellCommandServer()
    {
        _ = Task.Run(async () =>
        {
            while (!_engineCts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(ShellPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_engineCts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync();
                    if (line?.Split('\t', 2) is [var verb, var path]) InvokeUI(() => UiGuard.Run(() => HandleShellCommand(verb, path), "ShellCommand"));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogDebug(ex, "Shell command pipe"); await Task.Delay(1000); }
            }
        });
    }

    /// <summary>Called in a second instance started from the Explorer menu. Returns false when SyncIt is not running.</summary>
    internal static bool ForwardShellCommand(string verb, string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ShellPipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes($"{verb}\t{path}\n");
            client.Write(bytes); client.Flush();
            return true;
        }
        catch { return false; }
    }

    private void HandleShellCommand(string verb, string path)
    {
        _logger.LogInformation("Explorer command {Verb}", verb);
        if (verb == "history") { ShowHistory(path); return; }
        _ = Task.Run(async () =>
        {
            var store = CreateStateStore();
            await store.InitializeAsync(CancellationToken.None);
            var link = await DriveLinks.ResolveAsync(_config, path, store, p => ResolveDrive(p));
            InvokeUI(() =>
            {
                if (link is null) { ShowBalloon("SyncIt", "This item is not on Google Drive yet.", ToolTipIcon.Info); return; }
                if (verb == "copy-link") { Clipboard.SetText(link); ShowBalloon("SyncIt", "Drive link copied.", ToolTipIcon.Info); }
                else Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            });
        });
    }

    /// <summary>Registers the Explorer right-click commands for files and folders (per user, HKCU).</summary>
    internal static void RegisterExplorerMenu(string exePath)
    {
        foreach (var root in new[] { @"Software\Classes\*\shell", @"Software\Classes\Directory\shell" })
        {
            using var key = Registry.CurrentUser.CreateSubKey(root + @"\SyncIt");
            key.SetValue("MUIVerb", "SyncIt");
            key.SetValue("Icon", $"\"{exePath}\",0");
            key.SetValue("SubCommands", "");
            using var sub = key.CreateSubKey("shell");
            void Cmd(string name, string label, string verb)
            {
                using var c = sub.CreateSubKey(name);
                c.SetValue("MUIVerb", label);
                using var cmd = c.CreateSubKey("command");
                cmd.SetValue("", $"\"{exePath}\" --shell {verb} \"%1\"");
            }
            Cmd("01copy", "Copy Drive link", "copy-link");
            Cmd("02open", "Open in Drive", "open");
            if (root.Contains('*')) Cmd("03hist", "Version history…", "history");
        }
    }

    internal static void UnregisterExplorerMenu()
    {
        foreach (var root in new[] { @"Software\Classes\*\shell\SyncIt", @"Software\Classes\Directory\shell\SyncIt" })
            try { Registry.CurrentUser.DeleteSubKeyTree(root, false); } catch { }
    }

    // ── encrypted folders (Experimental) ──────────────────────────────────
    private void AddEncryptedFolder()
    {
        if (!_config.ExperimentalFeatures) return;
        string? local = null;
        var t = new Thread(() =>
        {
            using var d = new FolderBrowserDialog { Description = "Folder on this PC to back up ENCRYPTED (beta)", UseDescriptionForTitle = true };
            if (d.ShowDialog() == DialogResult.OK) local = d.SelectedPath;
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (local is null) return;

        var config = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new SyncConfig();
        AccountRegistry.Migrate(config, p => CredentialHelper.TokenExists(PathHelper.Expand(p)));
        var pair = new SyncPair
        {
            Id = SyncPairPlanner.NextPairId(config.SyncPairs), LocalPath = local, Enabled = false,
            Encrypted = true, Mode = SyncMode.BackupOnly, BackupKeepDeletedOnDrive = true, ExportGoogleDocs = false,
        };
        using (var picker = new RemoteLocationDialog(config.Accounts, id => DriveForAccount(id)))
        {
            if (picker.ShowDialog() != DialogResult.OK || picker.AccountId is null) return;
            AccountRegistry.ApplyLocation(pair, picker.AccountId, picker.SharedDrive);
            var folder = picker.FolderPath ?? $"SyncIt Encrypted/{Path.GetFileName(local.TrimEnd('\\'))}";
            var drive = ResolveDrive(pair);
            if (drive is null) return;
            var id = Task.Run(() => drive.EnsureFolderPathAsync(pair.RemoteFolderId, folder)).GetAwaiter().GetResult();
            AccountRegistry.ApplyRemoteFolder(pair, id, folder);
        }
        using var pass = new PassphraseDialog();
        if (pass.ShowDialog() != DialogResult.OK) return;
        var p = VaultCrypto.CreateParams(pass.Passphrase, out _);
        pair.EncryptionSalt = p.SaltB64; pair.EncryptionVerifier = p.VerifierB64; pair.EncryptionIterations = p.Iterations;
        VaultPassphraseStore.Save(VaultDir, pair.Id, pass.Passphrase);
        RemovableDrives.Bind(pair, SyncEngine.Volumes);
        pair.Enabled = true;
        config.SyncPairs.Add(pair);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, WriteOpts)); // config watcher starts it
        _logger.LogInformation("Encrypted backup pair {Id} added (beta).", pair.Id);
    }

    private async Task RestoreEncryptedAsync()
    {
        var pairs = _config.SyncPairs.Where(p => p.Encrypted).ToList();
        if (pairs.Count == 0) { MessageBox.Show("No encrypted backup folders yet.", "SyncIt"); return; }
        SyncPair? pair = pairs[0];
        if (pairs.Count > 1) { using var dlg = new PairPickerDialog(pairs, _config); pair = dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPair : null; }
        if (pair is null) return;
        var crypto = VaultPassphraseStore.Open(VaultDir, pair);
        if (crypto is null)
        {
            using var pd = new PassphraseDialog(confirm: false);
            if (pd.ShowDialog() != DialogResult.OK) return;
            try { crypto = VaultCrypto.Open(pd.Passphrase, new VaultCrypto.VaultParams(pair.EncryptionSalt!, pair.EncryptionVerifier!, pair.EncryptionIterations)); }
            catch { MessageBox.Show("Wrong passphrase.", "SyncIt"); return; }
        }
        string? target = null;
        var t = new Thread(() => { using var d = new FolderBrowserDialog { Description = "Choose an EMPTY folder to restore into", UseDescriptionForTitle = true }; if (d.ShowDialog() == DialogResult.OK) target = d.SelectedPath; });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (target is null || ResolveDrive(pair) is not { } drive) return;
        try
        {
            var runner = new EncryptedBackupRunner(drive, crypto, Path.Combine(VaultDir, pair.Id + ".manifest.json"), CreateLogger<EncryptedBackupRunner>());
            var n = await Task.Run(() => runner.RestoreAsync(pair.RemoteFolderId, target));
            MessageBox.Show($"Restored {n} file(s) into {target}.", "SyncIt");
        }
        catch (Exception ex) { MessageBox.Show("Restore failed: " + ex.Message, "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
