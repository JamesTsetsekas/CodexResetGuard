using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
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
        private readonly Action refresh, retry, saveExtras;
        private readonly bool demo;
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000 };
        private Palette palette;
        private Label account, badge, statusTitle, statusDetail, lastCheck, feedback, historyDetail, creditNote;
        private UsageSummary summary;
        private ModernNumber threshold, allowance, reserve;
        private ModernCombo window, period, preferred, theme;
        private CheckBox startup;
        private TextBox executable;
        private ListView credits, history;
        private ModernButton enable, save, check, retryButton, allowAll, clearAll, browse;
        private Panel rulesPanel;
        private bool rebuilding, initializedCredits, initializing = true;
        private string themePreference;
        private readonly List<ModernButton> presets = new List<ModernButton>();
        private readonly List<ImageList> rowImages = new List<ImageList>();
        internal NavigationTabs Tabs;
        public static readonly Color Accent = Color.FromArgb(28, 103, 70);
        internal Palette CurrentPalette { get { return palette; } }
        public GuardForm(ResetEngine engine, Action refresh, Action retry, Action saveExtras, bool demo = false)
        {
            this.engine = engine; this.refresh = refresh; this.retry = retry; this.saveExtras = saveExtras; this.demo = demo;
            themePreference = demo ? "light" : Appearance.Load(); palette = Palette.Create(Appearance.IsDark(themePreference));
            Text = "Codex Reset Guard"; Font = PaintKit.Font(9.5F, false); BackColor = palette.Canvas; ForeColor = palette.Ink;
            StartPosition = FormStartPosition.CenterScreen; AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 760); MinimumSize = new Size(600, 780); MaximizeBox = false; DoubleBuffered = true;
            Icon = TrayIcons.Create(Accent); ShowInTaskbar = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 15, 20, 12), ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            foreach (float height in new[] { 59F, 150F, 68F }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); Controls.Add(layout);
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 83)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            var heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            heading.Controls.Add(LabelAt("Codex Reset Guard", 0, 0, 400, 30, 17, true, false));
            account = LabelAt("Connecting to Codex…", 1, 32, 400, 20, 9, false, true); heading.Controls.Add(account); header.Controls.Add(heading, 0, 0);
            badge = new ModernLabel { Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleCenter, Font = PaintKit.Font(9, true), Margin = new Padding(0, 3, 10, 0) }; header.Controls.Add(badge, 1, 0);
            check = Button("↻", 35, delegate { refresh(); }); check.Font = PaintKit.Font(18, false); check.Tone = ButtonTone.Quiet; check.AccessibleName = "Refresh usage"; tips.SetToolTip(check, "Refresh usage (F5)"); header.Controls.Add(check, 2, 0); layout.Controls.Add(header, 0, 0);
            summary = new UsageSummary { Dock = DockStyle.Fill, Margin = Padding.Empty }; layout.Controls.Add(summary, 0, 1);
            var status = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            statusTitle = LabelAt("Automatic resets are off", 1, 10, 550, 22, 10, true, false);
            statusDetail = LabelAt("Choose allowed credits, then enable your rules.", 1, 33, 550, 27, 9, false, true);
            status.Controls.Add(statusTitle); status.Controls.Add(statusDetail); layout.Controls.Add(status, 0, 2);
            Tabs = new NavigationTabs { Dock = DockStyle.Fill, Margin = Padding.Empty }; layout.Controls.Add(Tabs, 0, 3);
            BuildRules(); BuildCredits(); BuildHistory(); BuildConnection();
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206));
            feedback = new ModernLabel { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = PaintKit.Font(9, false), AutoEllipsis = true, Tag = "muted", Margin = Padding.Empty }; actions.Controls.Add(feedback, 0, 0);
            save = Button("Save rules", 124, delegate { Try(delegate { engine.SaveRules(ReadRules()); SaveExtraSettings(); feedback.Text = Tabs.SelectedIndex == 3 ? "Settings saved" : "Rules saved"; }); }); save.Dock = DockStyle.Fill; save.Margin = new Padding(0, 0, 6, 0); actions.Controls.Add(save, 1, 0);
            enable = Button("Enable automatic resets", 206, async delegate { feedback.Text = ""; if (engine.State.Enabled) Try(engine.Pause); else if (engine.State.Pending != null) Tabs.SelectedIndex = 2; else await EnableAsync(); }); enable.Tone = ButtonTone.Primary; enable.Dock = DockStyle.Fill; actions.Controls.Add(enable, 2, 0); layout.Controls.Add(actions, 0, 4);
            lastCheck = new ModernLabel { Dock = DockStyle.Fill, Font = PaintKit.Font(8, false), Tag = "muted", TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, AutoEllipsis = true }; layout.Controls.Add(lastCheck, 0, 5);
            Tabs.SelectedIndexChanged += delegate { save.Text = Tabs.SelectedIndex == 3 ? "Save settings" : "Save rules"; feedback.Text = ""; };
            ApplyRules(); initializing = false; ApplyTheme(); UpdateView(); if (!demo) SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        }
        private Label LabelAt(string text, int x, int y, int width, int height, float size, bool bold, bool muted)
        { return new ModernLabel { Text = text, Left = x, Top = y, Width = width, Height = height, Font = PaintKit.Font(size, bold), Tag = muted ? "muted" : null, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; }
        private static ModernButton Button(string text, int width, EventHandler click)
        { var b = new ModernButton { Text = text, Width = width, Height = 34, Margin = Padding.Empty }; b.Click += click; return b; }
        private ModernCombo Combo(string[] items)
        { var c = new ModernCombo { Dock = DockStyle.Top, Margin = new Padding(0, 2, 0, 4) }; c.Items.AddRange(items); if (items.Length > 0) c.SelectedIndex = 0; return c; }
        private Label Note(string text, int height)
        { return new ModernLabel { Text = text, Dock = DockStyle.Fill, Height = height, Font = PaintKit.Font(9, false), Tag = "muted", Margin = new Padding(1, 4, 0, 4), AutoEllipsis = true }; }
        private void Row(TableLayoutPanel table, int row, string text, Control input, int height)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); table.Controls.Add(new ModernLabel { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(1, 0, 8, 0) }, 0, row); input.AccessibleName = text; table.Controls.Add(input, 1, row);
        }
        private void BuildRules()
        {
            var page = Tabs.AddPage("Automation"); rulesPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty }; page.Controls.Add(rulesPanel);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 7, Margin = Padding.Empty };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); rulesPanel.Controls.Add(table);
            window = Combo(new[] { "Weekly usage", "5-hour usage", "Either weekly or 5-hour" }); threshold = new ModernNumber(1, 100, 95, 2) { Width = 120, Margin = Padding.Empty, AccessibleName = "Usage threshold" };
            var thresholdRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            thresholdRow.Controls.Add(threshold); thresholdRow.Controls.Add(new ModernLabel { Text = "% used", Width = 55, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 6, 0), Tag = "muted" });
            foreach (decimal value in new[] { 95M, 99M }) { decimal captured = value; var b = Button(value + "%", 53, delegate { threshold.Value = captured; UpdatePresets(); }); b.Tone = ButtonTone.Quiet; b.Margin = new Padding(3, 0, 0, 0); b.Tag = captured; thresholdRow.Controls.Add(b); presets.Add(b); }
            threshold.ValueChanged += delegate { if (!initializing) UpdatePresets(); }; period = Combo(new[] { "4 hours", "8 hours", "12 hours", "24 hours", "48 hours", "Until I pause it" });
            allowance = new ModernNumber(1, 100, 1, 0) { Width = 96, Margin = Padding.Empty, AccessibleName = "Maximum resets" }; reserve = new ModernNumber(0, 100, 1, 0) { Width = 96, Margin = Padding.Empty, AccessibleName = "Credits to keep in reserve" };
            var budget = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            budget.Controls.Add(allowance); budget.Controls.Add(new ModernLabel { Text = "max", Width = 41, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(7, 0, 9, 0), Tag = "muted" }); budget.Controls.Add(reserve); budget.Controls.Add(new ModernLabel { Text = "in reserve", Width = 77, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(7, 0, 0, 0), Tag = "muted" });
            preferred = Combo(new string[0]); preferred.DropDownWidth = 560;
            Row(table, 0, "Watch", window, 42); Row(table, 1, "Reset at", thresholdRow, 44); Row(table, 2, "Stay enabled", period, 42); Row(table, 3, "Reset budget", budget, 44); Row(table, 4, "Credit order", preferred, 42);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); var note = Note("95% leaves headroom. Codex reports whole percentages, so 99.99% waits for 100%. Polling cannot guarantee uninterrupted sessions.", 48); table.Controls.Add(note, 0, 5); table.SetColumnSpan(note, 2);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 37)); var preview = Button("Preview rules", 125, delegate { Try(delegate { var state = new GuardState { Rules = ReadRules() }; var decision = Policy.Evaluate(state, engine.Latest, Data.Now, true); MessageBox.Show(this, decision.Reason + "\n\nThis preview does not use a reset.", "Rule preview", MessageBoxButtons.OK, MessageBoxIcon.Information); }); }); preview.Tone = ButtonTone.Quiet; table.Controls.Add(preview, 0, 6); table.SetColumnSpan(preview, 2);
        }
        private ListView List(string[] names, int[] widths)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true, BorderStyle = BorderStyle.None, OwnerDraw = true, Margin = Padding.Empty };
            var images = new ImageList { ImageSize = new Size(1, 39), ColorDepth = ColorDepth.Depth32Bit }; rowImages.Add(images); list.SmallImageList = images;
            for (int i = 0; i < names.Length; i++) list.Columns.Add(names[i], widths[i]);
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) { using (var brush = new SolidBrush(palette.Canvas)) e.Graphics.FillRectangle(brush, e.Bounds); PaintKit.Text(e.Graphics, e.Header.Text, list.Font, palette.Muted, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine); };
            list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                var bounds = e.Bounds; using (var brush = new SolidBrush(e.Item.Selected ? palette.AccentSoft : palette.Surface)) e.Graphics.FillRectangle(brush, bounds); using (var pen = new Pen(palette.Border)) e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
                int offset = 8;
                if (list.CheckBoxes && e.ColumnIndex == 0)
                {
                    int size = (int)(14 * DeviceDpi / 96F), x = bounds.Left + 4, y = bounds.Top + (bounds.Height - size) / 2;
                    PaintKit.Box(e.Graphics, new RectangleF(x, y, size, size), 3, e.Item.Checked ? palette.Accent : palette.Surface, palette.Border);
                    if (e.Item.Checked) using (var pen = new Pen(palette.Dark ? palette.Canvas : Color.White, 2)) e.Graphics.DrawLines(pen, new[] { new Point(x + 3, y + size / 2), new Point(x + size / 2 - 1, y + size - 4), new Point(x + size - 3, y + 4) }); offset = size + 14;
                }
                PaintKit.Text(e.Graphics, e.SubItem.Text, list.Font, list.Enabled ? e.ColumnIndex == 0 ? palette.Ink : palette.Muted : palette.Muted, new Rectangle(bounds.Left + offset, bounds.Top, Math.Max(0, bounds.Width - offset - 5), bounds.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                if (e.Item.Focused && list.Focused) e.DrawFocusRectangle(bounds);
            }; return list;
        }
        private void BuildCredits()
        {
            var page = Tabs.AddPage("Credits"); var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty }; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 51)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); page.Controls.Add(grid);
            creditNote = Note("Choose the resets this app may use. Earlier expiries come first; new credits stay unchecked.", 51); grid.Controls.Add(creditNote, 0, 0);
            credits = List(new[] { "Allowed reset", "Expires", "Time left", "Status" }, new[] { 135, 172, 88, 115 }); credits.CheckBoxes = true;
            credits.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { if (!rebuilding && e.NewValue == CheckState.Checked && !((ResetCredit)credits.Items[e.Index].Tag).Available(Data.Now + 30)) e.NewValue = CheckState.Unchecked; if (!rebuilding && IsHandleCreated) BeginInvoke(new Action(UpdateApproved)); };
            credits.Resize += delegate { if (credits.Columns.Count == 4) credits.Columns[3].Width = Math.Max(80, credits.ClientSize.Width - credits.Columns[0].Width - credits.Columns[1].Width - credits.Columns[2].Width); }; grid.Controls.Add(credits, 0, 1);
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 9, 0, 0), Margin = Padding.Empty, WrapContents = false };
            allowAll = Button("Allow all shown", 136, delegate { if (engine.State.Enabled || engine.State.Pending != null) return; foreach (ListViewItem item in credits.Items) item.Checked = ((ResetCredit)item.Tag).Available(Data.Now + 30); }); clearAll = Button("Clear selection", 132, delegate { if (engine.State.Enabled || engine.State.Pending != null) return; foreach (ListViewItem item in credits.Items) item.Checked = false; }); clearAll.Tone = ButtonTone.Quiet; clearAll.Margin = new Padding(8, 0, 0, 0); row.Controls.Add(allowAll); row.Controls.Add(clearAll); grid.Controls.Add(row, 0, 2);
        }
        private void BuildHistory()
        {
            var page = Tabs.AddPage("Activity"); var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty }; grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 68)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); page.Controls.Add(grid);
            history = List(new[] { "Event", "When" }, new[] { 318, 224 }); history.Resize += delegate { if (history.Columns.Count == 2) history.Columns[0].Width = Math.Max(200, history.ClientSize.Width - history.Columns[1].Width); };
            history.SelectedIndexChanged += delegate { historyDetail.Text = history.SelectedItems.Count == 0 ? "Select an event to see its details." : (string)history.SelectedItems[0].Tag; tips.SetToolTip(historyDetail, historyDetail.Text); }; grid.Controls.Add(history, 0, 0); historyDetail = Note("Select an event to see its details.", 68); grid.Controls.Add(historyDetail, 0, 1);
            retryButton = Button("Reconcile pending reset", 222, delegate
            {
                var pending = engine.State.Pending;
                if (pending != null && !pending.Acknowledged && MessageBox.Show(this, "This retries only the original saved request and selected credit.\n\nIf Codex did not apply it earlier, retrying can use that credit now even if usage has dropped, your reserve changed, or the enabled period ended. It does not enable a new reset allowance.\n\nRetry the original request?", "Confirm pending reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return; retry();
            }); grid.Controls.Add(retryButton, 0, 2);
        }
        private void BuildConnection()
        {
            var page = Tabs.AddPage("Settings"); page.AutoScroll = true; var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 6, Margin = Padding.Empty }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); page.Controls.Add(table);
            theme = Combo(new[] { "Use Windows setting", "Light", "Dark" }); theme.SelectedIndex = themePreference == "light" ? 1 : themePreference == "dark" ? 2 : 0;
            theme.SelectedIndexChanged += delegate { if (initializing) return; themePreference = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system"; ApplyTheme(); if (!demo) Try(delegate { Appearance.Save(themePreference); }); }; Row(table, 0, "Appearance", theme, 46);
            startup = new ModernCheckBox { Dock = DockStyle.Fill, Text = "Launch in the tray at Windows sign-in", Checked = !demo && Startup.Enabled, Margin = new Padding(1, 6, 0, 7), AutoSize = true }; table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); table.Controls.Add(startup, 0, 1); table.SetColumnSpan(startup, 2);
            var description = Note("Connected through the official Codex CLI and your existing ChatGPT sign-in. No tokens are copied or stored by Reset Guard.", 58); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); table.Controls.Add(description, 0, 2); table.SetColumnSpan(description, 2);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); var pathLabel = Note("Codex executable · leave blank to detect automatically", 25); table.Controls.Add(pathLabel, 0, 3); table.SetColumnSpan(pathLabel, 2);
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(1, 2, 0, 0) }; pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 91));
            executable = new TextBox { Dock = DockStyle.Top, Text = engine.State.CodexExecutable ?? "", BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 7, 0), AccessibleName = "Codex executable path" }; pathRow.Controls.Add(executable, 0, 0);
            browse = Button("Browse…", 88, delegate { using (var dialog = new OpenFileDialog { Title = "Choose the official Codex CLI executable", Filter = "Codex executable (*.exe)|*.exe", CheckFileExists = true }) if (dialog.ShowDialog(this) == DialogResult.OK) executable.Text = dialog.FileName; }); pathRow.Controls.Add(browse, 1, 0);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); table.Controls.Add(pathRow, 0, 4); table.SetColumnSpan(pathRow, 2);
            var note = Note("Codex CLI 0.147.0+ required. Keep your PC awake and the tray app running. Closing this window keeps monitoring active.\n\nCodex Reset Guard 1.1.0 · Independent open-source software", 83); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 83)); table.Controls.Add(note, 0, 5); table.SetColumnSpan(note, 2);
        }
        private void ApplyRules()
        {
            var r = engine.State.Rules; threshold.Value = (decimal)r.Threshold; window.SelectedIndex = r.Window == "fiveHour" ? 1 : r.Window == "either" ? 2 : 0; allowance.Value = r.MaximumResets; reserve.Value = r.ReserveCredits;
            int[] hours = { 4, 8, 12, 24, 48, 0 }; int index = Array.IndexOf(hours, r.ArmHours); period.SelectedIndex = index < 0 ? 2 : index;
        }
        private Rules ReadRules()
        {
            if (!threshold.Commit() || !allowance.Commit() || !reserve.Commit()) throw new GuardException("Enter a valid threshold (1–100), maximum resets (1–100), and reserve (0–100).");
            return new Rules { Threshold = (double)threshold.Value, Window = window.SelectedIndex == 1 ? "fiveHour" : window.SelectedIndex == 2 ? "either" : "weekly", ArmHours = new[] { 4, 8, 12, 24, 48, 0 }[period.SelectedIndex], MaximumResets = (int)allowance.Value, ReserveCredits = (int)reserve.Value, AllowedCreditIds = credits.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((ResetCredit)i.Tag).Id).ToList(), PreferredCreditId = preferred.SelectedItem is CreditChoice ? ((CreditChoice)preferred.SelectedItem).Id : null };
        }
        private void SaveExtraSettings()
        { engine.State.CodexExecutable = String.IsNullOrWhiteSpace(executable.Text) ? null : executable.Text.Trim(); saveExtras(); if (!demo) Startup.Set(startup.Checked); }
        private async Task EnableAsync()
        {
            string expectedAccount = engine.Latest == null ? null : engine.Latest.AccountKey;
            try { Rules rules = ReadRules(); engine.SaveRules(rules); SaveExtraSettings(); await engine.CheckAsync(false); if (expectedAccount != null && (engine.Latest == null || engine.Latest.AccountKey != expectedAccount)) throw new GuardException("The account changed during the check. Review the account and credits before enabling."); engine.Arm(rules); refresh(); }
            catch (Exception ex) { MessageBox.Show(this, ex is GuardException ? ex.Message : "Automatic resets could not be enabled. Check the connection and saved rules.", "Codex Reset Guard", MessageBoxButtons.OK, MessageBoxIcon.Information); } UpdateView();
        }
        public void UpdateView()
        {
            if (IsDisposed) return; var state = engine.State; var snapshot = engine.Latest;
            badge.Text = state.Pending != null ? "● Pending" : state.Enabled ? "● On" : "● Off"; badge.ForeColor = state.Pending != null ? palette.Warning : state.Enabled ? palette.Accent : palette.Muted;
            statusTitle.ForeColor = state.Pending != null ? palette.Warning : state.Enabled ? palette.Accent : palette.Ink;
            if (state.Pending != null) { statusTitle.Text = state.Pending.Acknowledged ? "Confirming your reset" : "A reset needs confirmation"; statusDetail.Text = state.Pending.Acknowledged ? "Codex accepted it. Waiting for refreshed usage before continuing." : "Your original request is saved. Review it in Activity."; }
            else if (engine.Failures > 0) { statusTitle.Text = "Connection needs attention"; statusDetail.Text = engine.Status; }
            else if (state.Enabled) { statusTitle.Text = "Watching your limits"; statusDetail.Text = Data.Percent(state.Rules.Threshold) + " trigger · " + (state.Rules.MaximumResets - state.UsedThisArm) + " reset(s) left · " + (state.ArmedUntil == 0 ? "until paused" : "until " + UsageSummary.ShortDate(state.ArmedUntil)); }
            else { statusTitle.Text = state.History.Count > 0 && state.History[0].Event == "Reset verified" ? "Reset complete · usage refreshed" : "Automatic resets are off"; statusDetail.Text = "Choose allowed credits, then enable your rules."; }
            tips.SetToolTip(statusTitle, engine.Status); tips.SetToolTip(statusDetail, engine.Status);
            if (snapshot != null) { account.Text = snapshot.AccountDisplay + "  ·  " + (snapshot.Plan ?? "ChatGPT"); tips.SetToolTip(account, account.Text); UpdateCredits(snapshot); }
            bool editable = !state.Enabled && state.Pending == null; rulesPanel.Enabled = credits.Enabled = editable; allowAll.Enabled = clearAll.Enabled = editable; startup.Enabled = executable.Enabled = browse.Enabled = editable;
            enable.Text = state.Enabled ? "Pause automatic resets" : state.Pending != null ? "Review pending reset" : "Enable automatic resets"; enable.Enabled = state.Pending != null || state.Enabled || !engine.Busy;
            save.Enabled = !engine.Busy && editable; check.Enabled = !engine.Busy; retryButton.Enabled = !engine.Busy && state.Pending != null;
            lastCheck.Text = engine.Busy ? "Checking Codex…" : (snapshot == null ? "Waiting for your first usage check" : "Updated " + UsageSummary.ShortDate(snapshot.FetchedAt)) + "   ·   Closing keeps the app in your tray";
            string selectedEvent = history.SelectedItems.Count == 0 ? null : (string)history.SelectedItems[0].Tag; history.BeginUpdate(); history.Items.Clear();
            foreach (var h in state.History) { var item = new ListViewItem(new[] { h.Event, UsageSummary.ShortDate(h.At) }) { ToolTipText = h.Detail, Tag = h.Detail }; history.Items.Add(item); if (h.Detail == selectedEvent) item.Selected = true; } history.EndUpdate(); UpdatePresets(); UpdateApproved();
        }
        private void UpdatePresets()
        { foreach (var b in presets) { b.Selected = threshold.Value == (decimal)b.Tag; b.Invalidate(); } if (summary != null) { summary.Threshold = engine.State.Enabled || engine.State.Pending != null ? engine.State.Rules.Threshold : (double)threshold.Value; summary.Invalidate(); } }
        private void UpdateApproved()
        { if (IsDisposed) return; summary.Snapshot = engine.Latest; summary.Approved = credits.Items.Cast<ListViewItem>().Count(i => i.Checked); summary.Invalidate(); var weekly = engine.Latest == null ? null : engine.Latest.Weekly; summary.AccessibleName = "Weekly usage " + (weekly == null ? "unavailable" : Data.Percent(weekly.Used)) + "; " + summary.Approved + " reset credits allowed"; }
        private void UpdateCredits(Snapshot s)
        {
            var selected = new HashSet<string>(initializedCredits && !engine.State.Enabled ? credits.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((ResetCredit)i.Tag).Id) : engine.State.Rules.AllowedCreditIds);
            string preferredId = initializedCredits && !engine.State.Enabled && preferred.SelectedItem is CreditChoice ? ((CreditChoice)preferred.SelectedItem).Id : engine.State.Rules.PreferredCreditId;
            rebuilding = true; credits.BeginUpdate(); credits.Items.Clear(); preferred.BeginUpdate(); preferred.Items.Clear(); preferred.Items.Add(new CreditChoice { Id = null, Label = "Earliest expiry · allowed credits" });
            foreach (var c in s.Credits.OrderBy(c => c.ExpiresAt ?? Int64.MaxValue))
            {
                double days = c.ExpiresAt.HasValue ? (c.ExpiresAt.Value - Data.Now) / 86400.0 : 0;
                var item = new ListViewItem(new[] { c.Title ?? "Full reset", c.ExpiresAt.HasValue ? UsageSummary.ShortDate(c.ExpiresAt.Value) : "Not reported", c.ExpiresAt.HasValue ? days > 0 ? days >= 1 ? days.ToString("0.0") + " days" : Math.Max(0, days * 24).ToString("0.0") + " hours" : "Expired" : "—", c.Status == "available" ? "Available" : c.Status ?? "Unknown" });
                item.Tag = c; item.Checked = selected.Contains(c.Id) && c.Available(Data.Now + 30); item.ToolTipText = c.Display; credits.Items.Add(item); preferred.Items.Add(new CreditChoice { Id = c.Id, Label = c.Display });
            }
            if (!String.IsNullOrWhiteSpace(preferredId) && !s.Credits.Any(c => c.Id == preferredId)) preferred.Items.Add(new CreditChoice { Id = preferredId, Label = "Selected credit · currently unavailable" }); preferred.SelectedIndex = 0;
            for (int i = 1; i < preferred.Items.Count; i++) if (((CreditChoice)preferred.Items[i]).Id == preferredId) preferred.SelectedIndex = i;
            credits.EndUpdate(); preferred.EndUpdate(); rebuilding = false; initializedCredits = true; creditNote.Text = s.Credits.Count == 0 ? "No reset-credit details are available yet. Refresh after earning a banked Codex reset." : "Choose the resets this app may use. Earlier expiries come first; new credits stay unchecked.";
        }
        public void Try(Action action)
        { try { action(); UpdateView(); } catch (Exception ex) { MessageBox.Show(this, ex is GuardException ? ex.Message : "The setting could not be saved. Automatic resets have not been enabled with this change.", "Codex Reset Guard", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
        private void ApplyTheme()
        {
            palette = Palette.Create(Appearance.IsDark(themePreference)); PaintKit.Theme(this, palette);
            if (IsHandleCreated) { int dark = palette.Dark ? 1 : 0; try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); int color = palette.Canvas.R | palette.Canvas.G << 8 | palette.Canvas.B << 16; DwmSetWindowAttribute(Handle, 35, ref color, 4); } catch { } } if (!initializing) UpdateView();
        }
        internal void PreviewTheme(bool dark) { themePreference = dark ? "dark" : "light"; initializing = true; theme.SelectedIndex = dark ? 2 : 1; initializing = false; ApplyTheme(); }
        private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) { if (themePreference == "system" && !IsDisposed && IsHandleCreated) { try { BeginInvoke(new Action(ApplyTheme)); } catch (InvalidOperationException) { } } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (!initializing) ApplyTheme(); }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        { if (keyData == Keys.Escape) { Hide(); return true; } if (keyData == Keys.F5) { refresh(); return true; } if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { Tabs.SelectedIndex = (Tabs.SelectedIndex + (keyData == (Keys.Control | Keys.Tab) ? 1 : 3)) % 4; return true; } return base.ProcessCmdKey(ref msg, keyData); }
        protected override void Dispose(bool disposing) { if (disposing) { if (!demo) SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged; tips.Dispose(); foreach (var images in rowImages) images.Dispose(); if (Icon != null) Icon.Dispose(); } base.Dispose(disposing); }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
    internal static class Startup
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool Enabled { get { using (var key = Registry.CurrentUser.OpenSubKey(Key)) return key != null && String.Equals(key.GetValue("CodexResetGuard") as string, Command, StringComparison.OrdinalIgnoreCase); } }
        private static string Command { get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray"; } }
        public static void Set(bool enabled) { using (var key = Registry.CurrentUser.CreateSubKey(Key)) { if (enabled) key.SetValue("CodexResetGuard", Command); else if (String.Equals(key.GetValue("CodexResetGuard") as string, Command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue("CodexResetGuard", false); } }
    }
}
