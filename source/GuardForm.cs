using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexResetGuard
{
    internal sealed class CreditChoice
    {
        public string Id;
        public string Label;
        public override string ToString() { return Label; }
    }
    internal sealed class GuardForm : Form
    {
        private readonly ResetEngine engine;
        private readonly Action refresh;
        private readonly Action retry;
        private readonly Action saveExtras;
        private Label account, usage, resetTime, status, lastCheck, connection;
        private ProgressBar usageBar;
        private NumericUpDown threshold, allowance, reserve;
        private ComboBox window, period, preferred;
        private CheckBox startup;
        private TextBox executable;
        private ListView credits, history;
        private Button enable, pause, save, check, retryButton;
        private Panel rulesPanel;
        private bool rebuilding;
        private bool initializedCredits;
        internal TabControl Tabs;
        public static readonly Color Accent = Color.FromArgb(22, 98, 88);
        private static readonly Color Ink = Color.FromArgb(28, 36, 44);
        private static readonly Color Muted = Color.FromArgb(88, 99, 112);

        public GuardForm(ResetEngine engine, Action refresh, Action retry, Action saveExtras)
        {
            this.engine = engine; this.refresh = refresh; this.retry = retry; this.saveExtras = saveExtras;
            Text = "Codex Reset Guard"; Font = new Font("Segoe UI", 10F); BackColor = Color.White; ForeColor = Ink;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(810, 745); MinimumSize = new Size(760, 720);
            AutoScaleMode = AutoScaleMode.Dpi; Icon = TrayIcons.Create(Accent); ShowInTaskbar = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            Controls.Add(layout);
            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(LabelAt("Codex Reset Guard", 0, 0, 740, 33, 21, FontStyle.Bold, Ink));
            account = LabelAt("Connecting to your signed-in Codex account…", 1, 40, 736, 27, 10, FontStyle.Regular, Muted);
            header.Controls.Add(account); layout.Controls.Add(header, 0, 0);
            var meter = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(244, 247, 247), Padding = new Padding(12) };
            usage = LabelAt("Weekly usage unavailable", 12, 7, 720, 29, 16, FontStyle.Bold, Ink);
            usageBar = new ProgressBar { Left = 15, Top = 40, Width = 714, Height = 9, Maximum = 100, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            resetTime = LabelAt("Waiting for a live usage check", 12, 56, 720, 25, 9, FontStyle.Regular, Muted);
            meter.Controls.Add(usage); meter.Controls.Add(usageBar); meter.Controls.Add(resetTime); layout.Controls.Add(meter, 0, 1);
            status = new Label { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 4), ForeColor = Muted, AutoEllipsis = true };
            layout.Controls.Add(status, 0, 2);
            Tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(13, 6) };
            layout.Controls.Add(Tabs, 0, 3);
            BuildRules(); BuildCredits(); BuildHistory(); BuildConnection();
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0), WrapContents = false };
            enable = Button("Enable automatic resets", 203, async delegate { await EnableAsync(); });
            enable.BackColor = Accent; enable.ForeColor = Color.White; enable.FlatStyle = FlatStyle.Flat; enable.FlatAppearance.BorderSize = 0;
            save = Button("Save rules", 110, delegate { Try(delegate { engine.SaveRules(ReadRules()); SaveExtraSettings(); }); });
            pause = Button("Pause", 86, delegate { Try(engine.Pause); });
            check = Button("Refresh now", 116, delegate { refresh(); });
            actions.Controls.AddRange(new Control[] { enable, save, pause, check }); layout.Controls.Add(actions, 0, 4);
            lastCheck = new Label { Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 8.5F), ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(lastCheck, 0, 5);
            ApplyRules(); UpdateView();
        }
        private static Label LabelAt(string text, int x, int y, int w, int h, float size, FontStyle style, Color color)
        { return new Label { Text = text, Left = x, Top = y, Width = w, Height = h, Font = new Font("Segoe UI", size, style), ForeColor = color, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; }
        private static Button Button(string text, int width, EventHandler handler)
        { var b = new Button { Text = text, Width = width, Height = 32, FlatStyle = FlatStyle.System, Margin = new Padding(6, 0, 0, 0) }; b.Click += handler; return b; }
        private TabPage Page(string title)
        { var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(14) }; Tabs.TabPages.Add(page); return page; }
        private void BuildRules()
        {
            var page = Page("Automatic resets");
            rulesPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; page.Controls.Add(rulesPanel);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 8 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rulesPanel.Controls.Add(table);
            window = Combo(new[] { "Weekly usage", "5-hour usage", "Either weekly or 5-hour" });
            threshold = Number(1, 100, 95, 2); threshold.Width = 95;
            var thresholdRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            thresholdRow.Controls.Add(threshold); thresholdRow.Controls.Add(new Label { Text = "% used", AutoSize = true, Margin = new Padding(8, 7, 0, 0) });
            period = Combo(new[] { "4 hours", "8 hours", "12 hours", "24 hours", "48 hours", "Until I pause it" });
            allowance = Number(1, 100, 1, 0); reserve = Number(0, 100, 1, 0);
            preferred = Combo(new string[0]); preferred.DropDownWidth = 650;
            Row(table, 0, "Watch", window); Row(table, 1, "Reset at", thresholdRow); Row(table, 2, "Stay enabled for", period);
            Row(table, 3, "Maximum resets", allowance); Row(table, 4, "Keep in reserve", reserve); Row(table, 5, "Credit selection", preferred);
            var note = new Label { AutoSize = true, MaximumSize = new Size(680, 0), Text = "95% is recommended for unattended work. Codex reports whole percentages; 99.99% waits for a reported 100%. Polling cannot guarantee that a large request will finish before a limit is reached.", ForeColor = Muted, Margin = new Padding(0, 12, 0, 8) };
            table.Controls.Add(note, 0, 6); table.SetColumnSpan(note, 2);
            var preview = Button("Preview rule · no reset", 195, delegate
            {
                Try(delegate
                {
                    var state = new GuardState { Rules = ReadRules() };
                    var decision = Policy.Evaluate(state, engine.Latest, Data.Now, true);
                    MessageBox.Show(this, decision.Reason + "\n\nThis preview does not use a reset.", "Rule preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            });
            table.Controls.Add(preview, 0, 7); table.SetColumnSpan(preview, 2);
        }
        private static ComboBox Combo(string[] items)
        { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 410, Dock = DockStyle.Top, Margin = new Padding(0, 3, 0, 5) }; c.Items.AddRange(items); if (items.Length > 0) c.SelectedIndex = 0; return c; }
        private static NumericUpDown Number(decimal min, decimal max, decimal value, int places)
        { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, DecimalPlaces = places, Increment = 1, Width = 95, Margin = new Padding(0, 3, 0, 5) }; }
        private static void Row(TableLayoutPanel table, int row, string name, Control input)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
            table.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(0, 7, 5, 0) }, 0, row); table.Controls.Add(input, 1, row);
        }
        private void BuildCredits()
        {
            var page = Page("Reset credits");
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 51)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            page.Controls.Add(grid);
            grid.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Check the credits this app may use. Earliest expiry wins unless you choose a specific credit. Newly granted credits start unchecked.", ForeColor = Muted }, 0, 0);
            credits = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true, BorderStyle = BorderStyle.FixedSingle };
            credits.Columns.Add("Allow / Reset", 180); credits.Columns.Add("Expires (local time)", 227); credits.Columns.Add("Time left", 110); credits.Columns.Add("Status", 120);
            credits.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { if (!rebuilding && e.NewValue == CheckState.Checked && !((ResetCredit)credits.Items[e.Index].Tag).Available(Data.Now + 30)) e.NewValue = CheckState.Unchecked; };
            grid.Controls.Add(credits, 0, 1);
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            row.Controls.Add(Button("Allow all shown", 141, delegate { if (engine.State.Enabled) return; foreach (ListViewItem item in credits.Items) item.Checked = ((ResetCredit)item.Tag).Available(Data.Now + 30); }));
            row.Controls.Add(Button("Clear selection", 135, delegate { if (engine.State.Enabled) return; foreach (ListViewItem item in credits.Items) item.Checked = false; }));
            grid.Controls.Add(row, 0, 2);
        }
        private void BuildHistory()
        {
            var page = Page("Activity");
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); page.Controls.Add(grid);
            history = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, ShowItemToolTips = true };
            history.Columns.Add("Time", 155); history.Columns.Add("Event", 143); history.Columns.Add("Detail", 400); grid.Controls.Add(history, 0, 0);
            retryButton = Button("Retry pending request", 205, delegate
            {
                var pending = engine.State.Pending;
                if (pending != null && !pending.Acknowledged && MessageBox.Show(this,
                    "This retries only the original saved request and selected credit.\n\nIf Codex did not apply it earlier, retrying can use that credit now even if usage has dropped, your reserve changed, or the enabled period ended. It does not enable a new reset allowance.\n\nRetry the original request?",
                    "Confirm pending reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                retry();
            });
            retryButton.Margin = new Padding(0, 8, 0, 0); grid.Controls.Add(retryButton, 0, 1);
        }
        private void BuildConnection()
        {
            var page = Page("Connection");
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 }; page.Controls.Add(panel);
            panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(680, 0), Text = "Uses your installed official Codex CLI and its existing ChatGPT sign-in. This app does not send model prompts, copy tokens, switch accounts, or restart Codex Desktop.", ForeColor = Muted, Margin = new Padding(0, 0, 0, 15) });
            startup = new CheckBox { AutoSize = true, Text = "Launch in the tray when I sign in to Windows", Checked = Startup.Enabled, Margin = new Padding(0, 0, 0, 18) }; panel.Controls.Add(startup);
            panel.Controls.Add(new Label { AutoSize = true, Text = "Codex executable (leave empty to detect automatically)", Margin = new Padding(0, 0, 0, 6) });
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 91));
            executable = new TextBox { Dock = DockStyle.Top, Text = engine.State.CodexExecutable ?? "" }; pathRow.Controls.Add(executable, 0, 0);
            var browse = Button("Browse…", 85, delegate { using (var dialog = new OpenFileDialog { Title = "Choose the official Codex CLI executable", Filter = "Codex executable (*.exe)|*.exe", CheckFileExists = true }) if (dialog.ShowDialog(this) == DialogResult.OK) executable.Text = dialog.FileName; });
            pathRow.Controls.Add(browse, 1, 0); panel.Controls.Add(pathRow);
            connection = new Label { AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Muted, Margin = new Padding(0, 10, 0, 18), Text = "Changes take effect after saving rules. Codex CLI 0.147.0+ is required." }; panel.Controls.Add(connection);
            panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Muted, Text = "Automatic resets run only while this program is running and the PC is awake. Closing the settings window keeps it in the tray. Quit stops monitoring. An enabled period can survive a restart until its deadline or allowance is reached." });
        }
        private void ApplyRules()
        {
            var r = engine.State.Rules;
            threshold.Value = (decimal)r.Threshold; window.SelectedIndex = r.Window == "fiveHour" ? 1 : r.Window == "either" ? 2 : 0;
            allowance.Value = r.MaximumResets; reserve.Value = r.ReserveCredits;
            int[] hours = { 4, 8, 12, 24, 48, 0 }; int index = Array.IndexOf(hours, r.ArmHours); period.SelectedIndex = index < 0 ? 2 : index;
        }
        private Rules ReadRules()
        {
            return new Rules { Threshold = (double)threshold.Value, Window = window.SelectedIndex == 1 ? "fiveHour" : window.SelectedIndex == 2 ? "either" : "weekly", ArmHours = new[] { 4, 8, 12, 24, 48, 0 }[period.SelectedIndex], MaximumResets = (int)allowance.Value, ReserveCredits = (int)reserve.Value, AllowedCreditIds = credits.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((ResetCredit)i.Tag).Id).ToList(), PreferredCreditId = preferred.SelectedItem is CreditChoice ? ((CreditChoice)preferred.SelectedItem).Id : null };
        }
        private void SaveExtraSettings()
        {
            engine.State.CodexExecutable = String.IsNullOrWhiteSpace(executable.Text) ? null : executable.Text.Trim();
            saveExtras(); Startup.Set(startup.Checked);
        }
        private async Task EnableAsync()
        {
            string expectedAccount = engine.Latest == null ? null : engine.Latest.AccountKey;
            try
            {
                Rules rules = ReadRules(); engine.SaveRules(rules); SaveExtraSettings();
                await engine.CheckAsync(false);
                if (expectedAccount != null && (engine.Latest == null || engine.Latest.AccountKey != expectedAccount)) throw new GuardException("The account changed during the check. Review the account and credits before enabling.");
                engine.Arm(rules); refresh();
            }
            catch (Exception ex) { MessageBox.Show(this, ex is GuardException ? ex.Message : "Automatic resets could not be enabled. Check the connection and saved rules.", "Codex Reset Guard", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            UpdateView();
        }
        public void UpdateView()
        {
            if (IsDisposed) return;
            var state = engine.State; var s = engine.Latest;
            status.Text = (state.Enabled ? "AUTO ON  ·  " : "AUTO OFF  ·  ") + engine.Status;
            status.ForeColor = state.Pending != null ? Color.FromArgb(148, 84, 15) : state.Enabled ? Accent : Muted;
            if (s != null)
            {
                account.Text = s.AccountDisplay + "  ·  " + (s.Plan ?? "ChatGPT") + "  ·  " + (s.AvailableCount.HasValue ? s.AvailableCount.Value + " reset credits" : "Reset count unavailable");
                var weekly = s.Weekly;
                usage.Text = weekly == null ? "Weekly usage unavailable" : "Weekly  " + Data.Percent(weekly.Used) + " used  ·  " + Data.Percent(100 - weekly.Used) + " remaining";
                usageBar.Value = weekly == null ? 0 : Math.Max(0, Math.Min(100, (int)weekly.Used));
                resetTime.Text = "Normal reset: " + Data.LocalTime(weekly == null ? null : weekly.ResetsAt) + (s.Windows.Any(w => w.Minutes == 300) ? "   ·   5-hour: " + Data.Percent(s.Windows.First(w => w.Minutes == 300).Used) + " used" : "");
                UpdateCredits(s);
            }
            rulesPanel.Enabled = !state.Enabled && state.Pending == null; credits.Enabled = !state.Enabled && state.Pending == null;
            startup.Enabled = executable.Enabled = !state.Enabled && state.Pending == null;
            enable.Enabled = !engine.Busy && !state.Enabled && state.Pending == null;
            save.Enabled = !engine.Busy && !state.Enabled && state.Pending == null; pause.Enabled = state.Enabled;
            check.Enabled = !engine.Busy; retryButton.Enabled = !engine.Busy && state.Pending != null;
            string freshness = s == null ? "No usage snapshot yet" : "Last checked " + Data.LocalTime(s.FetchedAt);
            lastCheck.Text = engine.Busy ? "Checking Codex…" : freshness + "  ·  " + (state.Enabled ? state.UsedThisArm + "/" + state.Rules.MaximumResets + " resets used; " + (state.ArmedUntil == 0 ? "until paused" : "enabled until " + Data.LocalTime(state.ArmedUntil)) : "Automatic resets require opt-in");
            history.BeginUpdate(); history.Items.Clear();
            foreach (var h in state.History) { var item = new ListViewItem(new[] { Data.LocalTime(h.At), h.Event, h.Detail }); item.ToolTipText = h.Detail; history.Items.Add(item); } history.EndUpdate();
        }
        private void UpdateCredits(Snapshot s)
        {
            var selected = new HashSet<string>(initializedCredits && !engine.State.Enabled ? credits.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((ResetCredit)i.Tag).Id) : engine.State.Rules.AllowedCreditIds);
            string preferredId = initializedCredits && !engine.State.Enabled && preferred.SelectedItem is CreditChoice ? ((CreditChoice)preferred.SelectedItem).Id : engine.State.Rules.PreferredCreditId;
            rebuilding = true; credits.BeginUpdate(); credits.Items.Clear(); preferred.BeginUpdate(); preferred.Items.Clear();
            preferred.Items.Add(new CreditChoice { Id = null, Label = "Earliest expiry among checked credits" });
            foreach (var c in s.Credits.OrderBy(c => c.ExpiresAt ?? Int64.MaxValue))
            {
                double days = c.ExpiresAt.HasValue ? (c.ExpiresAt.Value - Data.Now) / 86400.0 : 0;
                var item = new ListViewItem(new[] { c.Title ?? "Full reset", c.ExpiresAt.HasValue ? Data.LocalTime(c.ExpiresAt) : "No reported expiry", c.ExpiresAt.HasValue ? days > 0 ? days >= 1 ? days.ToString("0.0") + " days" : Math.Max(0, days * 24).ToString("0.0") + " hours" : "Expired" : "—", c.Status ?? "Unknown" });
                item.Tag = c; item.Checked = selected.Contains(c.Id) && c.Available(Data.Now + 30); item.ToolTipText = c.Display; credits.Items.Add(item);
                preferred.Items.Add(new CreditChoice { Id = c.Id, Label = c.Display });
            }
            if (!String.IsNullOrWhiteSpace(preferredId) && !s.Credits.Any(c => c.Id == preferredId)) preferred.Items.Add(new CreditChoice { Id = preferredId, Label = "Previously selected credit (not currently available)" });
            preferred.SelectedIndex = 0;
            for (int i = 1; i < preferred.Items.Count; i++) if (((CreditChoice)preferred.Items[i]).Id == preferredId) preferred.SelectedIndex = i;
            credits.EndUpdate(); preferred.EndUpdate(); rebuilding = false; initializedCredits = true;
        }
        public void Try(Action action)
        {
            try { action(); UpdateView(); }
            catch (Exception ex) { MessageBox.Show(this, ex is GuardException ? ex.Message : "The setting could not be saved. Automatic resets have not been enabled with this change.", "Codex Reset Guard", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
    }
    internal static class Startup
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool Enabled
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(Key)) return key != null && String.Equals(key.GetValue("CodexResetGuard") as string, Command, StringComparison.OrdinalIgnoreCase); }
        }
        private static string Command { get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray"; } }
        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (enabled) key.SetValue("CodexResetGuard", Command);
                else if (String.Equals(key.GetValue("CodexResetGuard") as string, Command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue("CodexResetGuard", false);
            }
        }
    }
}
