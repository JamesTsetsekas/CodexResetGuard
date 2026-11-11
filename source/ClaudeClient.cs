using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace CodexResetGuard
{
    // Claude support is monitor-and-assist only. Claude's limit resets are claimed through
    // claude.ai (Settings > Usage) or Claude Code's /limit-reset, which ask the person to
    // confirm. Reset Guard reads usage with the sign-in Claude Code already stores, never
    // refreshes or saves that token, identifies itself honestly, and never claims a reset.
    public sealed class ClaudeSettings
    {
        public bool Enabled { get; set; }
        public double AlertThreshold { get; set; }
        public string Window { get; set; }
        public bool AlertAtLimit { get; set; }
        public List<string> AlertedKeys { get; set; }
        public ClaudeSettings() { AlertThreshold = 95; Window = "either"; AlertAtLimit = true; AlertedKeys = new List<string>(); }
        public string Validate()
        {
            if (Double.IsNaN(AlertThreshold) || Double.IsInfinity(AlertThreshold) || AlertThreshold < 1 || AlertThreshold > 100) return "Choose a Claude alert threshold between 1% and 100%.";
            if (Window != "fiveHour" && Window != "weekly" && Window != "either") return "Choose a supported Claude usage window.";
            return null;
        }
    }
    public sealed class ClaudeWindow
    {
        public string Kind { get; set; } // fiveHour, weekly, model
        public string Name { get; set; }
        public double Used { get; set; }
        public long? ResetsAt { get; set; }
    }
    public sealed class ClaudeGrant
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Type { get; set; }
        public int ResetsLeft { get; set; }
        public long? EndsAt { get; set; }
        public bool Paused { get; set; }
        public bool UsableNow { get; set; }
        public bool RequiresLimit { get; set; }
        public string UnavailableReason { get; set; }
    }
    public sealed class ClaudeSnapshot
    {
        public string AccountDisplay { get; set; }
        public string Plan { get; set; }
        public long FetchedAt { get; set; }
        public List<ClaudeWindow> Windows { get; set; }
        public List<ClaudeGrant> Grants { get; set; }
        public bool ResetStatusKnown { get; set; }
        public bool ResetsEligible { get; set; }
        public string IneligibleReason { get; set; }
        public bool AtLimit { get; set; }
        public long? CooldownUntil { get; set; }
        public ClaudeSnapshot() { Windows = new List<ClaudeWindow>(); Grants = new List<ClaudeGrant>(); }
        public ClaudeWindow FiveHour { get { return Windows.FirstOrDefault(w => w.Kind == "fiveHour"); } }
        public ClaudeWindow Weekly { get { return Windows.FirstOrDefault(w => w.Kind == "weekly"); } }
        public int ResetsHeld(long now) { return Grants.Where(g => !g.Paused && (!g.EndsAt.HasValue || g.EndsAt.Value > now)).Sum(g => g.ResetsLeft); }
        public long? FirstExpiry(long now) { var ends = Grants.Where(g => g.ResetsLeft > 0 && !g.Paused && g.EndsAt.HasValue && g.EndsAt.Value > now).Select(g => g.EndsAt.Value).ToList(); return ends.Count == 0 ? (long?)null : ends.Min(); }
        public string ResetSummary(long now)
        {
            if (!ResetStatusKnown) return "Reset details not reported";
            if (!ResetsEligible) return IneligibleReason == "surface" ? "Claude shows banked resets only in Claude apps" : "No limit resets offered" + (String.IsNullOrEmpty(IneligibleReason) ? "" : " (" + IneligibleReason.Replace('_', ' ') + ")");
            int held = ResetsHeld(now); long? first = FirstExpiry(now);
            return held == 0 ? "No limit resets left" : held + (held == 1 ? " reset" : " resets") + " held" + (first.HasValue ? " · first expires " + UsageSummary.ShortDate(first.Value) : "");
        }
        internal static long? Time(object value)
        {
            var text = value as string; DateTimeOffset parsed;
            if (String.IsNullOrWhiteSpace(text) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed)) return null;
            return parsed.ToUnixTimeSeconds();
        }
        public static ClaudeSnapshot Parse(Dictionary<string, object> body, long now)
        {
            var s = new ClaudeSnapshot { FetchedAt = now };
            foreach (var slot in new[] { new[] { "five_hour", "fiveHour", "5-hour" }, new[] { "seven_day", "weekly", "Weekly" } })
            {
                var w = Data.Get(body, slot[0]) as Dictionary<string, object>; if (w == null) continue;
                var used = Data.Number(w, "utilization");
                if (used.HasValue && used.Value >= 0 && used.Value <= 1000) s.Windows.Add(new ClaudeWindow { Kind = slot[1], Name = slot[2], Used = Math.Min(100, used.Value), ResetsAt = Time(Data.Get(w, "resets_at")) });
            }
            var limits = Data.Get(body, "limits") as object[];
            if (limits != null) foreach (var item in limits)
            {
                var row = Data.Map(item); if (Data.Text(row, "kind") != "weekly_scoped") continue;
                var model = Data.Map(Data.Get(Data.Map(Data.Get(row, "scope")), "model")); string name = Data.Text(model, "display_name");
                var used = Data.Number(row, "percent");
                if (!String.IsNullOrWhiteSpace(name) && name.Length <= 40 && used.HasValue && used.Value >= 0 && used.Value <= 1000 && !s.Windows.Any(x => x.Kind == "model" && x.Name == name))
                    s.Windows.Add(new ClaudeWindow { Kind = "model", Name = name, Used = Math.Min(100, used.Value), ResetsAt = Time(Data.Get(row, "resets_at")) });
            }
            var resets = Data.Get(body, "cedar_ember") as Dictionary<string, object>;
            if (resets != null)
            {
                s.ResetStatusKnown = true; s.ResetsEligible = Data.Get(resets, "eligible") as bool? ?? false;
                s.IneligibleReason = Data.Text(resets, "ineligible_reason"); s.AtLimit = Data.Get(resets, "at_limit") as bool? ?? false; s.CooldownUntil = Time(Data.Get(resets, "cooldown_until"));
                var grants = Data.Get(resets, "grants") as object[];
                if (grants != null) foreach (var item in grants)
                {
                    var g = Data.Map(item); string id = Data.Text(g, "id"); var left = Data.Integer(g, "resets_left");
                    if (String.IsNullOrWhiteSpace(id) || !left.HasValue || left.Value > 1000) continue;
                    s.Grants.Add(new ClaudeGrant { Id = id, Label = Data.Text(g, "label"), Type = Data.Text(g, "type"), ResetsLeft = (int)left.Value, EndsAt = Time(Data.Get(g, "ends_at")), Paused = Data.Get(g, "paused") as bool? ?? false, UsableNow = Data.Get(g, "usable_now") as bool? ?? false, RequiresLimit = Data.Get(g, "use_requires_limit") as bool? ?? true, UnavailableReason = Data.Text(g, "unavailable_reason") });
                }
            }
            return s;
        }
    }
    public sealed class ClaudeAlert
    {
        public string Key;
        public string Title;
        public string Message;
    }
    public static class ClaudePolicy
    {
        public static List<ClaudeAlert> Evaluate(ClaudeSettings settings, ClaudeSnapshot s, long now)
        {
            var alerts = new List<ClaudeAlert>();
            if (settings == null || !settings.Enabled || settings.Validate() != null || s == null || now - s.FetchedAt > 180) return alerts;
            var watched = s.Windows.Where(w => (w.Kind == "fiveHour" && settings.Window != "weekly") || (w.Kind == "weekly" && settings.Window != "fiveHour"));
            int held = s.ResetsHeld(now);
            string offer = held > 0 ? " You hold " + held + (held == 1 ? " reset" : " resets") + "." : "";
            const string how = " Use one with /limit-reset in Claude Code, or in claude.ai Settings > Usage.";
            foreach (var w in watched.OrderByDescending(w => w.Used))
            {
                if (w.ResetsAt.HasValue && w.ResetsAt.Value <= now + 120) continue; // About to reset naturally.
                string window = w.Kind + ":" + ((w.ResetsAt ?? 0) / 600); // Ten-minute buckets absorb reset-time jitter.
                if (w.Used >= 100 && settings.AlertAtLimit)
                    alerts.Add(new ClaudeAlert { Key = window + ":limit", Title = "Claude " + w.Name + " limit reached", Message = "Claude " + w.Name.ToLowerInvariant() + " usage is at 100%." + offer + how });
                else if (w.Used >= settings.AlertThreshold)
                    alerts.Add(new ClaudeAlert { Key = window + ":threshold", Title = "Claude " + w.Name + " usage at " + Data.Percent(w.Used), Message = "Your Claude " + w.Name.ToLowerInvariant() + " window resets " + (w.ResetsAt.HasValue ? UsageSummary.ShortDate(w.ResetsAt.Value) : "at an unreported time") + "." + offer + how });
            }
            return alerts;
        }
    }
    public interface IClaudeSource { Task<ClaudeSnapshot> ReadAsync(); }
    internal sealed class ClaudeCredentials
    {
        public string AccessToken;
        public long ExpiresAt;
        public string Plan;
        public string Email;
        public static string ConfigDirectory
        {
            get { string custom = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"); return !String.IsNullOrWhiteSpace(custom) ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"); }
        }
        public static bool Installed { get { return File.Exists(Path.Combine(ConfigDirectory, ".credentials.json")); } }
        public static ClaudeCredentials Read()
        {
            // The token is read into memory for one request. It is never logged, copied, refreshed or persisted.
            string path = Path.Combine(ConfigDirectory, ".credentials.json");
            if (!File.Exists(path)) throw new GuardException("Claude Code sign-in was not found. Sign in to Claude Code with your Claude subscription first.");
            if (new FileInfo(path).Length > 1024 * 1024) throw new GuardException("The Claude Code credentials file is unexpectedly large.");
            Dictionary<string, object> oauth;
            try { oauth = Data.Map(Data.Get(Data.Parse(File.ReadAllText(path, Encoding.UTF8)), "claudeAiOauth")); }
            catch (Exception ex) { throw new GuardException("The Claude Code sign-in could not be read.", ex); }
            var c = new ClaudeCredentials { AccessToken = Data.Text(oauth, "accessToken"), ExpiresAt = (Data.Integer(oauth, "expiresAt") ?? 0) / 1000, Plan = Data.Text(oauth, "subscriptionType") };
            if (String.IsNullOrWhiteSpace(c.AccessToken)) throw new GuardException("Claude Code is not signed in with a Claude subscription. API-key sign-ins have no plan limits to watch.");
            var scopes = Data.Get(oauth, "scopes") as object[];
            if (scopes != null && !scopes.OfType<string>().Contains("user:profile")) throw new GuardException("This Claude Code sign-in cannot read plan usage. Run 'claude auth login' to sign in again.");
            try
            {
                string config = Path.Combine(Path.GetDirectoryName(ConfigDirectory.TrimEnd('\\', '/')) ?? "", ".claude.json");
                if (!String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"))) config = Path.Combine(ConfigDirectory, ".claude.json");
                if (File.Exists(config) && new FileInfo(config).Length < 16 * 1024 * 1024) c.Email = Data.Text(Data.Map(Data.Get(Data.Parse(File.ReadAllText(config, Encoding.UTF8)), "oauthAccount")), "emailAddress");
            }
            catch { }
            return c;
        }
    }
    public sealed class ClaudeUsageSource : IClaudeSource
    {
        internal const string UsageUrl = "https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=1";
        public async Task<ClaudeSnapshot> ReadAsync()
        {
            var credentials = ClaudeCredentials.Read();
            if (credentials.ExpiresAt > 0 && credentials.ExpiresAt <= Data.Now + 60) throw new GuardException("The Claude Code sign-in has expired. Open Claude Code once to refresh it; Reset Guard never refreshes Claude tokens.");
            var request = Task.Run(() => Get(credentials.AccessToken));
            if (await Task.WhenAny(request, Task.Delay(30000)) != request) { var ignored = request.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted); throw new GuardException("Claude usage took too long to load."); }
            string text = await request;
            var snapshot = ClaudeSnapshot.Parse(Data.Parse(text), Data.Now);
            snapshot.AccountDisplay = credentials.Email ?? "Claude account"; snapshot.Plan = credentials.Plan;
            if (snapshot.Windows.Count == 0) throw new GuardException("Claude did not report plan usage for this sign-in.");
            return snapshot;
        }
        private static string Get(string token)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var request = (HttpWebRequest)WebRequest.Create(UsageUrl);
            request.Method = "GET"; request.Timeout = 15000; request.ReadWriteTimeout = 15000; request.AllowAutoRedirect = false;
            request.Accept = "application/json"; request.UserAgent = "CodexResetGuard/" + Program.Version + " (usage monitor)";
            request.Headers["Authorization"] = "Bearer " + token; request.Headers["anthropic-beta"] = "oauth-2025-04-20";
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var buffer = new char[1024 * 1024 + 1]; int total = 0, read;
                    while ((read = reader.Read(buffer, total, buffer.Length - total)) > 0) { total += read; if (total > 1024 * 1024) throw new GuardException("Claude returned an oversized usage response."); }
                    return new string(buffer, 0, total);
                }
            }
            catch (WebException ex)
            {
                // Response bodies are discarded: they may contain account details.
                var response = ex.Response as HttpWebResponse; int code = response == null ? 0 : (int)response.StatusCode; if (response != null) response.Dispose();
                if (code == 401 || code == 403) throw new GuardException("Claude rejected the stored sign-in. Open Claude Code to refresh it, or run 'claude auth login'.");
                if (code == 429) throw new GuardException("Claude asked Reset Guard to slow down. Usage checks will back off.");
                throw new GuardException(code == 0 ? "Claude could not be reached." : "Claude usage is unavailable right now (HTTP " + code + ").");
            }
        }
        internal static string LocateCli()
        {
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string dir = entry.Trim().Trim('"'); if (String.IsNullOrWhiteSpace(dir)) continue;
                foreach (string name in new[] { "claude.exe", "claude.cmd" }) try { string candidate = Path.Combine(dir, name); if (File.Exists(candidate)) return candidate; } catch { }
            }
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            if (File.Exists(local)) return local;
            string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
            return File.Exists(npm) ? npm : null;
        }
        public static void OpenLimitReset()
        {
            // Opens Claude Code's own confirmation dialog. Nothing is redeemed unless the person confirms there.
            string cli = LocateCli(); if (cli == null) throw new GuardException("Claude Code was not found. Open claude.ai Settings > Usage to use a reset instead.");
            try { Process.Start(new ProcessStartInfo(cli, "\"/limit-reset\"") { UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }); }
            catch (Exception ex) { throw new GuardException("Claude Code could not be opened.", ex); }
        }
        public static void OpenUsagePage()
        {
            try { Process.Start(new ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true }); }
            catch (Exception ex) { throw new GuardException("The browser could not be opened.", ex); }
        }
    }
    public sealed class ClaudeMonitor
    {
        private readonly IClaudeSource source;
        private readonly Func<long> clock;
        private readonly Action save;
        private bool busy;
        private int generation; // A check that started under older settings must not overwrite newer state.
        public ClaudeSettings Settings { get; private set; }
        public ClaudeSnapshot Latest { get; private set; }
        public string Status { get; private set; }
        public int Failures { get; private set; }
        public bool Busy { get { return busy; } }
        public event Action Changed;
        public event Action<string, string> Notice;
        public ClaudeMonitor(ClaudeSettings settings, IClaudeSource source, Func<long> clock, Action save)
        {
            Settings = settings ?? new ClaudeSettings(); this.source = source; this.clock = clock; this.save = save;
            Status = Settings.Enabled ? "Checking Claude usage…" : "Claude monitoring is off.";
        }
        public int PollSeconds
        {
            get
            {
                if (Failures > 0) return Math.Min(900, 60 * (1 << Math.Min(Failures, 4)));
                var high = Latest == null ? null : Latest.Windows.Where(w => (w.Kind == "fiveHour" && Settings.Window != "weekly") || (w.Kind == "weekly" && Settings.Window != "fiveHour")).OrderByDescending(w => w.Used).FirstOrDefault();
                return high != null && high.Used >= Settings.AlertThreshold - 10 ? 60 : 180;
            }
        }
        public void Apply(ClaudeSettings settings)
        {
            string error = settings.Validate(); if (error != null) throw new GuardException(error);
            settings.AlertedKeys = Settings.AlertedKeys ?? new List<string>(); Settings = settings; generation++;
            if (!settings.Enabled) { Latest = null; Failures = 0; Status = "Claude monitoring is off."; }
            save(); if (Changed != null) Changed();
        }
        public async Task CheckAsync()
        {
            if (busy || !Settings.Enabled) return;
            busy = true; if (Changed != null) Changed(); int started = generation;
            try
            {
                var read = await source.ReadAsync();
                if (started != generation || !Settings.Enabled) return;
                Latest = read; Failures = 0;
                var five = Latest.FiveHour; var week = Latest.Weekly;
                Status = "5-hour " + (five == null ? "—" : Data.Percent(five.Used)) + " · weekly " + (week == null ? "—" : Data.Percent(week.Used)) + " · " + Latest.ResetSummary(clock());
                var alert = ClaudePolicy.Evaluate(Settings, Latest, clock()).FirstOrDefault(a => !Settings.AlertedKeys.Contains(a.Key));
                if (alert != null)
                {
                    Settings.AlertedKeys.Insert(0, alert.Key); if (Settings.AlertedKeys.Count > 20) Settings.AlertedKeys.RemoveRange(20, Settings.AlertedKeys.Count - 20);
                    try { save(); } catch { }
                    if (Notice != null) Notice(alert.Title, alert.Message);
                }
            }
            catch (Exception ex) { if (started != generation) return; Failures++; Status = ex is GuardException ? ex.Message : "Claude usage could not be checked."; }
            finally { busy = false; if (Changed != null) Changed(); }
        }
    }
}
