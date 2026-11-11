using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace CodexResetGuard
{
    internal static class TestsMain
    {
        private static int passed;
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--stub-server")
            {
                try { return StubServer(args.Length > 1 && args[1] == "--exit-after-initialize"); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stub-error.txt"), ex.ToString()); return 2; }
            }
            try { Run(); Console.WriteLine("PASS: " + passed + " checks. No real reset requests were sent."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        }
        private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
        private static GuardState Armed(long now)
        {
            var s = new GuardState { Enabled = true, AccountKey = "test-account", ArmId = "arm-1", ArmedUntil = now + 3600 };
            s.Rules.AllowedCreditIds.AddRange(new[] { "late", "early", "no-expiry" }); return s;
        }
        private static Snapshot Sample(long now, double usage)
        {
            var s = new Snapshot { AccountKey = "test-account", AccountDisplay = "test@example.invalid", IdentityVerified = true, Plan = "pro", FetchedAt = now, AvailableCount = 3, DetailsKnown = true, CreditsComplete = true };
            s.Windows.Add(new UsageWindow { Minutes = 10080, Used = usage, ResetsAt = now + 50000 });
            s.Credits.Add(new ResetCredit { Id = "late", Status = "available", Type = "codexRateLimits", ExpiresAt = now + 20000 });
            s.Credits.Add(new ResetCredit { Id = "early", Status = "available", Type = "codexRateLimits", ExpiresAt = now + 10000 });
            s.Credits.Add(new ResetCredit { Id = "no-expiry", Status = "available", Type = "codexRateLimits", ExpiresAt = null }); return s;
        }
        private static T Copy<T>(T value) { return Data.Serializer().Deserialize<T>(Data.Json(value)); }
        private static void Run()
        {
            long now = 1000000; var state = Armed(now); var snapshot = Sample(now, 95);
            Check(Policy.Evaluate(state, snapshot, now, false).Credit.Id == "early", "95% boundary selects earliest expiry, independent of row order");
            snapshot.Windows[0].Used = 94; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Below threshold never redeems");
            state.Rules.Threshold = 99.99; snapshot.Windows[0].Used = 99; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "99.99 does not trigger on rounded 99");
            snapshot.Windows[0].Used = 100; Check(Policy.Evaluate(state, snapshot, now, false).Ready, "99.99 waits for reported 100");
            state = Armed(now); snapshot = Sample(now, 95); state.Enabled = false; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Off by default blocks redemption");
            state.Enabled = true; snapshot.FetchedAt = now - 41; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Stale usage cannot trigger redemption");
            snapshot.FetchedAt = now + 20; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Future-clock snapshot cannot trigger redemption");
            snapshot = Sample(now, 95); snapshot.AccountKey = "different"; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Account mismatch blocks redemption");
            snapshot = Sample(now, 95); snapshot.IdentityVerified = false; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Unverified account blocks redemption");
            snapshot = Sample(now, 95); state.ArmedUntil = now; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Arming deadline is exclusive");
            state = Armed(now); state.UsedThisArm = 1; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Arming reset budget cannot be exceeded");
            state = Armed(now); state.Rules.ReserveCredits = 3; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Reserve credits are preserved");
            state = Armed(now); snapshot.DetailsKnown = false; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Count-only response never falls back to arbitrary credit");
            snapshot = Sample(now, 95); snapshot.AvailableCount = null; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Unknown available count is not zero or guessed");
            snapshot = Sample(now, 95); state.Rules.AllowedCreditIds.Remove("early"); Check(Policy.Evaluate(state, snapshot, now, false).Credit.Id == "late", "Unchecked credit is protected");
            state.Rules.PreferredCreditId = "early"; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Specific unapproved credit never falls back");
            state = Armed(now); state.Rules.PreferredCreditId = "late"; Check(Policy.Evaluate(state, snapshot, now, false).Credit.Id == "late", "Specific approved credit overrides expiry order");
            state = Armed(now); snapshot.Credits.First(c => c.Id == "early").ExpiresAt = now + 29; Check(Policy.Evaluate(state, snapshot, now, false).Credit.Id == "late", "Expiring-during-request credit is skipped");
            snapshot = Sample(now, 95); snapshot.Credits.First(c => c.Id == "early").Type = "other"; Check(Policy.Evaluate(state, snapshot, now, false).Credit.Id == "late", "Other reset types are not redeemed");
            snapshot = Sample(now, 95); snapshot.Windows[0].ResetsAt = now + 20; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Imminent natural reset conserves credit");
            snapshot.Windows[0].ResetsAt = null; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Unknown reset anchor blocks redemption");
            snapshot = Sample(now, 95); state.Rules.Window = "fiveHour"; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Weekly window is not assumed to be five-hour primary");
            snapshot.Windows.Add(new UsageWindow { Minutes = 300, Used = 96, ResetsAt = now + 3600 }); Check(Policy.Evaluate(state, snapshot, now, false).Ready, "Five-hour trigger is separately selectable");
            state = Armed(now); state.LastSuccessAt = now - 200; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "Success cooldown prevents rapid repeat spending");
            state = Armed(now); state.LastAttemptAt = now - 30; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "No-op attempts are rate limited");
            var data = Data.Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":99,\"windowDurationMins\":10080,\"resetsAt\":2000000}},\"rateLimitsByLimitId\":{\"codex_spark\":{\"primary\":{\"usedPercent\":100,\"windowDurationMins\":10080,\"resetsAt\":2000000}}}}");
            Check(Snapshot.Parse(data, now).Windows.Count == 0, "Multi-bucket response without codex cannot silently select another bucket");
            data.Remove("rateLimitsByLimitId"); Check(Snapshot.Parse(data, now).Weekly.Used == 99, "Legacy codex bucket remains supported");
            data = Data.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":101,\"windowDurationMins\":10080}},\"rateLimitResetCredits\":{\"availableCount\":3,\"credits\":null}}");
            var malformed = Snapshot.Parse(data, now); Check(malformed.Windows.Count == 0 && !malformed.DetailsKnown, "Invalid percentages and unknown details fail closed");
            var bad = Snapshot.Parse(Data.Parse("{\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":[{\"id\":\"early\",\"resetType\":\"codexRateLimits\",\"status\":\"available\",\"expiresAt\":\"bad\"}]}}"), now);
            bad.AccountKey = "test-account"; Check(bad.DetailsKnown && !bad.CreditsComplete && !Policy.CreditGone(new PendingReset { CreditId = "early", AccountKey = "test-account" }, bad), "A malformed credit row never proves that a pending credit is gone");
            data = Data.Parse("{\"rateLimitResetCredits\":{\"availableCount\":3,\"credits\":[{\"id\":\"x\",\"resetType\":\"codexRateLimits\",\"status\":\"available\",\"expiresAt\":\"invalid\"}]}}");
            Check(Snapshot.Parse(data, now).Credits.Count == 0, "Malformed expiry is never treated as a never-expiring credit");
            EngineTests(); StorageTests(); TransportTests(); ClaudeTests(); TimingTests(); RecoveryTests(); ValidationTests(); MoreClaudeTests();
        }
        private sealed class MemoryStore : IStateStore
        {
            public GuardState Saved;
            public bool Fail;
            public void Save(GuardState state) { if (Fail) throw new GuardException("Simulated disk failure"); Saved = Copy(state); }
        }
        private sealed class FakeGateway : IAccountGateway, IAccountSession
        {
            public long Now = 1000000;
            public Snapshot Snapshot;
            public bool ApplyBeforeTimeout;
            public bool FailBeforeApplyOnce;
            public bool StaleAfterReset;
            public string Outcome = "reset";
            public TaskCompletionSource<bool> ReadGate;
            public List<string> Keys = new List<string>();
            public List<string> Credits = new List<string>();
            public FakeGateway() { Snapshot = Sample(Now, 95); }
            public Task<IAccountSession> OpenAsync() { return Task.FromResult<IAccountSession>(this); }
            public async Task<Snapshot> ReadAsync()
            {
                if (ReadGate != null) { var g = ReadGate; ReadGate = null; await g.Task; }
                var s = Copy(Snapshot); s.FetchedAt = Now; return s;
            }
            public Task<string> ConsumeAsync(PendingReset pending)
            {
                Keys.Add(pending.Key); Credits.Add(pending.CreditId);
                if (FailBeforeApplyOnce && Keys.Count == 1) throw new GuardException("Simulated failed delivery before server committed");
                string outcome = Outcome;
                if (outcome == "reset" && !StaleAfterReset)
                {
                    bool removed = Snapshot.Credits.RemoveAll(c => c.Id == pending.CreditId) > 0;
                    if (!removed) return Task.FromResult("alreadyRedeemed");
                    Snapshot.AvailableCount--; Snapshot.Windows[0].Used = 0; Snapshot.Windows[0].ResetsAt = Now + 604800;
                }
                if (ApplyBeforeTimeout && Keys.Count == 1) throw new GuardException("Simulated timeout after server committed");
                return Task.FromResult(outcome);
            }
            public void Dispose() { }
        }
        private static void EngineTests()
        {
            var pendingGateway = new FakeGateway { FailBeforeApplyOnce = true }; var pendingStore = new MemoryStore();
            var pendingEngine = new ResetEngine(Armed(pendingGateway.Now), pendingStore, pendingGateway, () => pendingGateway.Now);
            pendingEngine.CheckAsync(false).GetAwaiter().GetResult(); pendingGateway.Now += 61; pendingGateway.Snapshot.Windows[0].Used = 1;
            pendingEngine.CheckAsync(false).GetAwaiter().GetResult();
            Check(pendingGateway.Keys.Count == 1 && !pendingEngine.State.Enabled && pendingEngine.State.Pending != null, "Automatic recovery never spends below the current threshold after an undelivered request");
            string pendingKey = pendingEngine.State.Pending.Key;
            pendingEngine.CheckAsync(true).GetAwaiter().GetResult();
            Check(pendingGateway.Keys.Count == 2 && pendingGateway.Keys.All(k => k == pendingKey) && pendingEngine.State.Pending == null && !pendingEngine.State.Enabled, "Explicit reconciliation can finish the original request without enabling further spending");
            pendingGateway = new FakeGateway { FailBeforeApplyOnce = true }; pendingStore = new MemoryStore();
            pendingEngine = new ResetEngine(Armed(pendingGateway.Now), pendingStore, pendingGateway, () => pendingGateway.Now);
            pendingEngine.CheckAsync(false).GetAwaiter().GetResult(); pendingGateway.Now += 61; pendingGateway.Snapshot.AvailableCount = 1;
            pendingEngine.CheckAsync(false).GetAwaiter().GetResult();
            Check(pendingGateway.Keys.Count == 1 && !pendingEngine.State.Enabled && pendingEngine.State.Pending != null, "Automatic retry preserves the current reserve after another client spends credits");
            pendingGateway = new FakeGateway { FailBeforeApplyOnce = true }; pendingStore = new MemoryStore();
            pendingEngine = new ResetEngine(Armed(pendingGateway.Now), pendingStore, pendingGateway, () => pendingGateway.Now);
            pendingEngine.CheckAsync(false).GetAwaiter().GetResult(); pendingGateway.Now += 61;
            pendingGateway.Snapshot.Credits.First(c => c.Id == "early").ExpiresAt = pendingGateway.Now + 15;
            pendingEngine.CheckAsync(false).GetAwaiter().GetResult();
            Check(pendingGateway.Keys.Count == 1 && !pendingEngine.State.Enabled, "Automatic recovery pauses when the original credit is about to expire");
            pendingGateway = new FakeGateway { FailBeforeApplyOnce = true }; pendingStore = new MemoryStore();
            pendingEngine = new ResetEngine(Armed(pendingGateway.Now), pendingStore, pendingGateway, () => pendingGateway.Now);
            pendingEngine.CheckAsync(false).GetAwaiter().GetResult(); pendingKey = pendingEngine.State.Pending.Key; pendingGateway.Now += 61;
            pendingGateway.Snapshot.Credits.First(c => c.Id == "late").ExpiresAt = pendingGateway.Now + 100;
            pendingEngine.CheckAsync(false).GetAwaiter().GetResult();
            Check(pendingGateway.Keys.Count == 2 && pendingGateway.Keys.All(k => k == pendingKey) && pendingGateway.Credits.All(c => c == "early") && pendingEngine.State.Pending == null, "A still-eligible automatic retry keeps the original credit and key when expiry order changes");
            var gateway = new FakeGateway(); var store = new MemoryStore(); var engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 1 && gateway.Credits[0] == "early", "Engine sends exactly the approved earliest credit");
            Check(engine.State.Pending == null && engine.State.UsedThisArm == 1 && !engine.State.Enabled, "Successful reset verifies and exhausts the one-reset allowance");
            gateway.Now += 1000; engine.CheckAsync(false).GetAwaiter().GetResult(); Check(gateway.Keys.Count == 1, "Polling after disarming never redeems again");
            gateway = new FakeGateway { ApplyBeforeTimeout = true }; store = new MemoryStore(); engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); string key = engine.State.Pending.Key;
            Check(store.Saved.Pending != null && store.Saved.Pending.Attempts == 1 && !store.Saved.Pending.Acknowledged, "Ambiguous response retains the durable request journal");
            gateway.Now += 61; engine = new ResetEngine(Copy(store.Saved), store, gateway, () => gateway.Now); engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 2 && gateway.Keys.All(k => k == key) && gateway.Credits.All(c => c == "early"), "A lost reply whose credit is already gone is recovered automatically with only the original key and credit");
            Check(engine.State.UsedThisArm == 1 && engine.State.Pending == null && engine.State.History[0].Event == "Reset verified", "alreadyRedeemed charges the allowance once and verifies");
            gateway = new FakeGateway { ApplyBeforeTimeout = true }; store = new MemoryStore(); engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); engine.Pause(); gateway.Now += 61; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 1 && engine.State.Pending != null, "Pause blocks even automatic retries of an ambiguous request");
            engine.CheckAsync(true).GetAwaiter().GetResult(); Check(gateway.Keys.Count == 2 && engine.State.Pending == null, "Explicit pending retry reconciles without arming more credits");
            gateway = new FakeGateway(); store = new MemoryStore { Fail = true }; engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); Check(gateway.Keys.Count == 0 && !engine.State.Enabled && engine.State.Pending == null, "Journal write failure prevents any consume request and leaves no phantom pending reset");
            gateway = new FakeGateway(); gateway.Snapshot.AccountKey = "switched-account"; store = new MemoryStore(); engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); Check(gateway.Keys.Count == 0 && !engine.State.Enabled, "Account changes automatically disarm the engine");
            gateway = new FakeGateway { StaleAfterReset = true }; store = new MemoryStore(); var s = Armed(gateway.Now); s.Rules.MaximumResets = 3;
            engine = new ResetEngine(s, store, gateway, () => gateway.Now); engine.CheckAsync(false).GetAwaiter().GetResult(); gateway.Now += 1000; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 1 && engine.State.Pending.Acknowledged, "Stale post-reset usage blocks all additional spending");
            gateway = new FakeGateway { Outcome = "nothingToReset" }; store = new MemoryStore(); engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); Check(engine.State.Pending == null && engine.State.UsedThisArm == 0, "nothingToReset does not charge an allowance");
            gateway = new FakeGateway { Outcome = "unrecognized" }; store = new MemoryStore(); engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); Check(!engine.State.Enabled && engine.State.Pending != null, "Unknown outcomes pause and preserve recovery identity");
            gateway = new FakeGateway(); store = new MemoryStore(); engine = new ResetEngine(new GuardState(), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); bool threw = false; try { engine.Arm(new Rules()); } catch (GuardException) { threw = true; }
            Check(threw && gateway.Keys.Count == 0, "Enabling requires at least one explicitly selected live credit");
            engine.Arm(Armed(gateway.Now).Rules); threw = false;
            try { engine.Arm(Armed(gateway.Now).Rules); } catch (GuardException) { threw = true; }
            Check(threw && engine.State.UsedThisArm == 0, "An already-enabled allowance cannot be silently rearmed");
        }
        private static void StorageTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CodexResetGuard-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var store = new StateStore(directory); Check(!store.Load().Enabled, "New installations start disabled");
                var state = Armed(1000000); store.Save(state); state.Rules.Threshold = 99; store.Save(state);
                Check(store.Load().Rules.Threshold == 99 && !File.Exists(store.StatePath + ".tmp"), "Atomic replacement persists rules without leftover temporary journal");
                File.WriteAllText(store.StatePath, "{broken"); bool threw = false; try { store.Load(); } catch (GuardException) { threw = true; }
                Check(threw && File.ReadAllText(store.StatePath) == "{broken", "Corrupt journals stop startup and are never silently discarded");
            }
            finally { Directory.Delete(directory, true); } // Unique test-owned directory only.
        }
        private static void TransportTests()
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            using (var session = new CodexSession(executable, "--stub-server"))
            {
                session.InitializeAsync().GetAwaiter().GetResult(); var s = session.ReadAsync().GetAwaiter().GetResult();
                Check(s.IdentityVerified && s.Weekly.Used == 95 && s.AvailableCount == 3, "Real stdio transport completes handshake and parses account usage");
                var pending = new PendingReset { AccountKey = "test-account", CreditId = "early", Key = Guid.NewGuid().ToString() };
                Check(session.ConsumeAsync(pending).GetAwaiter().GetResult() == "reset", "Real transport sends the explicit creditId to the isolated fake server");
                Check(session.ConsumeAsync(pending).GetAwaiter().GetResult() == "alreadyRedeemed", "Real transport preserves idempotency on repeated calls");
                Check(session.ReadAsync().GetAwaiter().GetResult().Weekly.Used == 0, "Real transport receives refreshed fake usage after redemption");
            }
            using (var rpc = new RpcClient(executable, "--stub-server"))
            {
                bool blocked = false; try { rpc.CallAsync("thread/start", new { }).GetAwaiter().GetResult(); } catch (GuardException) { blocked = true; }
                Check(blocked, "Transport allowlist cannot start model threads");
            }
        }
        private static void ClaudeTests()
        {
            long now = 1800000000;
            string iso = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(now + 7200).ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00");
            string week = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(now + 200000).ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00");
            var body = Data.Parse("{\"five_hour\":{\"utilization\":96.0,\"resets_at\":\"" + iso + "\"},\"seven_day\":{\"utilization\":40.0,\"resets_at\":\"" + week + "\"},\"seven_day_opus\":null," +
                "\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":15,\"resets_at\":\"" + week + "\",\"scope\":{\"model\":{\"display_name\":\"Fable\"}}}]," +
                "\"cedar_ember\":{\"eligible\":true,\"at_limit\":false,\"grants\":[{\"id\":\"g1\",\"label\":\"Limit reset\",\"resets_left\":2,\"ends_at\":\"" + week + "\",\"use_requires_limit\":true},{\"id\":\"g2\",\"resets_left\":1,\"paused\":true}]}}");
            var c = ClaudeSnapshot.Parse(body, now);
            Check(c.FiveHour.Used == 96 && c.FiveHour.ResetsAt == now + 7200 && c.Weekly.Used == 40, "Claude usage windows and ISO reset times parse");
            Check(c.Windows.Any(w => w.Kind == "model" && w.Name == "Fable" && w.Used == 15), "Claude per-model weekly window parses");
            Check(c.ResetsHeld(now) == 2 && c.FirstExpiry(now) == now + 200000, "Paused Claude grants are not counted as usable resets");
            var off = new ClaudeSettings(); Check(ClaudePolicy.Evaluate(off, c, now).Count == 0, "Claude alerts are off by default");
            var on = new ClaudeSettings { Enabled = true };
            var alerts = ClaudePolicy.Evaluate(on, c, now);
            Check(alerts.Count == 1 && alerts[0].Key.EndsWith(":threshold") && alerts[0].Message.Contains("2 resets"), "Claude threshold alert names held resets");
            c.FiveHour.Used = 100; alerts = ClaudePolicy.Evaluate(on, c, now);
            Check(alerts.Count == 1 && alerts[0].Key.EndsWith(":limit"), "Claude limit alert is distinct from the threshold alert");
            c.Weekly.Used = 99; alerts = ClaudePolicy.Evaluate(on, c, now);
            Check(alerts.Count == 2, "A second Claude window can alert after the first was already announced");
            c.FiveHour.ResetsAt = now + 60; alerts = ClaudePolicy.Evaluate(on, c, now);
            Check(alerts.All(a => !a.Key.StartsWith("fiveHour")), "Imminent natural Claude resets do not alert");
            Check(ClaudePolicy.Evaluate(on, c, now + 600).Count == 0, "Stale Claude usage never alerts");
            var surface = ClaudeSnapshot.Parse(Data.Parse("{\"five_hour\":{\"utilization\":5},\"cedar_ember\":{\"eligible\":false,\"ineligible_reason\":\"surface\",\"grants\":[]}}"), now);
            Check(surface.ResetSummary(now).Contains("only in Claude apps") && surface.FiveHour.ResetsAt == null, "Claude reset details hidden from third-party apps are explained, not guessed");
            var malformed = ClaudeSnapshot.Parse(Data.Parse("{\"five_hour\":{\"utilization\":\"x\"},\"cedar_ember\":{\"eligible\":true,\"grants\":[{\"id\":\"\",\"resets_left\":3},{\"id\":\"ok\",\"resets_left\":-1}]}}"), now);
            Check(malformed.Windows.Count == 0 && malformed.Grants.Count == 0, "Malformed Claude usage and grants are ignored");
            var monitor = new ClaudeMonitor(on, new FixedClaude(c), () => now, delegate { });
            int notices = 0; monitor.Notice += delegate { notices++; };
            c.FetchedAt = now; monitor.CheckAsync().GetAwaiter().GetResult(); monitor.CheckAsync().GetAwaiter().GetResult();
            Check(notices == 1 && monitor.Settings.AlertedKeys.Count == 1, "Each Claude alert is shown once per window");
        }
        private static void TimingTests()
        {
            long now = 1000000; var state = Armed(now); var snapshot = Sample(now, 96);
            snapshot.Windows[0].ResetsAt = now + 600; Check(!Policy.Evaluate(state, snapshot, now, false).Ready, "A credit is saved when the window resets on its own within the margin");
            state.Rules.SkipNearResetMinutes = 0; Check(Policy.Evaluate(state, snapshot, now, false).Ready, "A zero margin restores reset-anyway behaviour");
            state = Armed(now); state.Rules.Trigger = "predictive"; snapshot = Sample(now, 96);
            Check(!Policy.Evaluate(state, snapshot, now, false, new TriggerContext()).Ready, "Predictive timing holds above the threshold while the limit is not near");
            var context = new TriggerContext(); context.Samples[10080] = new List<KeyValuePair<long, double>> { new KeyValuePair<long, double>(now - 60, 90), new KeyValuePair<long, double>(now, 96) };
            Check(Policy.Evaluate(state, snapshot, now, false, context).Ready, "Predictive timing resets when the projected limit is about a minute away");
            context.Samples[10080] = new List<KeyValuePair<long, double>> { new KeyValuePair<long, double>(now - 600, 95), new KeyValuePair<long, double>(now, 96) };
            Check(!Policy.Evaluate(state, snapshot, now, false, context).Ready, "Predictive timing keeps waiting while usage rises slowly");
            snapshot.Windows[0].Used = 99; Check(Policy.Evaluate(state, snapshot, now, false, context).Ready, "Predictive timing always resets by a reported 99%");
            state.Rules.Threshold = 99.5; snapshot.Windows[0].Used = 99; Check(!Policy.Evaluate(state, snapshot, now, false, context).Ready, "Predictive timing never resets below the threshold");
            state = Armed(now); state.Rules.Trigger = "limit"; snapshot = Sample(now, 99); context = new TriggerContext();
            Check(!Policy.Evaluate(state, snapshot, now, false, context).Ready, "At-the-limit timing waits for a reported 100%");
            snapshot.Windows[0].Used = 100; context.FirstAtLimit[10080] = now; context.CodexIdleSeconds = 5;
            Check(!Policy.Evaluate(state, snapshot, now, false, context).Ready, "At-the-limit timing lets a running Codex turn continue");
            context.CodexIdleSeconds = 25; Check(Policy.Evaluate(state, snapshot, now, false, context).Ready, "At-the-limit timing resets once Codex is idle");
            var retry = Armed(now); retry.Rules.Trigger = "limit"; retry.Pending = new PendingReset { Key = "k", CreditId = "early", AccountKey = "test-account", BeforeWindows = new List<UsageWindow>() };
            var retrySnapshot = Sample(now, 99); var retryDecision = Policy.EvaluatePendingRetry(retry, retrySnapshot, now, new TriggerContext());
            Check(!retryDecision.Ready && retryDecision.Held, "Automatic retries honour at-the-limit timing");
            context.CodexIdleSeconds = 1; context.FirstAtLimit[10080] = now - 180; Check(Policy.Evaluate(state, snapshot, now, false, context).Ready, "At-the-limit timing resets when the grace period ends, even if Codex stays busy");
            state.Rules.GraceMinutes = 0; context.FirstAtLimit[10080] = now; Check(Policy.Evaluate(state, snapshot, now, false, context).Ready, "A zero grace period resets immediately at the limit");
            // The engine records 100% and trend samples from its own reads.
            var gateway = new FakeGateway(); gateway.Snapshot.Windows[0].Used = 100; var s = Armed(gateway.Now); s.Rules.Trigger = "limit";
            var engine = new ResetEngine(s, new MemoryStore(), gateway, () => gateway.Now); engine.IdleProbe = n => 2;
            engine.CheckAsync(false).GetAwaiter().GetResult(); Check(gateway.Keys.Count == 0, "Engine holds at the limit while Codex is busy");
            gateway.Now += 181; engine.CheckAsync(false).GetAwaiter().GetResult(); Check(gateway.Keys.Count == 1, "Engine resets after the at-limit grace period");
            gateway = new FakeGateway(); gateway.Snapshot.Windows[0].Used = 92; s = Armed(gateway.Now); engine = new ResetEngine(s, new MemoryStore(), gateway, () => gateway.Now); engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(engine.PollSeconds == 15, "Near-threshold polling uses the watched window");
            s.Rules.Window = "fiveHour"; gateway.Snapshot.Windows.Add(new UsageWindow { Minutes = 300, Used = 10, ResetsAt = gateway.Now + 3600 }); engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(engine.PollSeconds == 30 && gateway.Keys.Count == 0, "An unwatched high window neither speeds polling nor triggers a reset");
        }
        private static void RecoveryTests()
        {
            GuardState s;
            // Pause while a manual reconciliation is still reading must stop it from sending.
            var gateway = new FakeGateway { FailBeforeApplyOnce = true }; var store = new MemoryStore();
            var engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); gateway.Now += 61;
            var gate = new TaskCompletionSource<bool>(); gateway.ReadGate = gate; var running = engine.CheckAsync(true);
            engine.Pause(); gate.SetResult(true); running.GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 1 && engine.State.Pending != null && !engine.State.Pending.Acknowledged, "Pause cancels a reconciliation that had not been sent yet");
            engine.CheckAsync(true).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 2 && engine.State.Pending == null, "Reconciliation started after Pause is still honoured");
            // An acknowledged reset is confirmed by the lower inventory when usage did not visibly drop.
            gateway = new FakeGateway { StaleAfterReset = true }; store = new MemoryStore(); engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); Check(engine.State.Pending != null && engine.State.Pending.Acknowledged, "Acknowledged reset waits for confirmation");
            gateway.Snapshot.Credits.RemoveAll(c => c.Id == "early"); gateway.Snapshot.AvailableCount = 2; gateway.Now += 30; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(engine.State.Pending == null && gateway.Keys.Count == 1 && engine.State.RefreshBarrier != null, "A gone credit and lower inventory close an acknowledged reset but keep a refresh barrier");
            engine.State.Rules.MaximumResets = 3; engine.State.Enabled = true; engine.State.Rules.ReserveCredits = 0; gateway.StaleAfterReset = false; gateway.Now += 700; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 1 && engine.State.RefreshBarrier != null, "Stale usage after an inventory-only confirmation never authorizes another credit");
            gateway.Snapshot.Windows[0].Used = 40; gateway.Now += 30; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(engine.State.RefreshBarrier == null && gateway.Keys.Count == 1, "Refreshed usage lifts the barrier without spending");
            // An acknowledged reset that can never be confirmed can be closed without spending.
            gateway = new FakeGateway { StaleAfterReset = true }; store = new MemoryStore(); engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); engine.CloseAcknowledged();
            Check(engine.State.Pending == null && engine.State.UsedThisArm == 1 && store.Saved.Pending == null && gateway.Keys.Count == 1, "Closing an acknowledged reset keeps it counted and sends nothing");
            bool threw = false; gateway = new FakeGateway { ApplyBeforeTimeout = true }; engine = new ResetEngine(Armed(gateway.Now), new MemoryStore(), gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); try { engine.CloseAcknowledged(); } catch (GuardException) { threw = true; }
            Check(threw && engine.State.Pending != null, "An unacknowledged request cannot be closed without reconciliation");
            // A resend of a gone credit that Codex now declines was most likely applied earlier: count it, never re-spend.
            gateway = new FakeGateway { ApplyBeforeTimeout = true }; s = null; var lost = Armed(gateway.Now); lost.Rules.MaximumResets = 3; lost.Rules.ReserveCredits = 0;
            engine = new ResetEngine(lost, new MemoryStore(), gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); gateway.Outcome = "noCredit"; gateway.Now += 61; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(engine.State.Pending == null && engine.State.UsedThisArm == 1 && engine.State.RefreshBarrier != null && engine.State.History[0].Event == "Reset counted", "A declined resend of a gone credit is counted as used, not forgotten");
            // Declined requests back off and collapse into one history entry.
            gateway = new FakeGateway { Outcome = "nothingToReset" }; s = Armed(gateway.Now); s.ArmedUntil = gateway.Now + 86400; engine = new ResetEngine(s, new MemoryStore(), gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); gateway.Now += 121; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 1, "A declined request widens the next attempt cooldown");
            gateway.Now += 300; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 2 && engine.State.NoOpStreak == 2 && engine.State.History.Count(h => h.Event.StartsWith("No reset used")) == 1 && engine.State.History[0].Event == "No reset used ×2", "Repeated declines are recorded once with a count");
            for (int i = 0; i < 3; i++) { gateway.Now += 1801; engine.CheckAsync(false).GetAwaiter().GetResult(); }
            Check(gateway.Keys.Count == 5 && !engine.State.Enabled && engine.State.UsedThisArm == 0, "Five declines in a row pause automation without charging the allowance");
            // A server reply written just before the helper exits is still delivered.
            using (var rpc = new RpcClient(Assembly.GetExecutingAssembly().Location, "--stub-server --exit-after-initialize"))
            {
                var result = rpc.CallAsync("initialize", new { }).GetAwaiter().GetResult();
                Check(Data.Text(result, "userAgent") == "isolated-test-server", "A reply flushed right before the helper exits is not discarded");
                bool closed = false; try { System.Threading.Thread.Sleep(300); rpc.CallAsync("account/read", new { }).GetAwaiter().GetResult(); } catch (GuardException) { closed = true; }
                Check(closed, "Calls after the helper exits fail promptly");
            }
            Check(CodexGateway.SupportedVersion("codex-cli 0.147.0") && CodexGateway.SupportedVersion("codex-cli 0.167.1\r\n") && CodexGateway.SupportedVersion("codex 1.0.0") && !CodexGateway.SupportedVersion("codex-cli 0.146.9") && !CodexGateway.SupportedVersion("something 2.0.0"), "Codex version gate accepts 0.147+ and rejects older or unknown output");
            gateway = new FakeGateway(); engine = new ResetEngine(Armed(gateway.Now), new MemoryStore(), gateway, () => gateway.Now);
            var failing = new FailingGateway(); var down = new ResetEngine(Armed(gateway.Now), new MemoryStore(), failing, () => gateway.Now);
            for (int i = 0; i < 6; i++) down.CheckAsync(false).GetAwaiter().GetResult();
            Check(down.PollSeconds == 300, "Connection back-off reaches the documented five-minute cap");
        }
        private sealed class FailingGateway : IAccountGateway { public Task<IAccountSession> OpenAsync() { throw new GuardException("offline"); } }
        private static void ValidationTests()
        {
            Func<Action<Rules>, bool> invalid = change => { var r = new Rules(); change(r); return r.Validate() != null; };
            Check(new Rules().Validate() == null && invalid(r => r.Threshold = 0.99) && invalid(r => r.Threshold = 100.01) && invalid(r => r.Threshold = double.NaN) && invalid(r => r.Window = "monthly") && invalid(r => r.MaximumResets = 0) && invalid(r => r.ReserveCredits = -1) && invalid(r => r.ArmHours = 169) && invalid(r => r.AllowedCreditIds = null), "Rule validation rejects out-of-range values");
            Check(invalid(r => r.Trigger = "sometimes") && invalid(r => r.GraceMinutes = 31) && invalid(r => r.SkipNearResetMinutes = -1), "Timing validation rejects unsupported modes and ranges");
            long now = 1000000; var state = Armed(now); state.Rules.ReserveCredits = 3; var d = Policy.Evaluate(state, Sample(now, 99), now, false);
            Check(!d.Ready && d.Blocked, "A reserve that consumes every credit is reported as blocked, not as waiting");
            var gateway = new FakeGateway(); var engine = new ResetEngine(new GuardState(), new MemoryStore(), gateway, () => gateway.Now); engine.CheckAsync(false).GetAwaiter().GetResult();
            var rules = Armed(now).Rules; rules.ReserveCredits = 3; bool threw = false; try { engine.Arm(rules); } catch (GuardException) { threw = true; }
            Check(threw && !engine.State.Enabled, "Enabling refuses rules whose reserve leaves nothing usable");
            rules = Armed(now).Rules; rules.Window = "fiveHour"; threw = false; try { engine.Arm(rules); } catch (GuardException) { threw = true; }
            Check(threw && !engine.State.Enabled, "Enabling refuses a usage window Codex does not report");
            string directory = Path.Combine(Path.GetTempPath(), "CodexResetGuard-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var store = new StateStore(directory); var armed = Armed(now); armed.ArmedUntil = 0; File.WriteAllText(store.StatePath, Data.Json(armed));
                threw = false; try { store.Load(); } catch (GuardException) { threw = true; }
                Check(threw, "An enabled timed period without its deadline fails closed");
                var old = Data.Parse(Data.Json(Armed(now))); var oldRules = Data.Map(old["Rules"]); oldRules.Remove("Trigger"); oldRules.Remove("GraceMinutes"); oldRules.Remove("SkipNearResetMinutes"); old.Remove("Claude"); old.Remove("NoOpStreak");
                File.WriteAllText(store.StatePath, Data.Json(old)); var loaded = store.Load();
                Check(loaded.Rules.Trigger == "threshold" && loaded.Rules.SkipNearResetMinutes == 15 && loaded.Claude != null && !loaded.Claude.Enabled && loaded.Enabled, "Version 1.1 journals load with safe defaults for new settings");
            }
            finally { Directory.Delete(directory, true); }
        }
        private static void MoreClaudeTests()
        {
            long now = 1800000000;
            var c = new ClaudeSnapshot { FetchedAt = now, ResetStatusKnown = true };
            c.Windows.Add(new ClaudeWindow { Kind = "fiveHour", Name = "5-hour", Used = 100, ResetsAt = now + 3600 });
            c.Windows.Add(new ClaudeWindow { Kind = "weekly", Name = "Weekly", Used = 97, ResetsAt = now + 90000 });
            var settings = new ClaudeSettings { Enabled = true, AlertAtLimit = false };
            var alerts = ClaudePolicy.Evaluate(settings, c, now);
            Check(alerts.Count == 2 && alerts.All(a => a.Key.EndsWith(":threshold")), "With limit alerts off, a full Claude window raises only its threshold alert");
            settings.Window = "weekly"; alerts = ClaudePolicy.Evaluate(settings, c, now);
            Check(alerts.Count == 1 && alerts[0].Key.StartsWith("weekly"), "The Claude Watch setting filters which windows alert");
            var key = alerts[0].Key; c.Windows[1].ResetsAt += 30;
            Check(ClaudePolicy.Evaluate(settings, c, now)[0].Key == key || (now + 90000) / 600 != (now + 90030) / 600, "Small Claude reset-time jitter keeps the same alert key");
            var monitor = new ClaudeMonitor(new ClaudeSettings { Enabled = true, Window = "fiveHour" }, new FailingClaude(), () => now, delegate { });
            monitor.CheckAsync().GetAwaiter().GetResult(); monitor.CheckAsync().GetAwaiter().GetResult();
            Check(monitor.Failures == 2 && monitor.PollSeconds == 240 && monitor.Status == "Claude sign-in expired", "Claude failures surface their reason and back off");
            bool threw = false; try { monitor.Apply(new ClaudeSettings { Enabled = true, AlertThreshold = 0 }); } catch (GuardException) { threw = true; }
            Check(threw && monitor.Settings.Window == "fiveHour", "Invalid Claude settings are rejected without changing the saved ones");
            monitor.Apply(new ClaudeSettings { Enabled = false }); Check(monitor.Latest == null && monitor.Failures == 0, "Turning Claude monitoring off clears its state");
        }
        private sealed class FailingClaude : IClaudeSource { public Task<ClaudeSnapshot> ReadAsync() { throw new GuardException("Claude sign-in expired"); } }
        private sealed class FixedClaude : IClaudeSource
        {
            private readonly ClaudeSnapshot snapshot;
            public FixedClaude(ClaudeSnapshot s) { snapshot = s; }
            public Task<ClaudeSnapshot> ReadAsync() { return Task.FromResult(snapshot); }
        }
        private static int StubServer(bool exitAfterInitialize)
        {
            Console.InputEncoding = new System.Text.UTF8Encoding(false);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            bool initialized = false; var keys = new HashSet<string>(); bool reset = false; string line;
            while ((line = Console.ReadLine()) != null)
            {
                Dictionary<string, object> m;
                try { m = Data.Parse(line.TrimStart('\uFEFF')); } catch (Exception ex) { throw new Exception("Test input code points: " + String.Join(",", line.Take(32).Select(c => ((int)c).ToString()).ToArray()), ex); }
                string method = Data.Text(m, "method"); var id = Data.Get(m, "id"); var p = Data.Map(Data.Get(m, "params"));
                if (method == "initialized") { initialized = true; continue; }
                object result;
                if (method == "initialize" && exitAfterInitialize) { Console.WriteLine(Data.Json(new { id = id, result = new { userAgent = "isolated-test-server" } })); Console.Out.Flush(); return 0; }
                if (method == "initialize") result = new { userAgent = "isolated-test-server" };
                else if (!initialized) { Console.WriteLine(Data.Json(new { id = id, error = new { code = -32000 } })); continue; }
                else if (method == "account/read") result = new { account = new { type = "chatgpt", email = "test@example.invalid", planType = "pro" }, requiresOpenaiAuth = true };
                else if (method == "account/rateLimits/read") result = new { rateLimits = new { limitId = "codex", primary = new { usedPercent = reset ? 0 : 95, windowDurationMins = 10080, resetsAt = Data.Now + 50000 } }, rateLimitResetCredits = new { availableCount = reset ? 2 : 3, credits = reset ? new object[0] : new object[] { new { id = "early", resetType = "codexRateLimits", status = "available", expiresAt = Data.Now + 10000, grantedAt = Data.Now - 10000 } } } };
                else if (method == "account/rateLimitResetCredit/consume")
                {
                    string key = Data.Text(p, "idempotencyKey");
                    if (Data.Text(p, "creditId") != "early" || String.IsNullOrWhiteSpace(key)) { Console.WriteLine(Data.Json(new { id = id, error = new { code = -32602 } })); continue; }
                    bool existing = !keys.Add(key); reset = true; result = new { outcome = existing ? "alreadyRedeemed" : "reset" };
                }
                else { Console.WriteLine(Data.Json(new { id = id, error = new { code = -32601 } })); continue; }
                Console.WriteLine(Data.Json(new { id = id, result = result })); Console.Out.Flush();
            }
            return 0;
        }
    }
}
