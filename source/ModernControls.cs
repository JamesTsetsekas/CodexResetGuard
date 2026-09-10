using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexResetGuard
{
    internal sealed class Palette
    {
        public bool Dark;
        public Color Canvas, Surface, Hover, Border, Ink, Muted, Accent, AccentSoft, Warning, WarningSoft;
        public static Palette Create(bool dark)
        {
            return dark ? new Palette { Dark = true, Canvas = C(25, 28, 26), Surface = C(34, 38, 35), Hover = C(44, 49, 45), Border = C(57, 64, 58), Ink = C(237, 241, 237), Muted = C(160, 171, 162), Accent = C(157, 218, 178), AccentSoft = C(38, 61, 46), Warning = C(233, 191, 117), WarningSoft = C(58, 49, 32) }
                : new Palette { Canvas = C(246, 247, 244), Surface = C(255, 255, 253), Hover = C(235, 239, 233), Border = C(221, 227, 218), Ink = C(35, 43, 37), Muted = C(103, 116, 106), Accent = C(28, 103, 70), AccentSoft = C(228, 241, 232), Warning = C(139, 91, 25), WarningSoft = C(251, 242, 222) };
        }
        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
    }
    internal static class Appearance
    {
        private static string PathName { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexResetGuard", "appearance.json"); } }
        public static string Load()
        {
            try { string value = Data.Text(Data.Parse(File.ReadAllText(PathName)), "theme"); return value == "light" || value == "dark" ? value : "system"; }
            catch { return "system"; }
        }
        public static void Save(string value)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(PathName)); File.WriteAllText(PathName, Data.Json(new { theme = value })); }
            catch { throw new GuardException("The theme changed for this window, but the appearance preference could not be saved."); }
        }
        public static bool IsDark(string value)
        {
            if (value != "system") return value == "dark";
            try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0; }
            catch { return false; }
        }
    }
    internal interface IThemed { void ApplyPalette(Palette palette); }
    internal static class PaintKit
    {
        public static GraphicsPath Round(RectangleF bounds, float radius)
        {
            var p = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (d <= 0) { p.AddRectangle(bounds); return p; }
            p.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); p.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            p.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); p.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        public static void Box(Graphics g, RectangleF bounds, float radius, Color fill, Color border)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(bounds, radius)) using (var brush = new SolidBrush(fill)) { g.FillPath(brush, path); if (border != Color.Transparent) using (var pen = new Pen(border)) g.DrawPath(pen, path); }
        }
        public static Font Font(float size, bool bold) { return new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size, FontStyle.Regular); }
        public static void Text(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags extra)
        { TextRenderer.DrawText(g, text ?? "", font, bounds, color, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | extra); }
        public static void Theme(Control control, Palette p)
        {
            var themed = control as IThemed;
            if (themed != null) themed.ApplyPalette(p);
            else
            {
                control.BackColor = p.Canvas; control.ForeColor = Equals(control.Tag, "muted") ? p.Muted : p.Ink;
                if (control is TextBox || control is ListView) control.BackColor = p.Surface;
                var check = control as CheckBox; if (check != null) check.FlatStyle = FlatStyle.Flat;
            }
            foreach (Control child in control.Controls) Theme(child, p);
            control.Invalidate();
        }
    }
    internal enum ButtonTone { Standard, Primary, Quiet, Navigation }
    internal sealed class ModernLabel : Label, IThemed
    {
        private Palette palette = Palette.Create(false);
        public void ApplyPalette(Palette p) { palette = p; BackColor = p.Canvas; ForeColor = Equals(Tag, "muted") ? p.Muted : p.Ink; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            var flags = TextFormatFlags.WordBreak;
            if (TextAlign == ContentAlignment.MiddleLeft || TextAlign == ContentAlignment.MiddleCenter || TextAlign == ContentAlignment.MiddleRight) flags |= TextFormatFlags.VerticalCenter;
            if (TextAlign == ContentAlignment.TopCenter || TextAlign == ContentAlignment.MiddleCenter) flags |= TextFormatFlags.HorizontalCenter;
            PaintKit.Text(e.Graphics, Text, Font, palette.Muted, ClientRectangle, flags);
        }
    }
    internal sealed class ModernCheckBox : CheckBox, IThemed
    {
        private Palette palette = Palette.Create(false);
        public ModernCheckBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Cursor = Cursors.Hand; }
        public void ApplyPalette(Palette p) { palette = p; BackColor = p.Canvas; ForeColor = p.Ink; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            int size = (int)(15 * DeviceDpi / 96F), x = 2, y = (Height - size) / 2;
            PaintKit.Box(e.Graphics, new RectangleF(x, y, size, size), 3, Checked ? palette.Accent : palette.Surface, palette.Border);
            if (Checked) using (var pen = new Pen(palette.Dark ? palette.Canvas : Color.White, 2)) e.Graphics.DrawLines(pen, new[] { new Point(x + 3, y + size / 2), new Point(x + size / 2 - 1, y + size - 4), new Point(x + size - 3, y + 4) });
            PaintKit.Text(e.Graphics, Text, Font, Enabled ? palette.Ink : palette.Muted, new Rectangle(size + 10, 0, Width - size - 10, Height), TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(size + 8, 2, Width - size - 10, Height - 4), palette.Accent, palette.Canvas);
        }
    }
    internal sealed class ModernButton : Button, IThemed
    {
        private Palette palette = Palette.Create(false);
        private bool hover, pressed;
        public ButtonTone Tone;
        public bool Selected;
        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Height = 34; Cursor = Cursors.Hand; Font = PaintKit.Font(9.5F, true);
        }
        public void ApplyPalette(Palette p) { palette = p; BackColor = p.Canvas; ForeColor = p.Ink; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var p = palette; bool primary = Tone == ButtonTone.Primary;
            Color fill = primary ? p.Accent : Selected ? p.AccentSoft : Tone == ButtonTone.Quiet || Tone == ButtonTone.Navigation ? p.Canvas : p.Surface;
            Color ink = primary ? (p.Dark ? p.Canvas : Color.White) : Selected ? p.Accent : p.Ink;
            if (!Enabled) { fill = Tone == ButtonTone.Quiet ? p.Canvas : p.Hover; ink = p.Muted; }
            else if (hover || pressed) fill = primary ? (p.Dark ? Color.FromArgb(177, 231, 195) : Color.FromArgb(36, 123, 85)) : p.Hover;
            e.Graphics.Clear(p.Canvas);
            PaintKit.Box(e.Graphics, new RectangleF(1, 1, Width - 3, Height - 3), 6 * DeviceDpi / 96F, fill, Tone == ButtonTone.Standard ? p.Border : Color.Transparent);
            PaintKit.Text(e.Graphics, Text, Font, ink, new Rectangle(7, 0, Width - 14, Height - 1), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) { using (var path = PaintKit.Round(new RectangleF(4, 4, Width - 9, Height - 9), 4)) using (var pen = new Pen(p.Accent) { DashStyle = DashStyle.Dot }) e.Graphics.DrawPath(pen, path); }
        }
    }
    internal sealed class ModernCombo : ComboBox, IThemed
    {
        private Palette palette = Palette.Create(false);
        public ModernCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat; DrawMode = DrawMode.OwnerDrawFixed;
            Font = PaintKit.Font(9.5F, false); ItemHeight = 25; IntegralHeight = false; DropDownHeight = 210;
        }
        public void ApplyPalette(Palette p) { palette = p; BackColor = p.Surface; ForeColor = p.Ink; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var brush = new SolidBrush(selected ? palette.AccentSoft : palette.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            string text = e.Index < 0 || e.Index >= Items.Count ? "" : Items[e.Index].ToString();
            PaintKit.Text(e.Graphics, text, Font, !Enabled ? palette.Muted : selected ? palette.Accent : palette.Ink, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { if (DroppedDown) base.OnMouseWheel(e); else { var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true; } }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0xF && m.Msg != 0x317 && m.Msg != 0x318) return;
            if (Width < 5 || Height < 5) return;
            using (var graphics = (m.Msg == 0x317 || m.Msg == 0x318) && m.WParam != IntPtr.Zero ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle))
            {
                float scale = DeviceDpi / 96F; using (var background = new SolidBrush(palette.Canvas)) graphics.FillRectangle(background, ClientRectangle);
                PaintKit.Box(graphics, new RectangleF(1, 1, Width - 3, Height - 3), 6 * scale, palette.Surface, Focused ? palette.Accent : palette.Border);
                string label = SelectedIndex < 0 ? "" : Items[SelectedIndex].ToString();
                PaintKit.Text(graphics, label, Font, Enabled ? palette.Ink : palette.Muted, new Rectangle((int)(10 * scale), 0, Math.Max(1, Width - (int)(38 * scale)), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                float x = Width - 17 * scale, y = Height / 2F;
                using (var pen = new Pen(palette.Muted, 1.4F * scale)) graphics.DrawLines(pen, new[] { new PointF(x - 3 * scale, y - scale), new PointF(x, y + 2 * scale), new PointF(x + 3 * scale, y - scale) });
            }
        }
    }
    internal sealed class ModernNumber : UserControl, IThemed
    {
        private readonly TextBox input;
        private readonly ModernButton minus, plus;
        private Palette palette = Palette.Create(false);
        private decimal current;
        public decimal Minimum, Maximum;
        public int DecimalPlaces;
        public event EventHandler ValueChanged;
        public decimal Value
        {
            get { return current; }
            set { current = Math.Round(Math.Max(Minimum, Math.Min(Maximum, value)), DecimalPlaces); input.Text = current.ToString(DecimalPlaces == 0 ? "0" : "0.##", CultureInfo.CurrentCulture); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
        }
        public ModernNumber(decimal min, decimal max, decimal value, int places)
        {
            Minimum = min; Maximum = max; DecimalPlaces = places; Height = 34; Width = 138; TabStop = false; DoubleBuffered = true;
            input = new TextBox { BorderStyle = BorderStyle.None, Font = PaintKit.Font(10F, true), TextAlign = HorizontalAlignment.Left, AccessibleName = "Value" };
            minus = new ModernButton { Text = "−", Width = 27, TabStop = false, Tone = ButtonTone.Quiet, AccessibleName = "Decrease" };
            plus = new ModernButton { Text = "+", Width = 27, TabStop = false, Tone = ButtonTone.Quiet, AccessibleName = "Increase" };
            Controls.Add(input); Controls.Add(minus); Controls.Add(plus);
            minus.Click += delegate { Commit(); Value -= 1; }; plus.Click += delegate { Commit(); Value += 1; };
            input.Leave += delegate { Commit(); Invalidate(); }; input.Enter += delegate { Invalidate(); };
            input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) { Commit(); Value += e.KeyCode == Keys.Up ? 1 : -1; e.SuppressKeyPress = true; } };
            Value = value;
        }
        public bool Commit()
        {
            decimal value;
            if (!Decimal.TryParse(input.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) || value < Minimum || value > Maximum || value != Math.Round(value, DecimalPlaces)) { input.ForeColor = palette.Warning; return false; }
            Value = value; input.ForeColor = palette.Ink; return true;
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (input == null) return;
            int edge = (int)(8 * DeviceDpi / 96F), buttonWidth = (int)(27 * DeviceDpi / 96F);
            input.AccessibleName = AccessibleName ?? "Value"; minus.AccessibleName = "Decrease " + input.AccessibleName; plus.AccessibleName = "Increase " + input.AccessibleName;
            input.SetBounds(edge, Math.Max(2, (Height - input.PreferredHeight) / 2), Math.Max(20, Width - buttonWidth * 2 - edge * 2), input.PreferredHeight);
            minus.SetBounds(Width - buttonWidth * 2 - 2, 2, buttonWidth, Height - 4); plus.SetBounds(Width - buttonWidth - 2, 2, buttonWidth, Height - 4);
        }
        public void ApplyPalette(Palette p) { palette = p; BackColor = p.Canvas; input.BackColor = p.Surface; input.ForeColor = p.Ink; minus.ApplyPalette(p); plus.ApplyPalette(p); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) { PaintKit.Box(e.Graphics, new RectangleF(1, 1, Width - 3, Height - 3), 6, palette.Surface, input.Focused ? palette.Accent : palette.Border); }
    }
    internal sealed class NavigationTabs : Panel, IThemed
    {
        private readonly TableLayoutPanel navigation;
        private readonly Panel body;
        private readonly List<ModernButton> buttons = new List<ModernButton>();
        public readonly List<Panel> TabPages = new List<Panel>();
        private int selected;
        public event EventHandler SelectedIndexChanged;
        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < 0 || value >= TabPages.Count) return; selected = value;
                for (int i = 0; i < TabPages.Count; i++) { TabPages[i].Visible = i == value; buttons[i].Selected = i == value; buttons[i].Invalidate(); }
                TabPages[value].BringToFront(); if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }
        public NavigationTabs()
        {
            navigation = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 0, 0, 10), Margin = Padding.Empty };
            for (int i = 0; i < 4; i++) navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            Controls.Add(body); Controls.Add(navigation);
        }
        public Panel AddPage(string title)
        {
            int index = TabPages.Count; var page = new Panel { Dock = DockStyle.Fill, Visible = index == 0, Margin = Padding.Empty }; TabPages.Add(page); body.Controls.Add(page);
            var button = new ModernButton { Text = title, Tone = ButtonTone.Navigation, Selected = index == 0, Dock = DockStyle.Fill, Margin = new Padding(0, 0, index < 3 ? 5 : 0, 0), AccessibleRole = AccessibleRole.PageTab };
            button.Click += delegate { SelectedIndex = index; }; buttons.Add(button); navigation.Controls.Add(button, index, 0); return page;
        }
        public void ApplyPalette(Palette p) { BackColor = body.BackColor = navigation.BackColor = p.Canvas; }
    }
    internal sealed class UsageSummary : Control, IThemed
    {
        private Palette palette = Palette.Create(false);
        public Snapshot Snapshot;
        public int Approved;
        public double Threshold = 95;
        public UsageSummary() { DoubleBuffered = true; AccessibleRole = AccessibleRole.StaticText; }
        public void ApplyPalette(Palette p) { palette = p; BackColor = p.Canvas; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var p = palette; float scale = DeviceDpi / 96F;
            Func<int, int> d = v => (int)(v * scale);
            PaintKit.Box(g, new RectangleF(1, 1, Width - 3, Height - 3), d(10), p.Surface, p.Border);
            int split = (int)(Width * .68), left = d(17), right = split - d(17);
            using (var line = new Pen(p.Border)) g.DrawLine(line, split, d(18), split, Height - d(18));
            using (var small = PaintKit.Font(9, false)) using (var large = PaintKit.Font(29, true)) using (var caption = PaintKit.Font(8.5F, false))
            {
                var w = Snapshot == null ? null : Snapshot.Weekly;
                PaintKit.Text(g, "Weekly usage", small, p.Muted, new Rectangle(left, d(12), right - left, d(20)), TextFormatFlags.SingleLine);
                PaintKit.Text(g, w == null ? "—" : Data.Percent(w.Used), large, p.Ink, new Rectangle(left - d(1), d(32), right - left, d(45)), TextFormatFlags.SingleLine);
                string remaining = w == null ? "Waiting for Codex" : Data.Percent(100 - w.Used) + " remaining";
                PaintKit.Text(g, remaining, small, p.Muted, new Rectangle(left, d(58), right - left, d(20)), TextFormatFlags.Right | TextFormatFlags.SingleLine);
                int y = d(85), width = right - left; double used = w == null ? 0 : Math.Max(0, Math.Min(100, w.Used));
                PaintKit.Box(g, new RectangleF(left, y, width, d(5)), d(2), p.Hover, Color.Transparent);
                if (used > 0) PaintKit.Box(g, new RectangleF(left, y, Math.Max(d(2), (float)(width * used / 100)), d(5)), d(2), used >= Threshold ? p.Warning : p.Accent, Color.Transparent);
                using (var pen = new Pen(p.Muted)) { float marker = left + (float)(width * Threshold / 100); g.DrawLine(pen, marker, y - d(3), marker, y + d(8)); }
                string reset = "Resets " + (w == null || !w.ResetsAt.HasValue ? "not reported" : ShortDate(w.ResetsAt.Value));
                PaintKit.Text(g, reset, caption, p.Muted, new Rectangle(left, d(99), width, d(18)), TextFormatFlags.SingleLine);
                var five = Snapshot == null ? null : Snapshot.Windows.Find(x => x.Minutes == 300);
                PaintKit.Text(g, five == null ? "Trigger at " + Data.Percent(Threshold) + " used" : "5-hour usage  " + Data.Percent(five.Used) + " used", caption, p.Muted, new Rectangle(left, d(119), width, d(18)), TextFormatFlags.SingleLine);
                int creditX = split + d(18), creditWidth = Width - creditX - d(12);
                PaintKit.Text(g, "Reset credits", small, p.Muted, new Rectangle(creditX, d(12), creditWidth, d(20)), TextFormatFlags.SingleLine);
                PaintKit.Text(g, Snapshot == null || !Snapshot.AvailableCount.HasValue ? "—" : Snapshot.AvailableCount.Value.ToString(), large, p.Ink, new Rectangle(creditX, d(32), creditWidth, d(45)), TextFormatFlags.SingleLine);
                PaintKit.Text(g, Approved + " allowed", small, p.Accent, new Rectangle(creditX, d(83), creditWidth, d(20)), TextFormatFlags.SingleLine);
                PaintKit.Text(g, "Your banked resets", caption, p.Muted, new Rectangle(creditX, d(119), creditWidth, d(18)), TextFormatFlags.SingleLine);
            }
        }
        public static string ShortDate(long time) { try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(time).ToLocalTime().ToString("MMM d · h:mm tt"); } catch (ArgumentOutOfRangeException) { return "not reported"; } }
    }
    internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Palette palette;
        public ModernMenuRenderer(Palette p) { palette = p; RoundedEdges = false; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(palette.Surface); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var pen = new Pen(palette.Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) { if (e.Item.Selected && e.Item.Enabled) PaintKit.Box(e.Graphics, new RectangleF(3, 1, e.Item.Width - 7, e.Item.Height - 2), 4, palette.AccentSoft, Color.Transparent); }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? palette.Ink : palette.Muted; base.OnRenderItemText(e); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = palette.Muted; base.OnRenderArrow(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using (var pen = new Pen(palette.Border)) e.Graphics.DrawLine(pen, 9, e.Item.Height / 2, e.Item.Width - 9, e.Item.Height / 2); }
    }
}
