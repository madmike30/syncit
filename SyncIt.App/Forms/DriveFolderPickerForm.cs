using System.Runtime.Versioning;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>
/// Lets the user browse and pick a Google Drive folder to sync.
/// Shows a tree of folders from the user's Drive.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriveFolderPickerForm : Form
{
    private static readonly Color BgColor    = Color.FromArgb(250, 251, 253);
    private static readonly Color CardBg     = Color.White;
    private static readonly Color CardBorder = Color.FromArgb(225, 228, 232);
    private static readonly Color AccentBlue = Color.FromArgb(0, 102, 204);
    private static readonly Color SubtleText = Color.FromArgb(100, 110, 120);

    private readonly IGoogleDriveService _driveService;
    private TreeView _tree = null!;
    private Button _selectBtn = null!;
    private Button _cancelBtn = null!;
    private Label _statusLabel = null!;

    /// <summary>Selected folder ID, or null if cancelled.</summary>
    public string? SelectedFolderId { get; private set; }

    /// <summary>Selected folder name/path for display.</summary>
    public string? SelectedFolderName { get; private set; }

    public DriveFolderPickerForm(IGoogleDriveService driveService)
    {
        _driveService = driveService;
        BuildUI();
        _ = LoadRootFoldersAsync();
    }

    private void BuildUI()
    {
        Text            = "Choose Google Drive Folder";
        Size            = new Size(440, 480);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = BgColor;
        Font            = new Font("Segoe UI", 9.5f);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };
        Controls.Add(root);

        var title = new Label
        {
            Text      = "Select a folder from your Google Drive",
            Font      = new Font("Segoe UI Semibold", 12f),
            ForeColor = Color.FromArgb(30, 30, 30),
            AutoSize  = true,
            Location  = new Point(0, 0),
        };
        root.Controls.Add(title);

        var hint = new Label
        {
            Text      = "Click a folder to expand it. Select the folder you want to sync.",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = SubtleText,
            AutoSize  = true,
            Location  = new Point(0, 28),
        };
        root.Controls.Add(hint);

        _tree = new TreeView
        {
            Location    = new Point(0, 56),
            Size        = new Size(384, 300),
            Font        = new Font("Segoe UI", 10f),
            BorderStyle = BorderStyle.FixedSingle,
            ShowLines   = true,
            ShowPlusMinus = true,
            HideSelection = false,
        };
        _tree.BeforeExpand += OnBeforeExpand;
        _tree.AfterSelect += (_, _) => _selectBtn.Enabled = _tree.SelectedNode is not null;
        root.Controls.Add(_tree);

        _statusLabel = new Label
        {
            Text      = "Loading folders...",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = AccentBlue,
            AutoSize  = true,
            Location  = new Point(0, 364),
        };
        root.Controls.Add(_statusLabel);

        _selectBtn = new Button
        {
            Text      = "Select",
            Size      = new Size(100, 36),
            Location  = new Point(172, 390),
            BackColor = AccentBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI Semibold", 10f),
            Cursor    = Cursors.Hand,
            Enabled   = false,
        };
        _selectBtn.FlatAppearance.BorderSize = 0;
        _selectBtn.Click += OnSelect;
        root.Controls.Add(_selectBtn);

        _cancelBtn = new Button
        {
            Text      = "Cancel",
            Size      = new Size(100, 36),
            Location  = new Point(280, 390),
            BackColor = CardBg,
            ForeColor = Color.FromArgb(60, 60, 60),
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 10f),
            Cursor    = Cursors.Hand,
        };
        _cancelBtn.FlatAppearance.BorderColor = CardBorder;
        _cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        root.Controls.Add(_cancelBtn);
    }

    private async Task LoadRootFoldersAsync()
    {
        try
        {
            // Add "My Drive" as root, then load its subfolders
            var rootNode = new TreeNode("My Drive")
            {
                Tag = "root",
                ImageIndex = 0,
            };
            rootNode.Nodes.Add(new TreeNode("Loading...") { Tag = "__placeholder" });
            _tree.Nodes.Add(rootNode);

            // Pre-load the root level
            await LoadChildrenAsync(rootNode, "root");
            rootNode.Expand();

            _statusLabel.Text = "Select a folder and click Select.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error loading folders: {ex.Message}";
            _statusLabel.ForeColor = Color.FromArgb(215, 40, 40);
        }
    }

    private async void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node is null) return;

        // If the first child is a placeholder, load real children
        if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag as string == "__placeholder")
        {
            var folderId = e.Node.Tag as string;
            if (folderId is not null)
            {
                _statusLabel.Text = "Loading...";
                await LoadChildrenAsync(e.Node, folderId);
                _statusLabel.Text = "Select a folder and click Select.";
            }
        }
    }

    private async Task LoadChildrenAsync(TreeNode parent, string folderId)
    {
        try
        {
            // Remove placeholder
            parent.Nodes.Clear();

            var subfolders = await _driveService.ListSubfoldersAsync(folderId, "", maxDepth: 1);

            if (subfolders.Count == 0)
            {
                parent.Nodes.Add(new TreeNode("(no subfolders)") { ForeColor = SubtleText });
                return;
            }

            foreach (var folder in subfolders.OrderBy(f => f.Name))
            {
                var node = new TreeNode(folder.Name)
                {
                    Tag = folder.DriveId,
                };
                // Add a placeholder so the [+] expand button shows
                node.Nodes.Add(new TreeNode("Loading...") { Tag = "__placeholder" });
                parent.Nodes.Add(node);
            }
        }
        catch (Exception ex)
        {
            parent.Nodes.Clear();
            parent.Nodes.Add(new TreeNode($"Error: {ex.Message}") { ForeColor = Color.FromArgb(215, 40, 40) });
        }
    }

    private void OnSelect(object? sender, EventArgs e)
    {
        var node = _tree.SelectedNode;
        if (node?.Tag is not string folderId || folderId == "__placeholder") return;

        SelectedFolderId = folderId;
        SelectedFolderName = GetNodePath(node);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string GetNodePath(TreeNode node)
    {
        var parts = new List<string>();
        var current = node;
        while (current is not null)
        {
            parts.Insert(0, current.Text);
            current = current.Parent;
        }
        return string.Join(" / ", parts);
    }
    // Fluent theme (see Theme.cs): remaps colors/fonts by role, dark mode, rounded window corners.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);
    }
}
