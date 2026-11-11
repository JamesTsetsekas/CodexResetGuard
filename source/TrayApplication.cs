using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

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
        // The taskbar follows the system theme, which can differ from the app theme.
        public static bool DarkTaskbar
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return key == null || Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0)) == 0; }
                catch { return true; }
            }
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    }
    internal sealed class TrayApplication : ApplicationContext
    {
        private readonly ResetEngine engine;
        private readonly ClaudeMonitor claude;
        private readonly StateStore store;
        private readonly NotifyIcon tray;
        private Icon offIcon, onIcon, pendingIcon, alertIcon;
        private bool iconsDark;
        private readonly System.Windows.Forms.Timer timer;
        private readonly EventWaitHandle showEvent;
        private readonly Stopwatch monotonic = Stopwatch.StartNew(); // Polling survives wall-clock corrections.
        private readonly Dictionary<string, long> lastBalloon = new Dictionary<string, long>();
        private GuardForm form;
        private long nextCheck, nextClaudeCheck;
        private bool exiting;
        private int balloonTab = -1;
        private long Mono { get { return monotonic.ElapsedMilliseconds / 1000; } }
        public TrayApplication(ResetEngine engine, ClaudeMonitor claude, StateStore store, EventWaitHandle showEvent, bool show)
        {
            this.engine = engine; this.claude = claude; this.store = store; this.showEvent = showEvent;
            CreateIcons();
            // Create a Windows Forms synchronization context before starting asynchronous work.
            form = NewForm(); IntPtr handle = form.Handle;
            tray = new NotifyIcon { Icon = offIcon, Visible = true, Text = "Codex Reset Guard" };
            tray.DoubleClick += delegate { ShowSettings(); };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowSettings(); };
            tray.ContextMenuStrip = new ContextMenuStrip();
            // An empty strip starts with Cancel set; clear it so the first right-click opens the menu.
            tray.ContextMenuStrip.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { BuildMenu(); e.Cancel = false; };
            engine.Changed += delegate { if (!exiting) { UpdateTray(); if (form != null) form.UpdateView(); } };
            engine.Notice += delegate(string title, string text) { Balloon(title, text, title.Contains("verified") ? ToolTipIcon.Info : ToolTipIcon.Warning, GuardForm.ActivityTab); };
            claude.Changed += delegate { if (!exiting) { UpdateTray(); if (form != null && !form.IsDisposed) form.UpdateView(); } };
            claude.Notice += delegate(string title, string text) { Balloon(title, text, ToolTipIcon.Warning, GuardForm.ClaudeTab); };
            tray.BalloonTipClicked += delegate { ShowSettings(balloonTab); balloonTab = -1; };
            SystemEvents.UserPreferenceChanged += PreferenceChanged;
            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += async delegate
            {
                if (showEvent.WaitOne(0)) ShowSettings();
                if (!engine.Busy && Mono >= nextCheck) await RefreshAsync(false);
                if (claude.Settings.Enabled && !claude.Busy && Mono >= nextClaudeCheck) await RefreshClaudeAsync();
            };
            UpdateTray(); timer.Start(); if (show) ShowSettings();
        }
        private void CreateIcons()
        {
            iconsDark = TrayIcons.DarkTaskbar; var old = new[] { offIcon, onIcon, pendingIcon, alertIcon };
            offIcon = TrayIcons.Create(iconsDark ? Color.FromArgb(170, 180, 188) : Color.FromArgb(112, 124, 136));
            onIcon = TrayIcons.Create(iconsDark ? Color.FromArgb(120, 214, 156) : GuardForm.Accent);
            pendingIcon = TrayIcons.Create(iconsDark ? Color.FromArgb(240, 180, 80) : Color.FromArgb(183, 113, 20));
            alertIcon = TrayIcons.Create(iconsDark ? Color.FromArgb(240, 120, 100) : Color.FromArgb(178, 54, 38));
            foreach (var icon in old) if (icon != null) icon.Dispose();
        }
        private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (exiting || TrayIcons.DarkTaskbar == iconsDark) return;
            try { form.BeginInvoke(new Action(delegate { if (exiting) return; CreateIcons(); UpdateTray(); })); } catch (InvalidOperationException) { }
        }
        private void Balloon(string title, string text, ToolTipIcon icon, int tab)
        {
            if (exiting) return;
            long last; long now = Mono;
            // Attention balloons are rate-limited so a flapping connection does not repeat them every minute.
            if (icon == ToolTipIcon.Warning && title == "Reset Guard needs attention" && lastBalloon.TryGetValue(title, out last) && now - last < 900) return;
            lastBalloon[title] = now; balloonTab = tab; tray.ShowBalloonTip(icon == ToolTipIcon.Warning ? 9000 : 6000, title, text, icon);
        }
        private GuardForm NewForm()
        {
            var f = new GuardForm(engine, claude, async delegate { await RefreshAsync(false); await RefreshClaudeAsync(); }, async delegate { await RefreshClaudeAsync(); }, async delegate { await RefreshAsync(true); }, delegate { engine.SaveSettings(); });
            f.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; f.Hide(); }
            };
            return f;
        }
        public void ShowSettings() { ShowSettings(-1); }
        public void ShowSettings(int tab)
        {
            if (form == null || form.IsDisposed) form = NewForm();
            form.Show(); if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal; form.Activate();
            if (tab >= 0) form.Tabs.SelectedIndex = tab;
        }
        private async Task RefreshClaudeAsync()
        {
            if (!claude.Settings.Enabled || claude.Busy) return;
            nextClaudeCheck = Mono + 300;
            await claude.CheckAsync();
            nextClaudeCheck = Mono + claude.PollSeconds;
        }
        private async Task RefreshAsync(bool retry)
        {
            nextCheck = Mono + 300;
            await engine.CheckAsync(retry);
            nextCheck = Mono + engine.PollSeconds;
        }
        internal static string Describe(ResetEngine engine)
        {
            var s = engine.State;
            if (s.Pending != null) return s.Pending.Acknowledged ? "Confirming reset" : "Reset pending";
            if (engine.Failures > 0) return "Not connected";
            if (s.Enabled && engine.LastDecision != null && engine.LastDecision.Blocked) return "On · blocked";
            return s.Enabled ? "Auto on" : "Auto off";
        }
        private void UpdateTray()
        {
            var s = engine.State;
            bool blocked = s.Enabled && engine.LastDecision != null && engine.LastDecision.Blocked;
            tray.Icon = s.Pending != null ? pendingIcon : (engine.Failures > 0 && (s.Enabled || s.Pending != null)) || blocked ? alertIcon : s.Enabled ? onIcon : offIcon;
            string pct = "Codex —";
            if (engine.Latest != null && engine.Latest.Windows.Count > 0)
            {
                var parts = new List<string>();
                var week = engine.Latest.Weekly; var five = engine.Latest.Windows.Find(w => w.Minutes == 300);
                if (week != null) parts.Add(Data.Percent(week.Used) + " wk"); if (five != null) parts.Add(Data.Percent(five.Used) + " 5h");
                pct = "Codex " + String.Join(" ", parts.ToArray());
            }
            var c = claude.Settings.Enabled && claude.Failures == 0 ? claude.Latest : null; // Never show stale Claude numbers as current.
            if (claude.Settings.Enabled && claude.Failures > 0) pct += " · Claude ?";
            if (c != null) { var hi = c.Windows.Where(w => w.Kind != "model").OrderByDescending(w => w.Used).FirstOrDefault(); if (hi != null) pct += " · Claude " + Data.Percent(hi.Used) + (hi.Kind == "fiveHour" ? " 5h" : " wk"); }
            string text = Describe(engine) + " · " + pct;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        private void BuildMenu()
        {
            var menu = tray.ContextMenuStrip;
            var old = menu.Items.Cast<ToolStripItem>().ToList(); menu.Items.Clear(); foreach (var item in old) item.Dispose();
            menu.Renderer = new ModernMenuRenderer(form.CurrentPalette); menu.Font = PaintKit.Font(9.5F, false);
            menu.BackColor = form.CurrentPalette.Surface; menu.ForeColor = form.CurrentPalette.Ink;
            menu.ShowImageMargin = false; menu.ShowCheckMargin = true; menu.Padding = new Padding(4);
            var s = engine.State;
            menu.Items.Add(new ToolStripMenuItem("Codex · " + Describe(engine).ToLowerInvariant()) { Enabled = false });
            if (engine.Latest != null && engine.Latest.Windows.Count > 0)
            {
                var week = engine.Latest.Weekly; var five = engine.Latest.Windows.Find(w => w.Minutes == 300);
                string line = (week != null ? "Weekly " + Data.Percent(week.Used) : "") + (five != null ? (week != null ? " · " : "") + "5-hour " + Data.Percent(five.Used) : "") + " · " + (engine.Latest.AvailableCount.HasValue ? engine.Latest.AvailableCount.Value.ToString() : "?") + " credits";
                menu.Items.Add(new ToolStripMenuItem(line) { Enabled = false });
            }
            menu.Items.Add(new ToolStripSeparator());
            if (s.Enabled) menu.Items.Add("Pause automatic resets", null, delegate { form.Try(engine.Pause); });
            if (s.Pending != null) menu.Items.Add("Review pending reset…", null, delegate { ShowSettings(GuardForm.ActivityTab); });
            else if (!s.Enabled) menu.Items.Add("Enable with saved rules…", null, async delegate
            {
                ShowSettings(0); form.ReloadRules();
                if (s.Rules.AllowedCreditIds.Count == 0) { ShowSettings(1); form.Say("Check at least one credit, then enable."); return; }
                await form.EnableFromTrayAsync();
            });
            var levels = new ToolStripMenuItem("Threshold: " + Data.Percent(s.Rules.Threshold) + " used") { Enabled = !s.Enabled && s.Pending == null };
            foreach (double amount in new[] { 95.0, 97.0, 98.0, 99.0, 99.99 })
            {
                double selected = amount;
                var item = new ToolStripMenuItem(Data.Percent(amount) + (amount == 95 ? " (recommended)" : amount == 99.99 ? " (reported 100%)" : "")) { Checked = s.Rules.Threshold == amount };
                item.Click += delegate
                {
                    form.Try(delegate
                    {
                        var copy = Data.Serializer().Deserialize<Rules>(Data.Json(engine.State.Rules)); copy.Threshold = selected; engine.SaveRules(copy);
                        form.ReloadRules(); // Keeps an open window and its unsaved edits on other pages.
                    });
                }; levels.DropDownItems.Add(item);
            }
            menu.Items.Add(levels); menu.Items.Add("Choose allowed reset credits…", null, delegate { ShowSettings(1); });
            if (claude.Settings.Enabled)
            {
                menu.Items.Add(new ToolStripSeparator());
                var c = claude.Latest;
                string usage = c == null || claude.Failures > 0 ? "Claude · " + (claude.Failures > 0 ? "check failed" : "usage unavailable") : "Claude · " + String.Join(" · ", c.Windows.Where(w => w.Kind != "model").Select(w => (w.Kind == "fiveHour" ? "5-hour " : "weekly ") + Data.Percent(w.Used)).ToArray());
                menu.Items.Add(new ToolStripMenuItem(usage) { Enabled = false });
                menu.Items.Add("Use a Claude reset in Claude Code…", null, delegate { form.Try(ClaudeUsageSource.OpenLimitReset); });
                menu.Items.Add("Open claude.ai usage…", null, delegate { form.Try(ClaudeUsageSource.OpenUsagePage); });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings and activity…", null, delegate { ShowSettings(); });
            var refresh = new ToolStripMenuItem("Refresh now", null, async delegate { await RefreshAsync(false); await RefreshClaudeAsync(); }) { Enabled = !engine.Busy }; menu.Items.Add(refresh);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Quit(); });
            foreach (ToolStripItem item in menu.Items) item.Padding = new Padding(7, 5, 7, 5);
        }
        private bool quitting;
        private void Quit()
        {
            if (quitting) return; quitting = true; // The menu stays usable while the confirmation is open.
            try { QuitCore(); } finally { quitting = false; }
        }
        private void QuitCore()
        {
            if (engine.State.Enabled || engine.State.Pending != null || engine.RequestInFlight)
            {
                string why = engine.RequestInFlight ? "A reset request is in progress." : engine.State.Pending != null ? "A reset is awaiting confirmation." : "Automatic resets are on.";
                if (MessageBox.Show(form, why + " Quitting stops monitoring until Reset Guard runs again. Quit anyway?", "Codex Reset Guard", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            exiting = true; SystemEvents.UserPreferenceChanged -= PreferenceChanged; timer.Stop(); tray.Visible = false; tray.Dispose(); if (form != null) form.Dispose();
            foreach (var icon in new[] { offIcon, onIcon, pendingIcon, alertIcon }) icon.Dispose(); ExitThread();
        }
        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); showEvent.Dispose(); } base.Dispose(disposing); }
    }
}
