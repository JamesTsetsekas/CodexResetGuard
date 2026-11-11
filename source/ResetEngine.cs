using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexResetGuard
{
    public interface IAccountSession : IDisposable
    {
        Task<Snapshot> ReadAsync();
        Task<string> ConsumeAsync(PendingReset pending);
    }
    public interface IAccountGateway { Task<IAccountSession> OpenAsync(); }
    internal static class CodexActivity
    {
        // Seconds since the newest Codex session log was written. Reads file times only, never contents.
        public static long? SecondsIdle(long now)
        {
            try
            {
                string root = Path.Combine(AuthIdentity.CodexHome, "sessions"); if (!Directory.Exists(root)) return null;
                DateTime newest = DateTime.MinValue;
                for (int back = 0; back < 14; back++)
                {
                    var day = DateTime.Now.AddDays(-back);
                    string dir = Path.Combine(root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
                    if (!Directory.Exists(dir)) continue;
                    foreach (string file in Directory.EnumerateFiles(dir, "rollout-*.jsonl")) { var t = File.GetLastWriteTimeUtc(file); if (t > newest) newest = t; }
                }
                if (newest == DateTime.MinValue) return null;
                return Math.Max(0, now - (long)(newest - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            }
            catch { return null; }
        }
    }
    public sealed class ResetEngine
    {
        private readonly IStateStore store;
        private readonly IAccountGateway gateway;
        private readonly Func<long> clock;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private int authorization; // Incremented by Pause; a check started earlier may not send afterwards.
        private readonly TriggerContext trend = new TriggerContext();
        private readonly Dictionary<int, long?> trendAnchor = new Dictionary<int, long?>();
        public Func<long, long?> IdleProbe = CodexActivity.SecondsIdle;
        public GuardState State { get; private set; }
        public Snapshot Latest { get; private set; }
        public string Status { get; private set; }
        public Decision LastDecision { get; private set; }
        public int Failures { get; private set; }
        public bool Busy { get; private set; }
        public bool RequestInFlight { get; private set; }
        public event Action Changed;
        public event Action<string, string> Notice;
        public ResetEngine(GuardState state, IStateStore store, IAccountGateway gateway, Func<long> clock)
        {
            State = state; this.store = store; this.gateway = gateway; this.clock = clock; Status = "Connecting to Codex…";
        }
        internal TriggerContext Trend { get { return trend; } }
        private void Update() { if (Changed != null) Changed(); }
        private void Notify(string title, string message) { if (Notice != null) Notice(title, message); }
        private void Save()
        {
            try { store.Save(State); }
            catch { State.Enabled = false; throw; }
        }
        public void SaveRules(Rules rules)
        {
            string error = rules.Validate(); if (error != null) throw new GuardException(error);
            if (State.Enabled) throw new GuardException("Pause automatic resets before changing the rules.");
            if (State.Pending != null) throw new GuardException("Confirm the pending reset before changing rules.");
            State.Rules = rules; Save(); Update();
        }
        public void SaveSettings() { store.Save(State); Update(); }
        public void Arm(Rules rules)
        {
            if (State.Enabled) throw new GuardException("Automatic resets are already enabled. Pause before starting a new allowance.");
            if (Busy) throw new GuardException("A check is in progress. Enable automatic resets after it completes.");
            if (State.Pending != null) throw new GuardException("Confirm the pending reset before enabling a new allowance.");
            string error = rules.Validate(); if (error != null) throw new GuardException(error);
            if (Failures > 0 || Latest == null || !Latest.IdentityVerified || clock() - Latest.FetchedAt > Policy.CredentialFreshSeconds) throw new GuardException("Refresh usage first so the account can be verified.");
            if (!Latest.DetailsKnown || !rules.AllowedCreditIds.Any(id => Latest.Credits.Any(c => c.Id == id && c.Available(clock() + 30)))) throw new GuardException("On the Credits tab, check at least one available credit.");
            if (!String.IsNullOrEmpty(rules.PreferredCreditId) && !rules.AllowedCreditIds.Contains(rules.PreferredCreditId)) throw new GuardException("The credit chosen in Credit order must also be checked on the Credits tab.");
            if (Latest.AvailableCount.HasValue && Latest.AvailableCount.Value <= rules.ReserveCredits) throw new GuardException("You have " + Latest.AvailableCount.Value + " credit(s) and keep " + rules.ReserveCredits + " in reserve, so no reset could ever be used. Lower the reserve first.");
            if (!Policy.Watched(rules, Latest).Any()) throw new GuardException("Codex does not report the selected usage window for this account. Choose another window.");
            State.Rules = rules; State.AccountKey = Latest.AccountKey; State.ArmId = Guid.NewGuid().ToString(); State.UsedThisArm = 0; State.NoOpStreak = 0;
            State.RefreshBarrier = null; // Enabling again is the person's explicit go-ahead.
            State.ArmedUntil = rules.ArmHours == 0 ? 0 : clock() + rules.ArmHours * 3600L; State.Enabled = true;
            State.Record("Enabled", "Threshold " + Data.Percent(rules.Threshold) + " (" + TriggerName(rules.Trigger) + "); allowance " + rules.MaximumResets + "; reserve " + rules.ReserveCredits + ".", clock());
            Save(); LastDecision = Policy.Evaluate(State, Latest, clock(), false, trend); Status = LastDecision.Reason; Update();
        }
        public static string TriggerName(string trigger) { return trigger == "predictive" ? "use the last few percent" : trigger == "limit" ? "at the limit" : "at the threshold"; }
        public void Pause()
        {
            Interlocked.Increment(ref authorization);
            State.Enabled = false; State.Record("Paused", RequestInFlight ? "A sent request may still complete; all further attempts are paused." : "Automatic resets paused.", clock()); Save();
            Status = RequestInFlight ? "Pausing. The request already sent may still complete." : State.Pending != null ? "Paused. A pending reset still needs confirmation." : "Automatic resets are off."; Update();
        }
        // Closes an acknowledged reset whose usage refresh was never observed. The reset stays counted and no credit is spent.
        public void CloseAcknowledged()
        {
            var p = State.Pending;
            if (p == null || !p.Acknowledged) throw new GuardException("Only a reset Codex already accepted can be closed this way.");
            if (Busy) throw new GuardException("A check is in progress. Try again after it completes.");
            State.RefreshBarrier = p.BeforeWindows; State.Pending = null; State.Record("Reset closed", "Codex accepted the reset, but refreshed usage was not observed. It remains counted; no credit was spent by closing it. Another reset waits for refreshed usage or a new enabled period.", clock()); Save();
            Status = State.Enabled ? "Pending reset closed. Continuing to watch your rules." : "Pending reset closed. Automatic resets are off."; Update();
        }
        public Decision Preview() { return Policy.Evaluate(State, Latest, clock(), true, trend); }
        public int PollSeconds
        {
            get
            {
                if (Failures > 0) return Math.Min(300, 15 * (1 << Math.Min(Failures, 5)));
                if (State.Pending != null) return 30;
                if (State.Enabled && Latest != null)
                {
                    var watched = Policy.Watched(State.Rules, Latest).ToList();
                    if (State.Rules.Trigger != "threshold" && watched.Any(w => w.Used >= Math.Max(State.Rules.Threshold, 97))) return 10;
                    if (watched.Any(w => w.Used >= State.Rules.Threshold - 5)) return 15;
                }
                return State.Enabled ? 30 : 60;
            }
        }
        private void Observe(Snapshot s, long now)
        {
            foreach (var w in s.Windows)
            {
                long? anchor; trendAnchor.TryGetValue(w.Minutes, out anchor);
                List<KeyValuePair<long, double>> list;
                bool sameWindow = anchor.HasValue && w.ResetsAt.HasValue ? Math.Abs(anchor.Value - w.ResetsAt.Value) <= 120 : anchor == w.ResetsAt; // Reported reset times jitter slightly.
                if (!trend.Samples.TryGetValue(w.Minutes, out list) || !sameWindow || (list.Count > 0 && w.Used < list[list.Count - 1].Value - 2))
                {
                    list = new List<KeyValuePair<long, double>>(); trend.Samples[w.Minutes] = list; trend.FirstAtLimit.Remove(w.Minutes); trendAnchor[w.Minutes] = w.ResetsAt;
                }
                list.Add(new KeyValuePair<long, double>(now, w.Used)); list.RemoveAll(p => now - p.Key > 1800);
                if (w.Used >= 100) { if (!trend.FirstAtLimit.ContainsKey(w.Minutes)) trend.FirstAtLimit[w.Minutes] = now; }
                else trend.FirstAtLimit.Remove(w.Minutes);
            }
            trend.CodexIdleSeconds = State.Rules != null && State.Rules.Trigger == "limit" && IdleProbe != null ? IdleProbe(now) : null;
        }
        public async Task CheckAsync(bool retryPending)
        {
            if (!await gate.WaitAsync(0)) return;
            int generation = Volatile.Read(ref authorization);
            Busy = true; Update();
            try
            {
                using (IAccountSession session = await gateway.OpenAsync())
                {
                    Latest = await session.ReadAsync(); Failures = 0; Observe(Latest, clock());
                    if (State.RefreshBarrier != null && Latest.AccountKey == State.AccountKey && Policy.Refreshed(State.RefreshBarrier, Latest)) { State.RefreshBarrier = null; Save(); }
                    if (State.Enabled && (Latest.AccountKey != State.AccountKey || !Latest.IdentityVerified))
                    {
                        State.Enabled = false; State.Record("Account changed", "Automatic resets paused. Re-enable for the intended account.", clock()); Save();
                        Notify("Automatic resets paused", "The signed-in Codex account changed.");
                        Status = "The account changed. Automatic resets are paused."; return;
                    }
                    if (State.Enabled && State.ArmedUntil != 0 && clock() >= State.ArmedUntil)
                    {
                        State.Enabled = false; State.Record("Period ended", "The enabled period ended.", clock()); Save(); Notify("Enabled period ended", "Automatic resets are now off.");
                    }
                    if (State.Pending != null)
                    {
                        await HandlePending(session, retryPending, generation); return;
                    }
                    var decision = Policy.Evaluate(State, Latest, clock(), false, trend); LastDecision = decision; Status = decision.Reason;
                    if (!decision.Ready) return;
                    var credit = decision.Credit;
                    State.Pending = new PendingReset { Key = Guid.NewGuid().ToString(), CreditId = credit.Id, CreditExpiresAt = credit.ExpiresAt, AccountKey = Latest.AccountKey, ArmId = State.ArmId, BeforeCount = Latest.AvailableCount.Value, BeforeWindows = Latest.Windows.ToList(), StartedAt = clock() };
                    try { Save(); } // Durable write must succeed before any consume call.
                    catch { State.Pending = null; throw; } // Nothing was sent, so nothing needs reconciling.
                    await SendPending(session, generation);
                }
            }
            catch (Exception ex)
            {
                Failures++; Status = ex is GuardException ? ex.Message : "Codex could not be reached. No additional reset request will be sent until a fresh check succeeds.";
                if (State.Pending != null) Status = "Reset confirmation is pending. Only the original request can be retried. " + Status;
                if (Failures == 1 && (State.Enabled || State.Pending != null)) Notify("Reset Guard needs attention", Status);
            }
            finally { Busy = false; RequestInFlight = false; gate.Release(); Update(); }
        }
        private async Task HandlePending(IAccountSession session, bool manualRetry, int generation)
        {
            var p = State.Pending;
            if (Latest.AccountKey != p.AccountKey || !Latest.IdentityVerified) { Status = "Pending reset belongs to a different account. Return to that account to confirm it."; return; }
            if (p.Acknowledged)
            {
                FinishVerification(); return;
            }
            // If the saved credit is already gone, resending the original key cannot spend anything new:
            // Codex either reports it as ours (alreadyRedeemed) or reports no credit. This recovers lost replies.
            bool safe = Policy.CreditGone(p, Latest);
            if (!manualRetry && (!State.Enabled || p.Attempts >= (safe ? 6 : 3)))
            {
                if (State.Enabled) { State.Enabled = false; State.Record("Recovery paused", "Three attempts did not confirm the reset.", clock()); Save(); }
                Status = "Automatic resets paused. Use ‘Reconcile pending reset’ on Activity to finish the original attempt."; return;
            }
            if (!manualRetry && clock() - p.LastAttemptAt < 60) { Status = "Waiting to retry the same pending request. No other credit will be used."; return; }
            if (!manualRetry && !safe)
            {
                // A timeout does not prove that the first request reached the server.
                // Reusing its key could still spend for the first time, so current rules apply.
                var decision = Policy.EvaluatePendingRetry(State, Latest, clock(), trend);
                if (!decision.Ready && decision.Held) { Status = "Pending retry is waiting for your timing rule. " + decision.Reason; return; }
                if (!decision.Ready)
                {
                    State.Enabled = false; State.Record("Recovery paused", decision.Reason, clock()); Save();
                    Status = "Automatic recovery paused because current rules no longer permit this reset. The original request is saved; use Reconcile pending reset on Activity. " + decision.Reason;
                    Notify("Reset confirmation needs attention", "Current rules no longer permit automatic retry. Review the pending request in Activity."); return;
                }
            }
            await SendPending(session, generation, safe);
        }
        private async Task SendPending(IAccountSession session, int generation, bool creditGone = false)
        {
            var p = State.Pending;
            if (Latest.AccountKey != p.AccountKey || !Latest.IdentityVerified || clock() - Latest.FetchedAt > Policy.CredentialFreshSeconds) throw new GuardException("Fresh account verification is required before retrying.");
            if (generation != Volatile.Read(ref authorization)) { Status = "Paused before the request was sent. Nothing was sent."; return; }
            p.Attempts++; p.LastAttemptAt = clock(); State.LastAttemptAt = clock(); Save();
            RequestInFlight = true; Status = "Requesting the selected reset…"; Update();
            string outcome = await session.ConsumeAsync(p); RequestInFlight = false;
            if (outcome == "reset" || outcome == "alreadyRedeemed")
            {
                p.Acknowledged = true; State.UsedThisArm++; State.LastSuccessAt = clock(); State.NoOpStreak = 0;
                if (State.Rules.PreferredCreditId == p.CreditId) State.Rules.PreferredCreditId = null; // A spent specific credit cannot be chosen again.
                State.Rules.AllowedCreditIds.Remove(p.CreditId);
                State.Record("Reset acknowledged", "Waiting for fresh usage and credit confirmation. Expiry: " + Data.LocalTime(p.CreditExpiresAt) + ".", clock());
                if (State.UsedThisArm >= State.Rules.MaximumResets) State.Enabled = false;
                Save();
                Latest = await session.ReadAsync(); Observe(Latest, clock()); FinishVerification();
            }
            else if ((outcome == "noCredit" || outcome == "nothingToReset") && (creditGone || p.Attempts > 1))
            {
                // The saved credit is gone or an earlier attempt may have reached Codex, so this reset was most likely
                // applied by an earlier attempt whose reply was lost. Count it, and wait for refreshed usage before another.
                State.Pending = null; State.UsedThisArm++; State.LastSuccessAt = clock(); State.RefreshBarrier = p.BeforeWindows;
                State.Rules.AllowedCreditIds.Remove(p.CreditId); if (State.Rules.PreferredCreditId == p.CreditId) State.Rules.PreferredCreditId = null;
                if (State.UsedThisArm >= State.Rules.MaximumResets) State.Enabled = false;
                State.Record("Reset counted", "Codex reported " + outcome + " for a request that was probably applied earlier. It is counted against the allowance; another reset waits for refreshed usage.", clock());
                Save(); Status = "The earlier reset was probably applied. It is counted, and no other credit will be used until usage refreshes.";
            }
            else if (outcome == "noCredit" || outcome == "nothingToReset")
            {
                State.Pending = null; State.NoOpStreak++;
                string detail = outcome == "noCredit" ? "Codex reported no eligible credit." : "Codex reported no eligible usage window.";
                if (State.NoOpStreak == 1) State.Record("No reset used", detail, clock());
                else if (State.History.Count > 0 && State.History[0].Event.StartsWith("No reset used")) { State.History[0].Event = "No reset used ×" + State.NoOpStreak; State.History[0].At = clock(); }
                if (State.NoOpStreak >= 5 && State.Enabled) { State.Enabled = false; State.Record("Paused", "Codex declined five reset requests in a row. Automatic resets are paused.", clock()); Notify("Automatic resets paused", "Codex declined five reset requests in a row."); }
                Save(); Status = "Codex did not apply a reset: " + outcome + ". Next attempt in " + Policy.AttemptCooldown(State.NoOpStreak) / 60 + " min.";
            }
            else
            {
                State.Enabled = false; Save();
                throw new GuardException("The reset service returned an unrecognized outcome. Automatic resets are paused.");
            }
        }
        private void FinishVerification()
        {
            var p = State.Pending;
            if (!Policy.Verified(p, Latest))
            {
                if (Policy.ConsumedWithoutRefresh(p, Latest))
                {
                    State.RefreshBarrier = p.BeforeWindows; State.Pending = null;
                    State.Record("Reset confirmed", "The selected credit was used. Another reset waits until Codex reports refreshed usage.", clock()); Save();
                    Status = "Reset confirmed by your credit count. Waiting for refreshed usage before any other credit can be used."; return;
                }
                Status = "Codex acknowledged the reset. Waiting for refreshed usage; no additional credit will be used."; return;
            }
            State.Pending = null; State.Record("Reset verified", "Usage refreshed and the selected credit is no longer available.", clock()); Save();
            Status = State.Enabled ? "Reset verified. Continuing to watch your rules." : "Reset verified. Automatic resets are off.";
            Notify("Codex reset verified", State.Enabled ? "Usage is refreshed. Your remaining allowance is still enabled." : "Usage is refreshed. Enable again to authorize more resets.");
        }
    }
}
