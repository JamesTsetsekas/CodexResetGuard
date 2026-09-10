using System;
using System.Linq;

namespace CodexResetGuard
{
    public sealed class Decision
    {
        public ResetCredit Credit;
        public string Reason;
        public bool Ready { get { return Credit != null; } }
        public static Decision Wait(string reason) { return new Decision { Reason = reason }; }
    }
    public static class Policy
    {
        public static Decision Evaluate(GuardState state, Snapshot s, long now, bool preview)
        {
            return EvaluateCore(state, s, now, preview, null);
        }
        public static Decision EvaluatePendingRetry(GuardState state, Snapshot s, long now)
        {
            if (state.Pending == null) return Decision.Wait("No reset request is pending.");
            return EvaluateCore(state, s, now, false, state.Pending);
        }
        private static Decision EvaluateCore(GuardState state, Snapshot s, long now, bool preview, PendingReset retry)
        {
            if (state.Pending != null && retry == null) return Decision.Wait("A previous reset is awaiting confirmation. No additional credits will be used.");
            if (!preview && !state.Enabled) return Decision.Wait("Automatic resets are off.");
            var r = state.Rules; string problem = r == null ? "Rules are missing." : r.Validate();
            if (problem != null) return Decision.Wait(problem);
            if (s == null || now - s.FetchedAt > 40 || s.FetchedAt > now + 5) return Decision.Wait("Waiting for a fresh usage check.");
            if (!s.IdentityVerified || String.IsNullOrWhiteSpace(s.AccountKey)) return Decision.Wait("Codex account identity could not be verified. Automatic resets are unavailable.");
            if (!preview && state.AccountKey != s.AccountKey) return Decision.Wait("The Codex account changed. Enable automatic resets again for this account.");
            if (!preview && state.ArmedUntil != 0 && now >= state.ArmedUntil) return Decision.Wait("The enabled period has ended.");
            if (!preview && state.UsedThisArm >= r.MaximumResets) return Decision.Wait("The reset allowance for this enabled period has been used.");
            if (retry == null && state.LastAttemptAt > 0 && now - state.LastAttemptAt < 120) return Decision.Wait("Waiting two minutes after the last reset attempt.");
            if (state.LastSuccessAt > 0 && now - state.LastSuccessAt < 600) return Decision.Wait("Waiting ten minutes after the last successful reset.");
            if (!s.AvailableCount.HasValue || !s.DetailsKnown) return Decision.Wait("Waiting for individual reset-credit details.");
            if (s.AvailableCount.Value <= r.ReserveCredits) return Decision.Wait("Keeping your reserved reset credits.");
            var windows = s.Windows.Where(w => (r.Window == "weekly" && w.Minutes == 10080) || (r.Window == "fiveHour" && w.Minutes == 300) || (r.Window == "either" && (w.Minutes == 300 || w.Minutes == 10080))).ToList();
            if (windows.Count == 0) return Decision.Wait("The selected usage window was not reported by Codex.");
            var high = windows.Where(w => w.Used >= r.Threshold && w.ResetsAt.HasValue && w.ResetsAt > now + 30).ToList();
            if (high.Count == 0)
            {
                if (windows.Any(w => w.Used >= r.Threshold)) return Decision.Wait("The usage window is resetting now, or its reset time is unknown.");
                return Decision.Wait("Watching for " + Data.Percent(r.Threshold) + " used. Current highest: " + Data.Percent(windows.Max(w => w.Used)) + ".");
            }
            var credits = s.Credits.Where(c => c.Available(now + 30) && r.AllowedCreditIds.Contains(c.Id));
            if (retry != null) credits = credits.Where(c => c.Id == retry.CreditId);
            if (!String.IsNullOrWhiteSpace(r.PreferredCreditId)) credits = credits.Where(c => c.Id == r.PreferredCreditId);
            var selected = credits.OrderBy(c => c.ExpiresAt ?? Int64.MaxValue).ThenBy(c => c.GrantedAt).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
            if (selected == null) return Decision.Wait("No checked reset is currently available. Newly granted credits need to be selected first.");
            return new Decision { Credit = selected, Reason = "Ready to use the reset " + (selected.ExpiresAt.HasValue ? "expiring " + Data.LocalTime(selected.ExpiresAt) : "with no reported expiry") + "." };
        }
        public static bool Verified(PendingReset p, Snapshot s)
        {
            if (!p.Acknowledged || s.AccountKey != p.AccountKey || !s.DetailsKnown || !s.AvailableCount.HasValue) return false;
            bool gone = !s.Credits.Any(c => c.Id == p.CreditId && c.Status == "available");
            bool decreased = p.BeforeWindows != null && p.BeforeWindows.Any(before => s.Windows.Any(after => after.Minutes == before.Minutes && after.Used <= before.Used - 1));
            return gone && decreased;
        }
    }
}
