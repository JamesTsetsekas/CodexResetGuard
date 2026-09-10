using System;
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
    public sealed class ResetEngine
    {
        private readonly IStateStore store;
        private readonly IAccountGateway gateway;
        private readonly Func<long> clock;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public GuardState State { get; private set; }
        public Snapshot Latest { get; private set; }
        public string Status { get; private set; }
        public int Failures { get; private set; }
        public bool Busy { get; private set; }
        public bool RequestInFlight { get; private set; }
        public event Action Changed;
        public event Action<string, string> Notice;
        public ResetEngine(GuardState state, IStateStore store, IAccountGateway gateway, Func<long> clock)
        {
            State = state; this.store = store; this.gateway = gateway; this.clock = clock; Status = "Connecting to Codex…";
        }
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
        public void Arm(Rules rules)
        {
            if (State.Enabled) throw new GuardException("Automatic resets are already enabled. Pause before starting a new allowance.");
            if (Busy) throw new GuardException("A check is in progress. Enable automatic resets after it completes.");
            if (State.Pending != null) throw new GuardException("Confirm the pending reset before enabling a new allowance.");
            string error = rules.Validate(); if (error != null) throw new GuardException(error);
            if (Failures > 0 || Latest == null || !Latest.IdentityVerified || clock() - Latest.FetchedAt > 40) throw new GuardException("Refresh usage first so the account can be verified.");
            if (!Latest.DetailsKnown || !rules.AllowedCreditIds.Any(id => Latest.Credits.Any(c => c.Id == id && c.Available(clock() + 30)))) throw new GuardException("On the Reset credits tab, check at least one available credit.");
            if (!String.IsNullOrEmpty(rules.PreferredCreditId) && !rules.AllowedCreditIds.Contains(rules.PreferredCreditId)) throw new GuardException("Check the selected credit on the Reset credits tab.");
            State.Rules = rules; State.AccountKey = Latest.AccountKey; State.ArmId = Guid.NewGuid().ToString(); State.UsedThisArm = 0;
            State.ArmedUntil = rules.ArmHours == 0 ? 0 : clock() + rules.ArmHours * 3600L; State.Enabled = true;
            State.Record("Enabled", "Threshold " + Data.Percent(rules.Threshold) + "; allowance " + rules.MaximumResets + "; reserve " + rules.ReserveCredits + ".", clock());
            Save(); Status = Policy.Evaluate(State, Latest, clock(), false).Reason; Update();
        }
        public void Pause()
        {
            State.Enabled = false; State.Record("Paused", RequestInFlight ? "A sent request may still complete; all further attempts are paused." : "Automatic resets paused.", clock()); Save();
            Status = RequestInFlight ? "Pausing. The request already sent may still complete." : State.Pending != null ? "Paused. A pending reset still needs confirmation." : "Automatic resets are off."; Update();
        }
        public Decision Preview() { return Policy.Evaluate(State, Latest, clock(), true); }
        public int PollSeconds
        {
            get
            {
                if (Failures > 0) return Math.Min(300, 15 * (1 << Math.Min(Failures, 4)));
                if (State.Pending != null) return 30;
                if (State.Enabled && Latest != null && Latest.Windows.Any(w => w.Used >= State.Rules.Threshold - 5)) return 15;
                return State.Enabled ? 30 : 60;
            }
        }
        public async Task CheckAsync(bool retryPending)
        {
            if (!await gate.WaitAsync(0)) return;
            Busy = true; Update();
            try
            {
                using (IAccountSession session = await gateway.OpenAsync())
                {
                    Latest = await session.ReadAsync(); Failures = 0;
                    if (State.Enabled && (Latest.AccountKey != State.AccountKey || !Latest.IdentityVerified))
                    {
                        State.Enabled = false; State.Record("Account changed", "Automatic resets paused. Re-enable for the intended account.", clock()); Save();
                        Notify("Automatic resets paused", "The signed-in Codex account changed.");
                        Status = "The account changed. Automatic resets are paused.";
                    }
                    if (State.Enabled && State.ArmedUntil != 0 && clock() >= State.ArmedUntil)
                    {
                        State.Enabled = false; State.Record("Period ended", "The enabled period ended.", clock()); Save(); Notify("Enabled period ended", "Automatic resets are now off.");
                    }
                    if (State.Pending != null)
                    {
                        await HandlePending(session, retryPending); return;
                    }
                    var decision = Policy.Evaluate(State, Latest, clock(), false); Status = decision.Reason;
                    if (!decision.Ready) return;
                    var credit = decision.Credit;
                    State.Pending = new PendingReset { Key = Guid.NewGuid().ToString(), CreditId = credit.Id, CreditExpiresAt = credit.ExpiresAt, AccountKey = Latest.AccountKey, ArmId = State.ArmId, BeforeCount = Latest.AvailableCount.Value, BeforeWindows = Latest.Windows.ToList(), StartedAt = clock() };
                    Save(); // Durable write must succeed before any consume call.
                    await SendPending(session);
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
        private async Task HandlePending(IAccountSession session, bool manualRetry)
        {
            var p = State.Pending;
            if (Latest.AccountKey != p.AccountKey || !Latest.IdentityVerified) { Status = "Pending reset belongs to a different account. Return to that account to confirm it."; return; }
            if (p.Acknowledged)
            {
                FinishVerification(); return;
            }
            if (!manualRetry && (!State.Enabled || p.Attempts >= 3))
            {
                if (State.Enabled) { State.Enabled = false; Save(); }
                Status = "Automatic resets paused. Use ‘Retry pending request’ to reconcile the original attempt."; return;
            }
            if (!manualRetry && clock() - p.LastAttemptAt < 60) { Status = "Waiting to retry the same pending request. No other credit will be used."; return; }
            if (!manualRetry)
            {
                // A timeout does not prove that the first request reached the server.
                // Reusing its key could still spend for the first time, so current rules apply.
                var decision = Policy.EvaluatePendingRetry(State, Latest, clock());
                if (!decision.Ready)
                {
                    State.Enabled = false; State.Record("Recovery paused", decision.Reason, clock()); Save();
                    Status = "Automatic recovery paused because current rules no longer permit this reset. The original request is saved; use Retry pending request to reconcile it. " + decision.Reason;
                    Notify("Reset confirmation needs attention", "Current rules no longer permit automatic retry. Review the pending request in Activity."); return;
                }
            }
            await SendPending(session);
        }
        private async Task SendPending(IAccountSession session)
        {
            var p = State.Pending;
            if (Latest.AccountKey != p.AccountKey || !Latest.IdentityVerified || clock() - Latest.FetchedAt > 40) throw new GuardException("Fresh account verification is required before retrying.");
            p.Attempts++; p.LastAttemptAt = clock(); State.LastAttemptAt = clock(); Save();
            RequestInFlight = true; Status = "Requesting the selected reset…"; Update();
            string outcome = await session.ConsumeAsync(p); RequestInFlight = false;
            if (outcome == "reset" || outcome == "alreadyRedeemed")
            {
                p.Acknowledged = true; State.UsedThisArm++; State.LastSuccessAt = clock();
                State.Record("Reset acknowledged", "Waiting for fresh usage and credit confirmation. Expiry: " + Data.LocalTime(p.CreditExpiresAt) + ".", clock());
                if (State.UsedThisArm >= State.Rules.MaximumResets) State.Enabled = false;
                Save();
                Latest = await session.ReadAsync(); FinishVerification();
            }
            else if (outcome == "noCredit" || outcome == "nothingToReset")
            {
                State.Pending = null; State.Record("No reset used", outcome == "noCredit" ? "Codex reported no eligible credit." : "Codex reported no eligible usage window.", clock());
                Save(); Status = "Codex did not apply a reset: " + outcome + ".";
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
            if (!Policy.Verified(p, Latest)) { Status = "Codex acknowledged the reset. Waiting for refreshed usage; no additional credit will be used."; return; }
            State.Pending = null; State.Record("Reset verified", "Usage decreased and the selected credit is no longer available.", clock()); Save();
            Status = State.Enabled ? "Reset verified. Continuing to watch your rules." : "Reset verified. Automatic resets are off.";
            Notify("Codex reset verified", State.Enabled ? "Usage is refreshed. Your remaining allowance is still enabled." : "Usage is refreshed. Enable again to authorize more resets.");
        }
    }
}
