using System;
using System.Drawing;
using System.IO;
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
            using (var form = new GuardForm(engine, delegate { }, delegate { }, delegate { }))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000); form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents();
                for (int index = 0; index < form.Tabs.TabPages.Count; index++)
                {
                    form.Tabs.SelectedIndex = index; Application.DoEvents();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(directory, "tab-" + index + ".png")); }
                }
                form.Close();
            }
            File.WriteAllText(Path.Combine(directory, "ui-smoke.json"), Data.Json(new { ok = true, tabsRendered = 4, resetRequestsSent = 0, syntheticAccount = true }));
            return 0;
        }
    }
}
