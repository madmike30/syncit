using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SyncIt.App;

/// <summary>
/// SyncIt design system: Windows 11 (Fluent) look for WinForms.
/// Light by default; Preferences can switch to Dark or follow Windows. Accent follows the Windows accent color.
/// Forms call <see cref="Apply(Form)"/> once their controls exist; existing colors are remapped by role.
/// </summary>
internal static class Theme
{
    /// <summary>Light by default. Set from Preferences (Light / Dark / System).</summary>
    public static bool IsDark { get; private set; } = ReadIsDark("Light");

    public static readonly string[] Modes = { "Light", "Dark", "System" };

    /// <summary>Applies the saved appearance; affects windows opened afterwards.</summary>
    public static void Initialize(string? mode) => IsDark = ReadIsDark(mode);
    public static Color Accent { get; } = ReadAccent();

    public static Color Background => IsDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
    public static Color Surface    => IsDark ? Color.FromArgb(43, 43, 43) : Color.White;
    public static Color SurfaceAlt => IsDark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(249, 249, 249);
    public static Color Border     => IsDark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(229, 229, 229);
    public static Color Text       => IsDark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(27, 27, 27);
    public static Color SubtleText => IsDark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(96, 96, 96);
    public static Color AccentText => IsDark ? Lighten(Accent, 0.35f) : Accent;
    public static Color Hover      => IsDark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
    public static Color Success    => IsDark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(15, 123, 15);
    public static Color Danger     => IsDark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28);
    public const int Radius = 8;

    private static readonly string TextFamily    = PickFamily("Segoe UI Variable Text", "Segoe UI");
    private static readonly string DisplayFamily = PickFamily("Segoe UI Variable Display", "Segoe UI");

    public static Font Body(float size = 10f, FontStyle style = FontStyle.Regular) => new(TextFamily, size, style);
    public static Font Display(float size, FontStyle style = FontStyle.Bold) => new(DisplayFamily, size, style);

    // ── Apply ──────────────────────────────────────────────────────────────────

    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = Remap(form.Font);
        if (form.Icon is null || form.ShowIcon) form.Icon = IconManager.AppIcon;
        ApplyChildren(form);
        if (form.IsHandleCreated) ApplyWindowChrome(form.Handle);
        else form.HandleCreated += (_, _) => ApplyWindowChrome(form.Handle);
    }

    private static void ApplyChildren(Control parent)
    {
        // Theme controls that forms add later (e.g. dashboard cards rebuilt on refresh).
        if (Hooked.TryAdd(parent, new object()))
        {
            parent.ControlAdded += (_, e) => { if (e.Control is { } added) { ApplyControl(added); ApplyChildren(added); } };
            HonorPadding(parent);
        }
        foreach (Control c in parent.Controls)
        {
            ApplyControl(c);
            ApplyChildren(c);
        }
    }

    private static void ApplyControl(Control c)
    {
        c.Font = Remap(c.Font);
        switch (c)
        {
            case Button b:
                StyleButton(b);
                break;
            case LinkLabel l:
                l.LinkColor = AccentText; l.ActiveLinkColor = AccentText; l.VisitedLinkColor = AccentText;
                l.LinkBehavior = LinkBehavior.HoverUnderline;
                l.BackColor = Color.Transparent;
                break;
            case Label l when Equals(l.Tag, KeepColors):
                break; // account colours etc. are already chosen for both themes
            case Label l:
                l.ForeColor = MapFore(l.ForeColor);
                if (l.BackColor != Color.Transparent) l.BackColor = MapBack(l.BackColor);
                break;
            case TextBoxBase t:
                t.BackColor = Surface; t.ForeColor = Text;
                if (t is TextBox tb) tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Surface; cb.ForeColor = Text;
                break;
            case CheckBox or RadioButton:
                c.ForeColor = MapFore(c.ForeColor);
                if (c.BackColor != Color.Transparent) c.BackColor = MapBack(c.BackColor);
                ((ButtonBase)c).FlatStyle = FlatStyle.Standard; // System style ignores ForeColor (unreadable in dark mode)
                break;
            case TreeView tv:
                tv.BackColor = Surface; tv.ForeColor = Text; tv.BorderStyle = BorderStyle.None; tv.LineColor = Border;
                DarkScrollbars(tv);
                break;
            case ListView lv:
                lv.BackColor = Surface; lv.ForeColor = Text; lv.BorderStyle = BorderStyle.None;
                DarkScrollbars(lv);
                break;
            case ListBox lb:
                lb.BackColor = Surface; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.None;
                break;
            case DataGridView g:
                g.BackgroundColor = Surface; g.GridColor = Border; g.BorderStyle = BorderStyle.None;
                g.EnableHeadersVisualStyles = false;
                g.DefaultCellStyle.BackColor = Surface; g.DefaultCellStyle.ForeColor = Text;
                g.DefaultCellStyle.SelectionBackColor = Accent; g.DefaultCellStyle.SelectionForeColor = Color.White;
                g.ColumnHeadersDefaultCellStyle.BackColor = SurfaceAlt; g.ColumnHeadersDefaultCellStyle.ForeColor = SubtleText;
                g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
                break;
            case ProgressBar:
                break;
            default:
                if (c.BackColor != Color.Transparent) c.BackColor = MapBack(c.BackColor);
                c.ForeColor = MapFore(c.ForeColor);
                break;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Hooked = new();

    /// <summary>
    /// WinForms ignores a container's Padding for absolutely positioned children, so several windows drew
    /// their content flush against the left edge (looked clipped). Shift such children inside the padding.
    /// </summary>
    private static void HonorPadding(Control parent)
    {
        var pad = parent.Padding;
        if (pad.Left == 0) return;
        if (parent is FlowLayoutPanel or TableLayoutPanel or SplitterPanel) return;
        var kids = parent.Controls.Cast<Control>().Where(k => k.Dock == DockStyle.None).ToList();
        if (kids.Count == 0) return;
        var dx = Math.Max(0, pad.Left - kids.Min(k => k.Left));
        if (dx == 0) return; // horizontal only: shifting down would push bottom buttons out of fixed-size windows
        parent.SuspendLayout();
        foreach (var k in kids)
        {
            var anchorRight = (k.Anchor & AnchorStyles.Right) != 0 && (k.Anchor & AnchorStyles.Left) != 0;
            k.Left += dx;
            if (anchorRight && k.Width > dx * 2) k.Width -= dx * 2;
            else if (k.Right > parent.ClientSize.Width - pad.Right && k.Width > dx && k is not Button) k.Width -= Math.Min(dx, k.Right - (parent.ClientSize.Width - pad.Right));
        }
        parent.ResumeLayout();
    }

    public static void StyleButton(Button b, bool? primary = null)
    {
        var isPrimary = primary ?? IsAccentLike(b.BackColor);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = isPrimary ? 0 : 1;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = isPrimary ? Accent : Surface;
        b.ForeColor = isPrimary ? Color.White : Text;
        b.FlatAppearance.MouseOverBackColor = isPrimary ? Lighten(Accent, 0.12f) : Hover;
        b.FlatAppearance.MouseDownBackColor = isPrimary ? Darken(Accent, 0.12f) : Border;
        b.Cursor = Cursors.Hand;
        RoundCorners(b, 6);
    }

    /// <summary>Clips a control to a rounded rectangle and keeps it clipped on resize.</summary>
    public static void RoundCorners(Control c, int radius = Radius)
    {
        void Update()
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using var path = RoundedPath(new Rectangle(0, 0, c.Width, c.Height), radius);
            c.Region = new Region(path);
        }
        Update();
        c.Resize += (_, _) => Update();
    }

    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Paints a rounded card (fill + 1px border) behind a panel's children.</summary>
    public static void MakeCard(Panel p)
    {
        p.BackColor = Surface;
        p.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
            using var path = RoundedPath(r, Radius);
            using var pen = new Pen(Border);
            e.Graphics.DrawPath(pen, path);
        };
        RoundCorners(p, Radius);
    }

    // ── Color role mapping ─────────────────────────────────────────────────────

    public static Color MapBack(Color c)
    {
        if (c.A == 0) return c;
        if (IsAccentLike(c)) return Accent;
        var l = c.GetBrightness();
        if (c.R == 255 && c.G == 255 && c.B == 255) return Surface;
        if (l >= 0.90f) return Background;
        if (l >= 0.80f) return SurfaceAlt;
        return c;
    }

    /// <summary>Put this in a control's Tag to keep its own colours.</summary>
    public const string KeepColors = "keep-colors";

    public static Color MapFore(Color c)
    {
        if (c.A == 0) return c;
        if (c.R == 255 && c.G == 255 && c.B == 255) return c; // text on accent
        if (IsRed(c)) return Danger;
        if (IsGreen(c)) return Success;
        if (IsAccentLike(c)) return AccentText;
        var l = c.GetBrightness();
        if (l < 0.30f) return Text;
        if (l < 0.70f) return SubtleText;
        return c;
    }

    private static bool IsAccentLike(Color c) => c.B > 150 && c.B - c.R > 70 && c.B - c.G > 20;
    private static bool IsRed(Color c) => c.R > 150 && c.R - c.G > 80 && c.R - c.B > 80;
    private static bool IsGreen(Color c) => c.G > 110 && c.G - c.R > 60 && c.G - c.B > 30;

    public static Color Lighten(Color c, float f) => Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * f), (int)(c.G + (255 - c.G) * f), (int)(c.B + (255 - c.B) * f));
    public static Color Darken(Color c, float f) => Color.FromArgb(c.A, (int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));

    private static Font Remap(Font f)
    {
        var display = f.Name.Contains("Semibold", StringComparison.OrdinalIgnoreCase) || f.Size >= 14f;
        if (!f.Name.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase) && !f.Name.Equals("Microsoft Sans Serif", StringComparison.OrdinalIgnoreCase))
            return f;
        var style = f.Style | (f.Name.Contains("Semibold", StringComparison.OrdinalIgnoreCase) ? FontStyle.Bold : FontStyle.Regular);
        return new Font(display ? DisplayFamily : TextFamily, f.Size, style);
    }

    private static string PickFamily(string preferred, string fallback)
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        return fonts.Families.Any(ff => ff.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ? preferred : fallback;
    }

    // ── Windows integration ────────────────────────────────────────────────────

    internal static bool ReadIsDark(string? mode)
    {
        if (Environment.GetEnvironmentVariable("SYNCIT_THEME") is { } forced) return forced.Equals("dark", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(mode, "System", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static Color ReadAccent()
    {
        try
        {
            if (DwmGetColorizationColor(out var argb, out _) == 0)
            {
                var c = Color.FromArgb(255, Color.FromArgb((int)argb));
                // Keep contrast with white text: very light or grey accents fall back to Windows blue.
                if (c.GetBrightness() < 0.75f && c.GetSaturation() > 0.25f) return c;
            }
        }
        catch { }
        return Color.FromArgb(0, 103, 192);
    }

    private static void ApplyWindowChrome(IntPtr hwnd)
    {
        try
        {
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));     // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));    // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            int mica = 2;
            DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int));     // DWMWA_SYSTEMBACKDROP_TYPE = Mica (title bar)
            int caption = ColorTranslator.ToWin32(Background);
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));  // DWMWA_CAPTION_COLOR
        }
        catch { /* older Windows: ignore */ }
    }

    private static void DarkScrollbars(Control c)
    {
        if (!IsDark) return;
        void Set() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
        if (c.IsHandleCreated) Set(); else c.HandleCreated += (_, _) => Set();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetColorizationColor(out uint color, out bool opaque);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);
}

