using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VerjotNotes;


// Shared visual roles; native input, menu navigation and DPI ownership stay
// with their existing controls. No polling or application-wide repaint hook.
internal static class VerjotUi {
    internal static Color Surface { get { return SystemInformation.HighContrast ? SystemColors.Window : Color.White; } }
    internal static Color Ink { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(34, 46, 63); } }
    internal static Color Muted { get { return SystemInformation.HighContrast ? SystemColors.GrayText : Color.FromArgb(85, 101, 123); } }
    internal static Color Accent { get { return SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(40, 104, 168); } }
    internal static Color Hover { get { return SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(238, 245, 253); } }
    internal static Color Pressed { get { return SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(221, 234, 249); } }
    internal static Color Border { get { return SystemInformation.HighContrast ? SystemColors.WindowFrame : Color.FromArgb(218, 226, 235); } }
    internal static void StyleButton(Button button, bool primary) {
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? SystemColors.HighlightText : Ink;
        button.FlatAppearance.MouseOverBackColor = SystemInformation.HighContrast ? SystemColors.Highlight : primary ? Color.FromArgb(31, 85, 143) : Hover;
        button.MinimumSize = new Size(76, 28); button.Cursor = Cursors.Hand;
    }
    internal static void ToolBackground(Graphics graphics, Rectangle bounds, Color background, Color highlight, float amount, int dpi) {
        // Owner-drawn list rows share a surface. Clear only this component's
        // bounds, so an incremental hover repaint cannot erase its neighbours.
        using (var brush = new SolidBrush(background)) graphics.FillRectangle(brush, bounds);
        if (amount <= 0) return;
        Color color = Color.FromArgb((int)(background.R + (highlight.R - background.R) * amount),
            (int)(background.G + (highlight.G - background.G) * amount), (int)(background.B + (highlight.B - background.B) * amount));
        int inset = Math.Max(1, dpi / 96), diameter = Math.Min(Math.Min(bounds.Width, bounds.Height) - 2 * inset, 8 * dpi / 96);
        if (diameter <= 0) return;
        Rectangle r = Rectangle.Inflate(bounds, -inset, -inset);
        using (var path = new GraphicsPath()) using (var brush = new SolidBrush(color)) {
            path.AddArc(r.Left, r.Top, diameter, diameter, 180, 90);
            path.AddArc(r.Right - diameter, r.Top, diameter, diameter, 270, 90);
            path.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(r.Left, r.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); graphics.FillPath(brush, path);
        }
    }
}

