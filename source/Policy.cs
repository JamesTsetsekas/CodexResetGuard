using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexResetGuard
{
    public sealed class Decision
    {
        public ResetCredit Credit;
        public string Reason;
        // True when the rules can never fire as configured (shown as a warning, not a wait).
        public bool Blocked;
        // True when the selected timing mode is deliberately holding the reset back.
        public bool Held;
        public bool Ready { get { return Credit != null; } }
        public static Decision Wait(string reason) { return new Decision { Reason = reason }; }
        public static Decision Block(string reason) { return new Decision { Reason = reason, Blocked = true }; }
    }
    // Runtime signals that only some trigger modes use. Everything here is optional.
    public sealed class TriggerContext
    {
        // Seconds since any Codex session log was written; null when unknown.
        public long? CodexIdleSeconds;
        // Per window length (minutes): when 100% was first observed in the current window.
        public Dictionary<int, long> FirstAtLimit = new Dictionary<int, long>();
        // Per window length (minutes): recent (time, used) samples in the current window.
        public Dictionary<int, List<KeyValuePair<long, double>>> Samples = new Dictionary<int, List<KeyValuePair<long, double>>>();
        public const int LeadSeconds = 75;
        public const int IdleSeconds = 20;
        // Projected seconds until the window reaches 100%, from the recent rate of increase.
        public double? SecondsToLimit(UsageWindow w, long now)
        {
            List<KeyValuePair<long, double>> list;
            if (!Samples.TryGetValue(w.Minutes, out list) || list.Count < 2) return null;
            var recent = list.Where(p => now - p.Key <= 900).ToList(); if (recent.Count < 2) return null;
            var first = recent[0]; var last = recent[recent.Count - 1];
            double span = last.Key - first.Key, rise = last.Value - first.Value;
            if (span < 30 || rise <= 0) return null;
            return (100 - w.Used) / (rise / span);
        }
    }
    public static class Policy
    {
        public const int CredentialFreshSeconds = 40;
        public static Decision Evaluate(GuardState state, Snapshot s, long now, bool preview, TriggerContext context = null)
        {
            return EvaluateCore(state, s, now, preview, null, context);
        }
        public static Decision EvaluatePendingRetry(GuardState state, Snapshot s, long now, TriggerContext context = null)
        {
            if (state.Pending == null) return Decision.Wait("No reset request is pending.");
            return EvaluateCore(state, s, now, false, state.Pending, context);
        }
        public static int AttemptCooldown(int noOpStreak)
        {
            return noOpStreak <= 0 ? 120 : noOpStreak == 1 ? 300 : noOpStreak == 2 ? 900 : 1800;
        }
        public static IEnumerable<UsageWindow> Watched(Rules r, Snapshot s)
        {
            return s.Windows.Where(w => (r.Window == "weekly" && w.Minutes == 10080) || (r.Window == "fiveHour" && w.Minutes == 300) || (r.Window == "either" && (w.Minutes == 300 || w.Minutes == 10080)));
        }
        private static Decision EvaluateCore(GuardState state, Snapshot s, long now, bool preview, PendingReset retry, TriggerContext context)
        {
            if (state.Pending != null && retry == null) return Decision.Wait("A previous reset is awaiting confirmation. No additional credits will be used.");
            if (!preview && !state.Enabled) return Decision.Wait("Automatic resets are off.");
            var r = state.Rules; string problem = r == null ? "Rules are missing." : r.Validate();
            if (problem != null) return Decision.Block(problem);
            if (s == null || now - s.FetchedAt > CredentialFreshSeconds || s.FetchedAt > now + 5) return Decision.Wait("Waiting for a fresh usage check.");
            if (!s.IdentityVerified || String.IsNullOrWhiteSpace(s.AccountKey)) return Decision.Block("Codex account identity could not be verified. Automatic resets are unavailable.");
            if (!preview && state.AccountKey != s.AccountKey) return Decision.Block("The Codex account changed. Enable automatic resets again for this account.");
            if (!preview && state.ArmedUntil != 0 && now >= state.ArmedUntil) return Decision.Wait("The enabled period has ended.");
            if (!preview && state.UsedThisArm >= r.MaximumResets) return Decision.Wait("The reset allowance for this enabled period has been used.");
            if (retry == null && state.LastAttemptAt > 0 && now - state.LastAttemptAt < AttemptCooldown(state.NoOpStreak))
                return Decision.Wait(state.NoOpStreak > 0 ? "Codex declined the last reset. Waiting before asking again." : "Waiting two minutes after the last reset attempt.");
            if (state.LastSuccessAt > 0 && now - state.LastSuccessAt < 600) return Decision.Wait("Waiting ten minutes after the last successful reset.");
            if (!s.AvailableCount.HasValue || !s.DetailsKnown) return Decision.Wait("Waiting for individual reset-credit details.");
            if (retry == null && state.RefreshBarrier != null && !Refreshed(state.RefreshBarrier, s)) return Decision.Wait("Waiting for Codex to report refreshed usage after the last reset. No other credit will be used until then.");
            if (s.AvailableCount.Value <= r.ReserveCredits) return Decision.Block("You have " + s.AvailableCount.Value + " credit(s) and keep " + r.ReserveCredits + " in reserve, so none can be used. Lower the reserve to allow resets.");
            var windows = Watched(r, s).ToList();
            if (windows.Count == 0) return Decision.Block("Codex does not report the selected usage window. Choose another window.");
            var high = new List<UsageWindow>(); string waiting = null; bool held = false;
            foreach (var w in windows)
            {
                if (w.Used < r.Threshold) continue;
                if (!w.ResetsAt.HasValue || w.ResetsAt <= now + 30) { waiting = "The usage window is resetting now, or its reset time is unknown."; continue; }
                if (r.SkipNearResetMinutes > 0 && w.ResetsAt.Value - now <= r.SkipNearResetMinutes * 60L) { waiting = "The " + w.Name.ToLowerInvariant() + " window resets on its own " + UsageSummary.ShortDate(w.ResetsAt.Value) + ". Saving your credit."; continue; }
                string hold = TriggerHold(r, w, now, context);
                if (hold != null) { waiting = hold; held = true; continue; } // Automatic retries honour the timing mode too.
                high.Add(w);
            }
            if (high.Count == 0)
                return new Decision { Held = held, Reason = waiting ?? ("Watching for " + Data.Percent(r.Threshold) + " used" + (r.Trigger == "limit" ? ", then the limit" : "") + ". Current highest: " + Data.Percent(windows.Max(w => w.Used)) + ".") };
            var credits = s.Credits.Where(c => c.Available(now + 30) && r.AllowedCreditIds.Contains(c.Id));
            if (retry != null) credits = credits.Where(c => c.Id == retry.CreditId);
            if (!String.IsNullOrWhiteSpace(r.PreferredCreditId)) credits = credits.Where(c => c.Id == r.PreferredCreditId);
            var selected = credits.OrderBy(c => c.ExpiresAt ?? Int64.MaxValue).ThenBy(c => c.GrantedAt).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
            if (selected == null) return !String.IsNullOrWhiteSpace(r.PreferredCreditId) && !s.Credits.Any(c => c.Id == r.PreferredCreditId && c.Available(now + 30))
                ? Decision.Block("The specific credit chosen in Credit order is no longer available. Choose another on Automation.")
                : Decision.Wait("No checked reset is currently available. Newly granted credits need to be selected on Credits first.");
            return new Decision { Credit = selected, Reason = "Ready to use the reset " + (selected.ExpiresAt.HasValue ? "expiring " + Data.LocalTime(selected.ExpiresAt) : "with no reported expiry") + "." };
        }
        // Returns a wait reason while the selected trigger mode is holding the reset back.
        private static string TriggerHold(Rules r, UsageWindow w, long now, TriggerContext context)
        {
            if (r.Trigger == "predictive")
            {
                if (w.Used >= 99) return null;
                var eta = context == null ? null : context.SecondsToLimit(w, now);
                if (eta.HasValue && eta.Value <= TriggerContext.LeadSeconds) return null;
                return "Using the last few percent: " + Data.Percent(w.Used) + " used, " + (eta.HasValue ? "about " + Math.Max(1, (int)(eta.Value / 60)) + " min to the limit" : "rising slowly") + ". Resets before Codex would block.";
            }
            if (r.Trigger == "limit")
            {
                if (w.Used < 100) return "Waiting for the " + w.Name.ToLowerInvariant() + " limit (now " + Data.Percent(w.Used) + "). Running turns may finish past 100%.";
                long first; if (context == null || !context.FirstAtLimit.TryGetValue(w.Minutes, out first)) first = now;
                long waited = now - first, grace = r.GraceMinutes * 60L;
                if (waited >= grace) return null;
                if (context != null && context.CodexIdleSeconds.HasValue && context.CodexIdleSeconds.Value >= TriggerContext.IdleSeconds) return null;
                return "At the limit. Letting the running Codex turn finish (up to " + Math.Max(0, (grace - waited + 59) / 60) + " more min) before resetting.";
            }
            return null;
        }
        public static bool Verified(PendingReset p, Snapshot s)
        {
            if (!p.Acknowledged || s.AccountKey != p.AccountKey || !s.DetailsKnown || !s.AvailableCount.HasValue) return false;
            bool gone = !s.Credits.Any(c => c.Id == p.CreditId && c.Status == "available");
            return gone && p.BeforeWindows != null && Refreshed(p.BeforeWindows, s);
        }
        // Codex acknowledged this exact request and its credit is gone, but usage has not visibly refreshed.
        // The redemption is accounted for; spending again stays blocked by GuardState.RefreshBarrier.
        public static bool ConsumedWithoutRefresh(PendingReset p, Snapshot s)
        {
            if (!p.Acknowledged || s.AccountKey != p.AccountKey || !s.DetailsKnown || !s.CreditsComplete || !s.AvailableCount.HasValue) return false;
            return !s.Credits.Any(c => c.Id == p.CreditId && c.Status == "available") && p.BeforeCount > 0 && s.AvailableCount.Value < p.BeforeCount;
        }
        // Usage refreshed: a window reports at least one point less, or a later reset time.
        public static bool Refreshed(List<UsageWindow> before, Snapshot s)
        {
            return before.Any(b => s.Windows.Any(a => a.Minutes == b.Minutes && (a.Used <= b.Used - 1 || (a.ResetsAt.HasValue && b.ResetsAt.HasValue && a.ResetsAt.Value > b.ResetsAt.Value + 60))));
        }
        // A pending credit that is no longer available can be retried with its original key without any new spending.
        public static bool CreditGone(PendingReset p, Snapshot s)
        {
            return s != null && s.DetailsKnown && s.CreditsComplete && s.AccountKey == p.AccountKey && !s.Credits.Any(c => c.Id == p.CreditId && c.Status == "available");
        }
    }
}