/// <summary>Fluent-style tray menu: rounded hover highlight, roomy items, theme colors.</summary>
internal sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
{
    private const int TextIndent = 22;

    public FluentMenuRenderer() : base(new FluentColors()) { RoundedEdges = true; }

    public static void Style(ContextMenuStrip menu)
    {
        menu.Renderer = new FluentMenuRenderer();
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Text;
        menu.Font = Theme.Body(9.5f);
        menu.Padding = new Padding(6, 6, 6, 6);
        menu.Opening += (_, _) =>
        {
            foreach (ToolStripItem item in menu.Items)
            {
                if (item is ToolStripSeparator) continue;
                item.Padding = new Padding(0, 6, 20 + TextIndent, 6);
                item.ForeColor = item.Enabled ? Theme.Text : Theme.SubtleText;
            }
        };
        menu.HandleCreated += (_, _) =>
        {
            try { int round = 2; DwmSetWindowAttribute(menu.Handle, 33, ref round, sizeof(int)); } catch { }
        };
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var path = Theme.RoundedPath(r, 4);
        using var b = new SolidBrush(Theme.Hover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.SubtleText;
        // Without an image margin WinForms draws item text ~3px from the menu edge (looks clipped).
        // Indent it to line up with the rounded hover highlight.
        if (e.Item.Owner is ToolStripDropDownMenu { ShowImageMargin: false, ShowCheckMargin: false })
        {
            var r = e.TextRectangle;
            e.TextRectangle = new Rectangle(r.X + TextIndent, r.Y, r.Width, r.Height);
            if (e.Item is ToolStripMenuItem { Checked: true })
            {
                // The check margin is hidden, so draw the checkmark inside the indent.
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var cy = e.Item.Height / 2;
                using var pen = new Pen(Theme.AccentText, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                e.Graphics.DrawLines(pen, new[] { new Point(10, cy), new Point(14, cy + 4), new Point(21, cy - 4) });
            }
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.AccentText, 2f);
        e.Graphics.DrawLines(pen, new[] { new Point(r.Left + 3, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4), new Point(r.Right - 3, r.Top + 4) });
    }

    private sealed class FluentColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
