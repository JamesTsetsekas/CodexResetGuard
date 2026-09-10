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
                try { return StubServer(); }
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
            var s = new Snapshot { AccountKey = "test-account", AccountDisplay = "test@example.invalid", IdentityVerified = true, Plan = "pro", FetchedAt = now, AvailableCount = 3, DetailsKnown = true };
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
            data = Data.Parse("{\"rateLimitResetCredits\":{\"availableCount\":3,\"credits\":[{\"id\":\"x\",\"resetType\":\"codexRateLimits\",\"status\":\"available\",\"expiresAt\":\"invalid\"}]}}");
            Check(Snapshot.Parse(data, now).Credits.Count == 0, "Malformed expiry is never treated as a never-expiring credit");
            EngineTests(); StorageTests(); TransportTests();
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
            public List<string> Keys = new List<string>();
            public List<string> Credits = new List<string>();
            public FakeGateway() { Snapshot = Sample(Now, 95); }
            public Task<IAccountSession> OpenAsync() { return Task.FromResult<IAccountSession>(this); }
            public Task<Snapshot> ReadAsync() { var s = Copy(Snapshot); s.FetchedAt = Now; return Task.FromResult(s); }
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
            Check(gateway.Keys.Count == 1 && !engine.State.Enabled && engine.State.Pending != null, "Changed conditions after a possibly committed request pause automatic recovery without discarding its identity");
            engine.CheckAsync(true).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 2 && gateway.Keys.All(k => k == key) && gateway.Credits.All(c => c == "early"), "Restart recovery retries only the original idempotency key and credit");
            Check(engine.State.UsedThisArm == 1 && engine.State.Pending == null, "alreadyRedeemed charges the allowance once and verifies");
            gateway = new FakeGateway { ApplyBeforeTimeout = true }; store = new MemoryStore(); engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); engine.Pause(); gateway.Now += 61; engine.CheckAsync(false).GetAwaiter().GetResult();
            Check(gateway.Keys.Count == 1 && engine.State.Pending != null, "Pause blocks even automatic retries of an ambiguous request");
            engine.CheckAsync(true).GetAwaiter().GetResult(); Check(gateway.Keys.Count == 2 && engine.State.Pending == null, "Explicit pending retry reconciles without arming more credits");
            gateway = new FakeGateway(); store = new MemoryStore { Fail = true }; engine = new ResetEngine(Armed(gateway.Now), store, gateway, () => gateway.Now);
            engine.CheckAsync(false).GetAwaiter().GetResult(); Check(gateway.Keys.Count == 0 && !engine.State.Enabled, "Journal write failure prevents any consume request");
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
        private static int StubServer()
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
