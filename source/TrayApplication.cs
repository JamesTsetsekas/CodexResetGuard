using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexResetGuard
{
    internal static class TrayIcons
    {
        public static Icon Create(Color color)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var pen = new Pen(color, 4))
            using (var brush = new SolidBrush(color))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.Clear(Color.Transparent);
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                graphics.DrawArc(pen, 5, 5, 22, 22, 35, 280);
                graphics.FillPolygon(brush, new[] { new PointF(25, 4), new PointF(30, 14), new PointF(19, 13) });
                graphics.FillEllipse(brush, 12, 12, 8, 8);
                IntPtr handle = bitmap.GetHicon(); try { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); } finally { DestroyIcon(handle); }
            }
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    }
    internal sealed class TrayApplication : ApplicationContext
    {
        private readonly ResetEngine engine;
        private readonly StateStore store;
        private readonly NotifyIcon tray;
        private readonly Icon offIcon = TrayIcons.Create(Color.FromArgb(112, 124, 136));
        private readonly Icon onIcon = TrayIcons.Create(GuardForm.Accent);
        private readonly Icon pendingIcon = TrayIcons.Create(Color.FromArgb(183, 113, 20));
        private readonly System.Windows.Forms.Timer timer;
        private readonly EventWaitHandle showEvent;
        private readonly Font menuFont = PaintKit.Font(9.5F, false);
        private GuardForm form;
        private long nextCheck;
        private bool exiting;
        public TrayApplication(ResetEngine engine, StateStore store, EventWaitHandle showEvent, bool show)
        {
            this.engine = engine; this.store = store; this.showEvent = showEvent;
            // Create a Windows Forms synchronization context before starting asynchronous work.
            form = NewForm(); IntPtr handle = form.Handle;
            tray = new NotifyIcon { Icon = offIcon, Visible = true, Text = "Codex Reset Guard · Auto off" };
            tray.DoubleClick += delegate { ShowSettings(); };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowSettings(); };
            tray.ContextMenuStrip = new ContextMenuStrip(); tray.ContextMenuStrip.Opening += delegate { BuildMenu(); };
            engine.Changed += delegate { if (!exiting) { UpdateTray(); if (form != null) form.UpdateView(); } };
            engine.Notice += delegate(string title, string text) { if (!exiting) tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Info); };
            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += async delegate
            {
                if (showEvent.WaitOne(0)) ShowSettings();
                if (!engine.Busy && Data.Now >= nextCheck) await RefreshAsync(false);
            };
            timer.Start(); if (show) ShowSettings();
        }
        private GuardForm NewForm()
        {
            var f = new GuardForm(engine, async delegate { await RefreshAsync(false); }, async delegate { await RefreshAsync(true); }, delegate { store.Save(engine.State); });
            f.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; f.Hide(); }
            };
            return f;
        }
        public void ShowSettings()
        {
            if (form == null || form.IsDisposed) form = NewForm();
            form.Show(); if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal; form.Activate();
        }
        private async Task RefreshAsync(bool retry)
        {
            nextCheck = Data.Now + 300;
            await engine.CheckAsync(retry);
            nextCheck = Data.Now + engine.PollSeconds;
        }
        private void UpdateTray()
        {
            tray.Icon = engine.State.Pending != null ? pendingIcon : engine.State.Enabled ? onIcon : offIcon;
            string pct = engine.Latest == null || engine.Latest.Weekly == null ? "usage unavailable" : Data.Percent(engine.Latest.Weekly.Used) + " weekly used";
            string text = "Reset Guard · " + (engine.State.Enabled ? "Auto on" : "Auto off") + " · " + pct;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        private void BuildMenu()
        {
            var menu = tray.ContextMenuStrip; menu.Items.Clear();
            menu.Renderer = new ModernMenuRenderer(form.CurrentPalette); menu.Font = menuFont;
            menu.BackColor = form.CurrentPalette.Surface; menu.ForeColor = form.CurrentPalette.Ink;
            menu.ShowImageMargin = false; menu.ShowCheckMargin = true; menu.Padding = new Padding(4);
            menu.Items.Add("Codex Reset Guard", null, delegate { ShowSettings(); });
            menu.Items.Add(new ToolStripMenuItem(engine.State.Enabled ? "Automatic resets enabled" : "Automatic resets off") { Enabled = false });
            if (engine.Latest != null && engine.Latest.Weekly != null) menu.Items.Add(new ToolStripMenuItem(Data.Percent(engine.Latest.Weekly.Used) + " weekly used · " + (engine.Latest.AvailableCount.HasValue ? engine.Latest.AvailableCount.Value.ToString() : "?") + " credits") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            if (engine.State.Enabled) menu.Items.Add("Pause automatic resets", null, delegate { form.Try(engine.Pause); });
            else menu.Items.Add("Enable with saved rules…", null, async delegate
            {
                ShowSettings();
                if (engine.State.Rules.AllowedCreditIds.Count == 0 || engine.State.Pending != null) return;
                await RefreshAsync(false); form.Try(delegate { engine.Arm(engine.State.Rules); });
            });
            var levels = new ToolStripMenuItem("Threshold: " + Data.Percent(engine.State.Rules.Threshold) + " used") { Enabled = !engine.State.Enabled && engine.State.Pending == null };
            foreach (double amount in new[] { 95.0, 97.0, 98.0, 99.0, 99.99 })
            {
                double selected = amount;
                var item = new ToolStripMenuItem(Data.Percent(amount) + (amount == 95 ? " (recommended)" : amount == 99.99 ? " (reported 100%)" : "")) { Checked = engine.State.Rules.Threshold == amount };
                item.Click += delegate
                {
                    form.Try(delegate
                    {
                        var copy = Data.Serializer().Deserialize<Rules>(Data.Json(engine.State.Rules)); copy.Threshold = selected; engine.SaveRules(copy);
                        if (form != null && !form.IsDisposed) { form.Dispose(); form = NewForm(); }
                    });
                }; levels.DropDownItems.Add(item);
            }
            menu.Items.Add(levels); menu.Items.Add("Choose allowed reset credits…", null, delegate { ShowSettings(); form.Tabs.SelectedIndex = 1; });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings and activity…", null, delegate { ShowSettings(); });
            var refresh = new ToolStripMenuItem("Refresh now", null, async delegate { await RefreshAsync(false); }) { Enabled = !engine.Busy }; menu.Items.Add(refresh);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Quit(); });
            foreach (ToolStripItem item in menu.Items) item.Padding = new Padding(7, 5, 7, 5);
        }
        private void Quit()
        {
            exiting = true; timer.Stop(); tray.Visible = false; tray.Dispose(); if (form != null) form.Dispose();
            offIcon.Dispose(); onIcon.Dispose(); pendingIcon.Dispose(); ExitThread();
        }
        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); showEvent.Dispose(); menuFont.Dispose(); } base.Dispose(disposing); }
    }
}
