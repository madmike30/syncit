using System.Runtime.Versioning;
using System.Text.Json;
using SyncIt.Core.Models;
using SyncIt.Core.Services;

namespace SyncIt.App.Forms;

/// <summary>
/// Lets the user browse and select which subfolders within their Drive to sync.
/// Uses lazy-loading (load children on expand) to handle large Drive accounts.
/// Empty selection = sync everything (the default).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SelectFoldersForm : Form
{
    private readonly SyncPair _pair;
    private readonly string _configPath;
    private readonly IGoogleDriveService _driveService;

    private TreeView _tree = null!;
    private Label _statusLabel = null!;
    private Button _okButton = null!;
    private Button _cancelButton = null!;
    private Button _selectAllButton = null!;
    private Button _clearButton = null!;

    // Prevents recursive AfterCheck cascades
    private bool _suppressCheckEvents;

    private const string Placeholder = "__placeholder";

    /// <summary>Stored in TreeNode.Tag to track both path and Drive ID.</summary>
    private record FolderTag(string RelativePath, string DriveId);

    public SelectFoldersForm(SyncPair pair, string configPath, IGoogleDriveService driveService)
    {
        _pair         = pair;
        _configPath   = configPath;
        _driveService = driveService;

        InitializeComponent();
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    private void InitializeComponent()
    {
        Text            = "Select Folders to Sync";
        Size            = new Size(480, 560);
        MinimumSize     = new Size(360, 380);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = false;

        // --- Header ---
        var header = new Label
        {
            Text      = "Choose which Google Drive folders to sync.",
            Dock      = DockStyle.Top,
            AutoSize  = false,
            Height    = 28,
            Padding   = new Padding(0, 4, 0, 0),
            Font      = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
        };

        var hintLabel = new Label
        {
            Text    = "Check the folders you want to sync locally.\n" +
                      "Unchecked folders will not be downloaded or uploaded.",
            Dock    = DockStyle.Top,
            AutoSize = false,
            Height  = 38,
            ForeColor = Color.FromArgb(80, 80, 80),
        };

        // --- Status / tree ---
        _statusLabel = new Label
        {
            Text      = "Loading folders from Google Drive...",
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray,
        };

        _tree = new TreeView
        {
            Dock       = DockStyle.Fill,
            CheckBoxes = true,
            Visible    = false,
            HideSelection = false,
            ShowLines     = true,
            ShowPlusMinus = true,
        };
        _tree.AfterCheck  += OnAfterCheck;
        _tree.BeforeExpand += OnBeforeExpand;

        var treePanel = new Panel { Dock = DockStyle.Fill };
        treePanel.Controls.Add(_tree);
        treePanel.Controls.Add(_statusLabel);

        // --- Quick-action buttons row ---
        _selectAllButton = new Button { Text = "Select All", Width = 80, Height = 26, Enabled = false };
        _clearButton     = new Button { Text = "Clear All",  Width = 80, Height = 26, Enabled = false };
        _selectAllButton.Click += (_, _) => SetAllChecked(true);
        _clearButton.Click     += (_, _) => SetAllChecked(false);

        var quickPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Bottom,
            AutoSize      = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding       = new Padding(0, 4, 0, 0),
        };
        quickPanel.Controls.Add(_selectAllButton);
        quickPanel.Controls.Add(_clearButton);

        // --- OK / Cancel ---
        _cancelButton = new Button
        {
            Text         = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width        = 80,
            Height       = 26,
        };
        _okButton = new Button
        {
            Text         = "OK",
            DialogResult = DialogResult.OK,
            Width        = 80,
            Height       = 26,
            Enabled      = false,
        };

        var okCancelPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Bottom,
            AutoSize      = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding       = new Padding(0, 4, 0, 4),
        };
        okCancelPanel.Controls.Add(_cancelButton);
        okCancelPanel.Controls.Add(_okButton);

        // --- Assemble ---
        var outer = new TableLayoutPanel
        {
            Dock       = DockStyle.Fill,
            RowCount   = 5,
            ColumnCount = 1,
            Padding    = new Padding(10),
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // header
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // hint
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // tree
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // select-all/clear
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // ok/cancel

        outer.Controls.Add(header,       0, 0);
        outer.Controls.Add(hintLabel,    0, 1);
        outer.Controls.Add(treePanel,    0, 2);
        outer.Controls.Add(quickPanel,   0, 3);
        outer.Controls.Add(okCancelPanel, 0, 4);

        Controls.Add(outer);
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    // -------------------------------------------------------------------------
    // Load top-level folders on show
    // -------------------------------------------------------------------------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadTopLevelAsync();
    }

    private async Task LoadTopLevelAsync()
    {
        try
        {
            var subfolders = await _driveService.ListSubfoldersAsync(
                _pair.RemoteFolderId, "", maxDepth: 1);

            if (subfolders.Count == 0)
            {
                _statusLabel.Text      = "No folders found in Google Drive.";
                _statusLabel.ForeColor = Color.Gray;
                _okButton.Enabled      = true;
                return;
            }

            _tree.BeginUpdate();
            foreach (var folder in subfolders.OrderBy(f => f.Name))
            {
                var node = new TreeNode(folder.Name)
                {
                    Tag = new FolderTag(folder.RelativePath, folder.DriveId),
                };
                // Add placeholder so [+] expand button appears
                node.Nodes.Add(new TreeNode("Loading...") { Tag = Placeholder });

                // Pre-check if this folder is in SelectedSubfolders
                node.Checked = IsPathSelected(folder.RelativePath);

                _tree.Nodes.Add(node);
            }
            _tree.EndUpdate();

            _statusLabel.Visible     = false;
            _tree.Visible            = true;
            _okButton.Enabled        = true;
            _selectAllButton.Enabled = true;
            _clearButton.Enabled     = true;
        }
        catch (Exception ex)
        {
            _statusLabel.Text      = $"Error loading folders: {ex.Message}";
            _statusLabel.ForeColor = Color.DarkRed;
        }
    }

    // -------------------------------------------------------------------------
    // Lazy-load children on expand
    // -------------------------------------------------------------------------

    private async void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node is null) return;

        // If the first child is a placeholder, load real children
        if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag as string == Placeholder)
        {
            if (e.Node.Tag is not FolderTag tag) return;

            try
            {
                e.Node.Nodes.Clear();

                var children = await _driveService.ListSubfoldersAsync(
                    tag.DriveId, tag.RelativePath, maxDepth: 1);

                if (children.Count == 0)
                {
                    e.Node.Nodes.Add(new TreeNode("(no subfolders)") { ForeColor = Color.Gray });
                    return;
                }

                _suppressCheckEvents = true;
                try
                {
                    foreach (var child in children.OrderBy(c => c.Name))
                    {
                        var childNode = new TreeNode(child.Name)
                        {
                            Tag     = new FolderTag(child.RelativePath, child.DriveId),
                            Checked = e.Node.Checked || IsPathSelected(child.RelativePath),
                        };
                        childNode.Nodes.Add(new TreeNode("Loading...") { Tag = Placeholder });
                        e.Node.Nodes.Add(childNode);
                    }
                }
                finally
                {
                    _suppressCheckEvents = false;
                }
            }
            catch (Exception ex)
            {
                e.Node.Nodes.Clear();
                e.Node.Nodes.Add(new TreeNode($"Error: {ex.Message}") { ForeColor = Color.DarkRed });
            }
        }
    }

    // -------------------------------------------------------------------------
    // Check helpers
    // -------------------------------------------------------------------------

    private bool IsPathSelected(string relativePath)
    {
        foreach (var sf in _pair.SelectedSubfolders)
        {
            var prefix = sf.TrimEnd('/');
            if (relativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // TreeView check cascading
    // -------------------------------------------------------------------------

    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_suppressCheckEvents || e.Action == TreeViewAction.Unknown || e.Node is null) return;

        _suppressCheckEvents = true;
        try
        {
            SetSubtreeChecked(e.Node, e.Node.Checked);
        }
        finally
        {
            _suppressCheckEvents = false;
        }
    }

    private static void SetSubtreeChecked(TreeNode node, bool isChecked)
    {
        foreach (TreeNode child in node.Nodes)
        {
            if (child.Tag as string == Placeholder) continue;
            child.Checked = isChecked;
            SetSubtreeChecked(child, isChecked);
        }
    }

    private void SetAllChecked(bool isChecked)
    {
        _suppressCheckEvents = true;
        try
        {
            foreach (TreeNode root in _tree.Nodes)
            {
                root.Checked = isChecked;
                SetSubtreeChecked(root, isChecked);
            }
        }
        finally
        {
            _suppressCheckEvents = false;
        }
    }

    // -------------------------------------------------------------------------
    // Save on OK
    // -------------------------------------------------------------------------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
            SaveToConfig();
        base.OnFormClosing(e);
    }

    private void SaveToConfig()
    {
        var selected = new List<string>();
        CollectMinimalCheckedPaths(_tree.Nodes, selected);

        try
        {
            var json    = File.ReadAllText(_configPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented               = true,
                Converters                  = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            };
            var config = JsonSerializer.Deserialize<SyncConfig>(json, options);
            if (config is null) return;

            var pair = config.SyncPairs.FirstOrDefault(p => p.Id == _pair.Id);
            if (pair is null) return;

            pair.SelectedSubfolders = selected;

            File.WriteAllText(_configPath, JsonSerializer.Serialize(config, options));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to save folder selection:\n{ex.Message}",
                "SyncIt",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Walk the tree and collect the minimal set of checked paths:
    /// if a parent is checked, include it and skip its children
    /// (children are implicitly covered by the parent selection).
    /// </summary>
    private static void CollectMinimalCheckedPaths(TreeNodeCollection nodes, List<string> result)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag as string == Placeholder) continue;

            if (node.Checked && node.Tag is FolderTag tag)
            {
                result.Add(tag.RelativePath);
                // Don't descend — children are covered by the parent
            }
            else
            {
                CollectMinimalCheckedPaths(node.Nodes, result);
            }
        }
    }
    // Fluent theme (see Theme.cs): remaps colors/fonts by role, dark mode, rounded window corners.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);
    }
}
