using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexResetGuard
{
    public static class Data
    {
        public static long Now { get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; } }
        public static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static object Get(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v : null; }
        public static string Text(Dictionary<string, object> d, string k) { return Get(d, k) as string; }
        public static double? Number(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k); double n;
            if (v == null || v is bool || !Double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n) || Double.IsNaN(n) || Double.IsInfinity(n)) return null;
            return n;
        }
        public static long? Integer(Dictionary<string, object> d, string k)
        {
            double? n = Number(d, k);
            return n.HasValue && n.Value >= 0 && n.Value <= 9007199254740991L && n.Value == Math.Truncate(n.Value) ? (long?)n.Value : null;
        }
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 48 }; }
        public static string Json(object value) { return Serializer().Serialize(value); }
        public static Dictionary<string, object> Parse(string text) { return Map(Serializer().DeserializeObject(text)); }
        public static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        public static string LocalTime(long? value)
        {
            if (!value.HasValue) return "Not reported";
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(value.Value).ToLocalTime().ToString("MMM d, yyyy  h:mm tt"); }
            catch (ArgumentOutOfRangeException) { return "Not reported"; }
        }
        public static string Percent(double? n) { return n.HasValue ? n.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%" : "Unavailable"; }
    }

    public sealed class UsageWindow
    {
        public int Minutes { get; set; }
        public double Used { get; set; }
        public long? ResetsAt { get; set; }
        public string Name { get { return Minutes == 10080 ? "Weekly" : Minutes == 300 ? "5-hour" : Minutes + "-minute"; } }
    }
    public sealed class ResetCredit
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }
        public string Title { get; set; }
        public long GrantedAt { get; set; }
        public long? ExpiresAt { get; set; }
        public bool Available(long now) { return !String.IsNullOrWhiteSpace(Id) && Type == "codexRateLimits" && Status == "available" && (!ExpiresAt.HasValue || ExpiresAt.Value > now); }
        public string Display { get { return (String.IsNullOrWhiteSpace(Title) ? "Full reset" : Title) + " — " + (ExpiresAt.HasValue ? "expires " + Data.LocalTime(ExpiresAt) : "no reported expiry"); } }
    }
    public sealed class Snapshot
    {
        public string AccountKey { get; set; }
        public string AccountDisplay { get; set; }
        public string Plan { get; set; }
        public long FetchedAt { get; set; }
        public int? AvailableCount { get; set; }
        public bool DetailsKnown { get; set; }
        public bool IdentityVerified { get; set; }
        public List<UsageWindow> Windows { get; set; }
        public List<ResetCredit> Credits { get; set; }
        public Snapshot() { Windows = new List<UsageWindow>(); Credits = new List<ResetCredit>(); }
        public UsageWindow Weekly { get { return Windows.FirstOrDefault(w => w.Minutes == 10080); } }
        public static Snapshot Parse(Dictionary<string, object> result, long now)
        {
            var s = new Snapshot { FetchedAt = now };
            var buckets = Data.Get(result, "rateLimitsByLimitId") as Dictionary<string, object>;
            var bucket = buckets != null ? Data.Map(Data.Get(buckets, "codex")) : Data.Map(Data.Get(result, "rateLimits"));
            string id = Data.Text(bucket, "limitId");
            if (id != null && id != "codex") bucket = new Dictionary<string, object>();
            s.Plan = Data.Text(bucket, "planType");
            foreach (string slot in new[] { "primary", "secondary" })
            {
                var w = Data.Map(Data.Get(bucket, slot));
                var used = Data.Number(w, "usedPercent"); var mins = Data.Integer(w, "windowDurationMins");
                if (used.HasValue && used >= 0 && used <= 100 && mins.HasValue && mins > 0 && mins <= Int32.MaxValue)
                    s.Windows.Add(new UsageWindow { Used = used.Value, Minutes = (int)mins.Value, ResetsAt = Data.Integer(w, "resetsAt") });
            }
            var summary = Data.Map(Data.Get(result, "rateLimitResetCredits"));
            var count = Data.Integer(summary, "availableCount");
            if (count.HasValue && count <= Int32.MaxValue) s.AvailableCount = (int)count.Value;
            var rows = Data.Get(summary, "credits") as object[];
            s.DetailsKnown = rows != null;
            if (rows != null) foreach (var item in rows)
            {
                var row = Data.Map(item);
                var c = new ResetCredit { Id = Data.Text(row, "id"), Type = Data.Text(row, "resetType"), Status = Data.Text(row, "status"), Title = Data.Text(row, "title"), GrantedAt = Data.Integer(row, "grantedAt") ?? 0, ExpiresAt = Data.Integer(row, "expiresAt") };
                if (Data.Get(row, "expiresAt") != null && !c.ExpiresAt.HasValue) continue;
                if (!String.IsNullOrWhiteSpace(c.Id) && !s.Credits.Any(x => x.Id == c.Id)) s.Credits.Add(c);
            }
            return s;
        }
    }
    public sealed class Rules
    {
        public double Threshold { get; set; }
        public string Window { get; set; }
        public int ArmHours { get; set; }
        public int MaximumResets { get; set; }
        public int ReserveCredits { get; set; }
        public List<string> AllowedCreditIds { get; set; }
        public string PreferredCreditId { get; set; }
        public Rules() { Threshold = 95; Window = "weekly"; ArmHours = 12; MaximumResets = 1; ReserveCredits = 1; AllowedCreditIds = new List<string>(); }
        public string Validate()
        {
            if (Double.IsNaN(Threshold) || Double.IsInfinity(Threshold) || Threshold < 1 || Threshold > 100) return "Choose a threshold between 1% and 100%.";
            if (Window != "weekly" && Window != "fiveHour" && Window != "either") return "Choose a supported usage window.";
            if (MaximumResets < 1 || MaximumResets > 100 || ReserveCredits < 0 || ReserveCredits > 100) return "Reset allowances must be between 1 and 100; reserves between 0 and 100.";
            if (ArmHours < 0 || ArmHours > 168) return "Choose an arming period of up to seven days, or until paused.";
            if (AllowedCreditIds == null) return "Choose which credits may be used.";
            return null;
        }
    }
    public sealed class PendingReset
    {
        public string Key { get; set; }
        public string CreditId { get; set; }
        public long? CreditExpiresAt { get; set; }
        public string AccountKey { get; set; }
        public string ArmId { get; set; }
        public int BeforeCount { get; set; }
        public List<UsageWindow> BeforeWindows { get; set; }
        public long StartedAt { get; set; }
        public long LastAttemptAt { get; set; }
        public int Attempts { get; set; }
        public bool Acknowledged { get; set; }
    }
    public sealed class HistoryEntry
    {
        public long At { get; set; }
        public string Event { get; set; }
        public string Detail { get; set; }
    }
    public sealed class GuardState
    {
        public int Version { get; set; }
        public Rules Rules { get; set; }
        public bool Enabled { get; set; }
        public string AccountKey { get; set; }
        public string ArmId { get; set; }
        public long ArmedUntil { get; set; }
        public int UsedThisArm { get; set; }
        public long LastAttemptAt { get; set; }
        public long LastSuccessAt { get; set; }
        public PendingReset Pending { get; set; }
        public List<HistoryEntry> History { get; set; }
        public string CodexExecutable { get; set; }
        public GuardState() { Version = 1; Rules = new Rules(); History = new List<HistoryEntry>(); }
        public void Record(string type, string detail, long now)
        {
            History.Insert(0, new HistoryEntry { At = now, Event = type, Detail = detail });
            if (History.Count > 100) History.RemoveRange(100, History.Count - 100);
        }
    }
}
