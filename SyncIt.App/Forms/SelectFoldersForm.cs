using System.Runtime.Versioning;
using System.Text.Json;
using SyncIt.Core.Ipc;
using SyncIt.Core.Models;

namespace SyncIt.App.Forms;

/// <summary>
/// Lets the user browse and select which subfolders within a sync pair's Drive root to sync.
/// Empty selection = sync everything (the default).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SelectFoldersForm : Form
{
    private readonly SyncPair _pair;
    private readonly string _configPath;
    private readonly Task<SubfoldersResultPayload?> _loadTask;

    private TreeView _tree = null!;
    private Label _statusLabel = null!;
    private Button _okButton = null!;
    private Button _cancelButton = null!;
    private Button _selectAllButton = null!;
    private Button _clearButton = null!;
    private Label _hintLabel = null!;

    // Prevents recursive AfterCheck cascades
    private bool _suppressCheckEvents;

    public SelectFoldersForm(SyncPair pair, string configPath, Task<SubfoldersResultPayload?> loadTask)
    {
        _pair       = pair;
        _configPath = configPath;
        _loadTask   = loadTask;

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
            Text      = $"Choose which folders in \"{_pair.RemoteFolderName}\" to sync.",
            Dock      = DockStyle.Top,
            AutoSize  = false,
            Height    = 28,
            Padding   = new Padding(0, 4, 0, 0),
            Font      = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
        };

        _hintLabel = new Label
        {
            Text    = "Files at the root of the Drive folder are always synced, regardless of selection.\n" +
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
        };
        _tree.AfterCheck += OnAfterCheck;

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
        outer.Controls.Add(_hintLabel,   0, 1);
        outer.Controls.Add(treePanel,    0, 2);
        outer.Controls.Add(quickPanel,   0, 3);
        outer.Controls.Add(okCancelPanel, 0, 4);

        Controls.Add(outer);
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    // -------------------------------------------------------------------------
    // Load subfolders asynchronously once the form appears
    // -------------------------------------------------------------------------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // Wait up to 30 s for the service to respond
            SubfoldersResultPayload? result = null;
            try
            {
                result = await _loadTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                _statusLabel.Text      = "Timed out waiting for folder list. Is the SyncIt service running?";
                _statusLabel.ForeColor = Color.DarkRed;
                return;
            }

            if (result is null)
            {
                _statusLabel.Text      = "No response from the SyncIt service.";
                _statusLabel.ForeColor = Color.DarkRed;
                return;
            }

            if (result.Error is not null)
            {
                _statusLabel.Text      = $"Error: {result.Error}";
                _statusLabel.ForeColor = Color.DarkRed;
                return;
            }

            if (result.Subfolders.Count == 0)
            {
                _statusLabel.Text      = "No subfolders found in this Drive folder.\n" +
                                         "All files at the root are always synced.";
                _statusLabel.ForeColor = Color.Gray;
                _okButton.Enabled      = true;
                return;
            }

            PopulateTree(result.Subfolders);
            _statusLabel.Visible    = false;
            _tree.Visible           = true;
            _okButton.Enabled       = true;
            _selectAllButton.Enabled = true;
            _clearButton.Enabled    = true;
        }
        catch (Exception ex)
        {
            _statusLabel.Text      = $"Unexpected error: {ex.Message}";
            _statusLabel.ForeColor = Color.DarkRed;
        }
    }

    // -------------------------------------------------------------------------
    // Build TreeView from flat path list
    // -------------------------------------------------------------------------

    private void PopulateTree(List<SubfolderEntry> entries)
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        // Map from relative path → TreeNode for efficient parent lookup
        var nodeMap = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        // The service returns entries depth-first (parent before children)
        foreach (var entry in entries)
        {
            var node = new TreeNode(entry.Name) { Tag = entry.RelativePath };

            // Pre-check if this exact path (or a parent path) is in SelectedSubfolders
            node.Checked = IsPathSelected(entry.RelativePath);

            var slash = entry.RelativePath.LastIndexOf('/');
            if (slash < 0)
            {
                // Top-level folder
                _tree.Nodes.Add(node);
            }
            else
            {
                var parentPath = entry.RelativePath[..slash];
                if (nodeMap.TryGetValue(parentPath, out var parentNode))
                    parentNode.Nodes.Add(node);
                else
                    _tree.Nodes.Add(node); // parent not in map (shouldn't happen)
            }

            nodeMap[entry.RelativePath] = node;
        }

        _tree.ExpandAll();
        _tree.EndUpdate();
    }

    private bool IsPathSelected(string relativePath)
    {
        foreach (var sf in _pair.SelectedSubfolders)
        {
            var prefix = sf.TrimEnd('/');
            // Path equals a selected folder, or is inside one
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
            // Cascade to all descendants
            SetSubtreeChecked(e.Node, e.Node.Checked);

            // If unchecked, also uncheck ancestors that were fully covering this node
            // (leave ancestors that were explicitly selected for the path to still work)
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
        // Collect checked top-level paths (children of a checked parent are redundant)
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
            if (node.Checked && node.Tag is string path)
            {
                result.Add(path);
                // Don't descend — children are covered by the parent
            }
            else
            {
                CollectMinimalCheckedPaths(node.Nodes, result);
            }
        }
    }
}