// CheckBox preserves the existing boolean binding; radio semantics describe
// the two mutually exclusive modes to assistive technology.
internal sealed class VerjotModeButton : CheckBox {
    bool hovered;
    internal VerjotModeButton() {
        AutoCheck = false; Appearance = Appearance.Button; FlatStyle = FlatStyle.Flat;
        AccessibleRole = AccessibleRole.RadioButton; TextAlign = ContentAlignment.MiddleCenter;
        Dock = DockStyle.Fill; Margin = Padding.Empty; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e) {
        if (Parent != null && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)) {
            foreach (Control child in Parent.Controls) {
                var next = child as VerjotModeButton;
                if (next != null && next != this && next.Enabled && next.Visible) {
                    next.Focus(); next.OnClick(EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; return;
                }
            }
        }
        base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e) {
        Color fill = Checked || hovered ? VerjotUi.Hover : VerjotUi.Surface;
        Color ink = (Checked || hovered) && SystemInformation.HighContrast ? SystemColors.HighlightText : Checked ? VerjotUi.Accent : VerjotUi.Ink;
        e.Graphics.Clear(fill);
        using (var pen = new Pen(Checked ? VerjotUi.Accent : VerjotUi.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
    }
}

// One renderer for app-owned menus. Native menu navigation, focus and dismissal
// stay with WinForms; no animation timer or editor repaint is involved.
internal sealed class VerjotMenu : ContextMenuStrip {
    Font menuFont;
    int menuDpi;
    public VerjotMenu() { ApplyStyle(96); }
    public VerjotMenu(System.ComponentModel.IContainer owner) : base(owner) { ApplyStyle(96); }
    [StructLayout(LayoutKind.Sequential)] struct PointValue { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(PointValue point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    protected override void OnOpening(System.ComponentModel.CancelEventArgs e) {
        base.OnOpening(e); // Callers populate dynamic recent-meeting items here.
        int dpi = SourceControl == null ? 96 : SourceControl.DeviceDpi;
        if (SourceControl == null) try {
            Point point = Cursor.Position; uint x, y;
            if (GetDpiForMonitor(MonitorFromPoint(new PointValue { X = point.X, Y = point.Y }, 2), 0, out x, out y) == 0) dpi = (int)x;
        } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        ApplyStyle(dpi);
    }
    internal void ApplyStyle(int dpi) {
        dpi = Math.Max(96, Math.Min(480, dpi));
        Font previous = null;
        if (menuFont == null || menuDpi != dpi) {
            previous = menuFont; menuDpi = dpi;
            menuFont = new Font("Microsoft YaHei UI", 12f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        StyleDropDown(this);
        if (previous != null) previous.Dispose();
    }
    void StyleDropDown(ToolStripDropDown menu) {
        bool first = !(menu.Renderer is VerjotMenuRenderer);
        menu.Renderer = VerjotMenuRenderer.Shared; menu.Font = menuFont;
        menu.BackColor = VerjotMenuRenderer.Surface; menu.ForeColor = VerjotMenuRenderer.Ink;
        int pad = Math.Max(1, 4 * menuDpi / 96);
        // A hosted search/list popup owns its inner layout and must not be resized.
        bool hosted = menu.Items.Count == 1 && menu.Items[0] is ToolStripControlHost;
        if (hosted) {
            // The native menu layout reserves an extra command/arrow gutter
            // even with image margins disabled. A hosted picker owns every pixel.
            menu.AutoSize = false; menu.MinimumSize = menu.MaximumSize = Size.Empty;
            menu.ClientSize = new Size(menu.Items[0].Width + menu.Padding.Horizontal, menu.Items[0].Height + menu.Padding.Vertical);
        }
        if (!hosted) {
            menu.Padding = new Padding(pad);
            int width = 156 * menuDpi / 96;
            foreach (ToolStripItem item in menu.Items) if (item.Available && item is ToolStripMenuItem) {
                var command = (ToolStripMenuItem)item;
                string keys = command.ShortcutKeyDisplayString ?? "";
                width = Math.Max(width, TextRenderer.MeasureText(item.Text + keys, menuFont).Width + (keys.Length == 0 ? 48 : 64) * menuDpi / 96);
            }
            int available = Screen.FromPoint(Cursor.Position).WorkingArea.Width - pad * 2;
            width = Math.Max(80, Math.Min(width, Math.Min(360 * menuDpi / 96, available)));
            menu.MaximumSize = new Size(width + pad * 2, Math.Max(40, Screen.FromPoint(Cursor.Position).WorkingArea.Height - pad * 2));
            foreach (ToolStripItem item in menu.Items) {
                if (!(item is ToolStripMenuItem) && !(item is ToolStripSeparator)) continue;
                item.AutoSize = false; item.Margin = Padding.Empty;
                item.Size = new Size(width, item is ToolStripSeparator ? 7 * menuDpi / 96 : Math.Max(26 * menuDpi / 96, menuFont.Height + 8 * menuDpi / 96));
            }
        }
        if (first && menu != this) menu.Opening += delegate { if (!IsDisposed) StyleDropDown(menu); };
        foreach (ToolStripItem item in menu.Items) {
            var parent = item as ToolStripMenuItem;
            if (parent != null && parent.HasDropDownItems) StyleDropDown(parent.DropDown);
        }
        menu.PerformLayout();
    }
    internal static void AttachTextMenu(TextBoxBase field) {
        if (field.ContextMenuStrip != null) return;
        var menu = new VerjotMenu();
        var undo = menu.Items.Add("撤销", null, delegate { field.Undo(); });
        menu.Items.Add(new ToolStripSeparator());
        var cut = menu.Items.Add("剪切", null, delegate { field.Cut(); });
        var copy = menu.Items.Add("复制", null, delegate { field.Copy(); });
        var paste = menu.Items.Add("粘贴", null, delegate { field.Paste(); });
        menu.Items.Add(new ToolStripSeparator());
        var all = menu.Items.Add("全选", null, delegate { field.SelectAll(); });
        ((ToolStripMenuItem)undo).ShortcutKeyDisplayString = "Ctrl+Z";
        ((ToolStripMenuItem)cut).ShortcutKeyDisplayString = "Ctrl+X";
        ((ToolStripMenuItem)copy).ShortcutKeyDisplayString = "Ctrl+C";
        ((ToolStripMenuItem)paste).ShortcutKeyDisplayString = "Ctrl+V";
        ((ToolStripMenuItem)all).ShortcutKeyDisplayString = "Ctrl+A";
        menu.Opening += delegate {
            bool editable = !field.ReadOnly && field.Enabled;
            var text = field as TextBox;
            bool secret = text != null && (text.UseSystemPasswordChar || text.PasswordChar != '\0');
            undo.Enabled = editable && field.CanUndo;
            undo.Visible = undo.Enabled;
            menu.Items[1].Visible = undo.Enabled;
            cut.Enabled = editable && !secret && field.SelectionLength > 0;
            copy.Enabled = !secret && field.SelectionLength > 0;
            paste.Enabled = editable; // Never inspect the user's clipboard on hover/open.
            cut.Visible = editable;
            paste.Visible = editable;
            all.Enabled = field.TextLength > 0;
        };
        field.ContextMenuStrip = menu;
        field.Disposed += delegate { menu.Dispose(); };
    }
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (disposing && menuFont != null) { menuFont.Dispose(); menuFont = null; }
    }
}

internal sealed class VerjotMenuRenderer : ToolStripProfessionalRenderer {
    internal static readonly VerjotMenuRenderer Shared = new VerjotMenuRenderer();
    internal static Color Surface { get { return SystemInformation.HighContrast ? SystemColors.Menu : Color.White; } }
    internal static Color Ink { get { return SystemInformation.HighContrast ? SystemColors.MenuText : VerjotUi.Ink; } }
    internal static Color Muted { get { return SystemInformation.HighContrast ? SystemColors.GrayText : VerjotUi.Muted; } }
    internal static Color Accent { get { return SystemInformation.HighContrast ? SystemColors.MenuText : VerjotUi.Accent; } }
    internal static Color Hover { get { return SystemInformation.HighContrast ? SystemColors.Highlight : VerjotUi.Hover; } }
    internal static readonly Color Danger = Color.FromArgb(174, 47, 47);
    VerjotMenuRenderer() { RoundedEdges = false; }
    static float Scale(ToolStrip strip) { return strip.Font.Size / 12f; }
    static bool Destructive(ToolStripItem item) { return item.Name == "deleteNote"; }
    static Color Foreground(ToolStripItem item) {
        if (!item.Enabled) return Muted;
        if (SystemInformation.HighContrast && item.Selected) return SystemColors.HighlightText;
        return Destructive(item) && !SystemInformation.HighContrast ? Danger : Ink;
    }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Surface); }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) {
        using (var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowFrame : VerjotUi.Border))
            e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, e.ToolStrip.Width - 1), Math.Max(0, e.ToolStrip.Height - 1));
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
        if (!e.Item.Enabled || !e.Item.Selected) return;
        int inset = Math.Max(1, (int)Math.Round(Scale(e.ToolStrip) * 2));
        Rectangle bounds = new Rectangle(inset, 1, Math.Max(1, e.Item.Width - inset * 2), e.Item.Height - 2);
        using (var brush = new SolidBrush(Destructive(e.Item) && !SystemInformation.HighContrast ? Color.FromArgb(253, 239, 239) : Hover))
            e.Graphics.FillRectangle(brush, bounds);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
        int inset = Math.Max(4, (int)Math.Round(10 * Scale(e.ToolStrip)));
        using (var pen = new Pen(SystemInformation.HighContrast ? SystemColors.GrayText : Color.FromArgb(232, 237, 243)))
            e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
        Rectangle bounds = Rectangle.Intersect(e.TextRectangle, new Rectangle(0, 0, e.Item.Width - (int)(16 * Scale(e.ToolStrip)), e.Item.Height));
        string text = e.Text ?? ""; string shortcut = null;
        int split = text.LastIndexOf(" · ", StringComparison.Ordinal);
        if (split >= 0 && Regex.IsMatch(text.Substring(split + 3), @"^(?:(?:Ctrl|Alt|Shift)\+)+[A-Za-z0-9]+$")) {
            shortcut = text.Substring(split + 3); text = text.Substring(0, split);
        }
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        flags |= e.TextFormat & (TextFormatFlags.Right | TextFormatFlags.RightToLeft);
        if ((e.TextFormat & TextFormatFlags.HidePrefix) != 0) flags |= TextFormatFlags.HidePrefix;
        if (shortcut != null) {
            int width = TextRenderer.MeasureText(e.Graphics, shortcut, e.TextFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            Rectangle key = new Rectangle(Math.Max(bounds.Left, bounds.Right - width), bounds.Top, Math.Min(width, bounds.Width), bounds.Height);
            TextRenderer.DrawText(e.Graphics, shortcut, e.TextFont, key, SystemInformation.HighContrast && e.Item.Selected ? Foreground(e.Item) : Muted, flags | TextFormatFlags.Right);
            bounds.Width = Math.Max(0, key.Left - bounds.Left - (int)(20 * Scale(e.ToolStrip)));
        }
        TextRenderer.DrawText(e.Graphics, text, e.TextFont, bounds, Foreground(e.Item), flags);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
        float scale = Scale(e.Item.Owner);
        // WinForms may retain a preferred-width arrow rectangle after a very
        // long label is constrained. Anchor to the actual visible item instead.
        float cx = e.Item.Width - 12 * scale, cy = e.Item.Height / 2f;
        float direction = e.Direction == ArrowDirection.Left ? -1 : 1;
        var old = e.Graphics.SmoothingMode; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Foreground(e.Item), Math.Max(1, 1.4f * scale)))
            e.Graphics.DrawLines(pen, new[] { new PointF(cx - direction * 2 * scale, cy - 3 * scale), new PointF(cx + direction * 1.5f * scale, cy), new PointF(cx - direction * 2 * scale, cy + 3 * scale) });
        e.Graphics.SmoothingMode = old;
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
        float scale = Scale(e.ToolStrip), cx = e.ImageRectangle.Left + e.ImageRectangle.Width / 2f, cy = e.Item.Height / 2f;
        var old = e.Graphics.SmoothingMode; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(SystemInformation.HighContrast ? Foreground(e.Item) : Accent, Math.Max(1, 1.7f * scale)))
            e.Graphics.DrawLines(pen, new[] { new PointF(cx - 4 * scale, cy), new PointF(cx - 1 * scale, cy + 3 * scale), new PointF(cx + 5 * scale, cy - 4 * scale) });
        e.Graphics.SmoothingMode = old;
    }
}
