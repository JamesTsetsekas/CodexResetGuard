using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexResetGuard
{
    internal static class UiSmoke
    {
        private sealed class MemoryStore : IStateStore { public void Save(GuardState state) { } }
        private sealed class DemoGateway : IAccountGateway, IAccountSession
        {
            public Task<IAccountSession> OpenAsync() { return Task.FromResult<IAccountSession>(this); }
            public Task<Snapshot> ReadAsync() { return Task.FromResult(Example()); }
            public Task<string> ConsumeAsync(PendingReset pending) { throw new GuardException("UI smoke mode cannot redeem credits."); }
            public void Dispose() { }
        }
        internal static Snapshot Example()
        {
            long now = Data.Now;
            var s = new Snapshot { AccountDisplay = "demo@example.invalid", AccountKey = "demo-account", IdentityVerified = true, Plan = "pro", FetchedAt = now, AvailableCount = 3, DetailsKnown = true };
            s.Windows.Add(new UsageWindow { Minutes = 10080, Used = 53, ResetsAt = now + 5 * 86400 });
            s.Credits.Add(new ResetCredit { Id = "demo-1", Title = "Full reset", Type = "codexRateLimits", Status = "available", ExpiresAt = now + 11 * 86400 });
            s.Credits.Add(new ResetCredit { Id = "demo-2", Title = "Full reset", Type = "codexRateLimits", Status = "available", ExpiresAt = now + 24 * 86400 });
            s.Credits.Add(new ResetCredit { Id = "demo-3", Title = "Full reset", Type = "codexRateLimits", Status = "available", ExpiresAt = now + 26 * 86400 });
            return s;
        }
        public static int Run(string directory)
        {
            Directory.CreateDirectory(directory);
            var state = new GuardState(); state.Rules.AllowedCreditIds.Add("demo-1"); state.Rules.AllowedCreditIds.Add("demo-2");
            state.Record("Started", "Automatic resets are off. No reset credits have been used.", Data.Now);
            var engine = new ResetEngine(state, new MemoryStore(), new DemoGateway(), () => Data.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult();
            using (var form = new GuardForm(engine, delegate { }, delegate { }, delegate { }, true))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000); form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents();
                for (int theme = 0; theme < 2; theme++)
                {
                    form.PreviewTheme(theme == 1);
                    for (int index = 0; index < form.Tabs.TabPages.Count; index++)
                    {
                        form.Tabs.SelectedIndex = index; Application.DoEvents();
                        Capture(form, directory, (theme == 0 ? "light" : "dark") + "-tab-" + index);
                    }
                }
                var checks = VerifyControls(form, engine, directory);
                File.WriteAllText(Path.Combine(directory, "ui-checks.json"), Data.Json(new { passed = checks.Count, checks = checks, realResetRequests = 0 }));
                form.Close();
            }
            File.WriteAllText(Path.Combine(directory, "ui-smoke.json"), Data.Json(new { ok = true, tabsRendered = 8, resetRequestsSent = 0, syntheticAccount = true }));
            return 0;
        }
        private static void Capture(GuardForm form, string directory, string name)
        {
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(directory, name + ".png")); }
            var content = form.Controls[0];
            using (var bitmap = new Bitmap(content.Width, content.Height)) { content.DrawToBitmap(bitmap, new Rectangle(0, 0, content.Width, content.Height)); bitmap.Save(Path.Combine(directory, name + "-client.png")); }
        }
        private static T Field<T>(GuardForm form, string name) { return (T)typeof(GuardForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
        private static Rules Read(GuardForm form) { return (Rules)typeof(GuardForm).GetMethod("ReadRules", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null); }
        private static List<string> VerifyControls(GuardForm form, ResetEngine engine, string directory)
        {
            var checks = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception(name); checks.Add(name); };
            form.Tabs.SelectedIndex = 0;
            string saved = Data.Json(engine.State);
            var theme = Field<ModernCombo>(form, "theme"); theme.SelectedIndex = 1; theme.SelectedIndex = 2;
            check(Data.Json(engine.State) == saved, "Changing appearance preserves all saved authorization and rules");
            var number = Field<ModernNumber>(form, "threshold"); var input = number.Controls.OfType<TextBox>().Single();
            input.Text = 99.99M.ToString(System.Globalization.CultureInfo.CurrentCulture);
            check(Read(form).Threshold == 99.99, "Typed fractional threshold is committed exactly before saving");
            input.Text = "0"; bool rejected = false;
            try { Read(form); } catch (TargetInvocationException ex) { rejected = ex.InnerException is GuardException; }
            check(rejected && !engine.State.Enabled, "Invalid numeric input blocks saving instead of using a previous value"); number.Value = 95;
            var budget = Field<ModernNumber>(form, "allowance"); budget.Controls.OfType<TextBox>().Single().Text = 1.5M.ToString(System.Globalization.CultureInfo.CurrentCulture);
            check(!budget.Commit(), "Fractional reset budgets are rejected instead of rounded into extra spending"); budget.Value = 1;
            var credits = Field<ListView>(form, "credits");
            check(credits.Items.Count == 3 && !credits.Items[2].Checked, "New credits remain unchecked");
            credits.Items[0].Checked = false; Field<ModernCombo>(form, "preferred").SelectedIndex = 2; form.UpdateView();
            var read = Read(form);
            check(!read.AllowedCreditIds.Contains("demo-1") && read.AllowedCreditIds.Contains("demo-2") && read.PreferredCreditId == "demo-2", "Refresh preserves unsaved credit selections and the chosen specific credit");
            Field<ModernButton>(form, "save").PerformClick();
            check(engine.State.Rules.PreferredCreditId == "demo-2" && !engine.State.Enabled, "Save rules persists selections without enabling resets");
            Field<ModernButton>(form, "enable").PerformClick(); Application.DoEvents();
            check(engine.State.Enabled && engine.State.Rules.PreferredCreditId == "demo-2", "Enable button arms only the displayed saved rules");
            check(!Field<Panel>(form, "rulesPanel").Enabled && !credits.Enabled && !Field<ModernButton>(form, "save").Enabled && theme.Enabled, "Enabled rules lock spending controls while appearance remains available");
            Capture(form, directory, "dark-enabled");
            engine.State.Pending = new PendingReset { Key = "demo-pending-key", CreditId = "demo-2", AccountKey = "demo-account", Acknowledged = true, BeforeWindows = Example().Windows };
            form.UpdateView(); var action = Field<ModernButton>(form, "enable");
            check(action.Enabled && action.Text == "Pause automatic resets", "Pause remains available while a reset is pending");
            action.PerformClick(); Application.DoEvents();
            check(!engine.State.Enabled && engine.State.Pending != null && action.Text == "Review pending reset", "Pause keeps the original pending request and exposes review");
            Capture(form, directory, "dark-pending"); action.PerformClick();
            check(form.Tabs.SelectedIndex == 2 && Field<ModernButton>(form, "retryButton").Enabled, "Pending review opens Activity with reconciliation available");
            engine.State.Pending = null; engine.State.Record("Reset verified", "Usage decreased and the selected credit is no longer available.", Data.Now);
            engine.Latest.Weekly.Used = 0; engine.Latest.AvailableCount = 2; engine.Latest.Credits.RemoveAll(c => c.Id == "demo-2"); form.UpdateView(); form.Tabs.SelectedIndex = 0;
            check(Field<Label>(form, "statusTitle").Text.StartsWith("Reset complete"), "Completed reset presents an explicit refreshed-usage status");
            Capture(form, directory, "dark-complete"); form.PreviewTheme(false); Capture(form, directory, "light-complete");
            engine.Latest.Credits.Clear(); engine.Latest.AvailableCount = 0; form.UpdateView(); form.Tabs.SelectedIndex = 1;
            Capture(form, directory, "light-empty");
            check(credits.Items.Count == 0 && !engine.State.Enabled, "Empty inventory remains unarmed with no selectable credits");
            return checks;
        }
    }
}
