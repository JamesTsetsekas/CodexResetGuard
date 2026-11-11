using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CodexResetGuard
{
    internal sealed class AuthIdentity
    {
        public string Key;
        public string Email;
        public static string CodexHome
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (!String.IsNullOrWhiteSpace(custom))
                {
                    custom = Environment.ExpandEnvironmentVariables(custom.Trim().Trim('"'));
                    try { if (Path.IsPathRooted(custom)) return Path.GetFullPath(custom); } catch (ArgumentException) { }
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }
        }
        public static AuthIdentity Read()
        {
            try
            {
                string path = Path.Combine(CodexHome, "auth.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
                // Read identity fields only. Tokens are never logged, copied or persisted by this app.
                var root = Data.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (Data.Text(root, "auth_mode") != "chatgpt") return null;
                var tokens = Data.Map(Data.Get(root, "tokens"));
                string id = Data.Text(tokens, "account_id"); string jwt = Data.Text(tokens, "id_token");
                if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(jwt)) return null;
                string[] parts = jwt.Split('.'); if (parts.Length != 3) return null;
                string body = parts[1].Replace('-', '+').Replace('_', '/'); body += new string('=', (4 - body.Length % 4) % 4);
                var claims = Data.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(body)));
                string email = Data.Text(claims, "email");
                var authClaims = Data.Map(Data.Get(claims, "https://api.openai.com/auth"));
                string user = Data.Text(authClaims, "chatgpt_user_id") ?? Data.Text(claims, "sub");
                if (String.IsNullOrWhiteSpace(email) || String.IsNullOrWhiteSpace(user)) return null;
                // JWT claims are only a local account-change guard; official Codex authenticates upstream.
                return new AuthIdentity { Key = Data.Hash(id + "\n" + user), Email = email };
            }
            catch { return null; }
        }
    }
    public sealed class CodexGateway : IAccountGateway
    {
        private readonly Func<string> configuredPath;
        private string located, lastConfigured;
        private string verifiedStamp;
        public string Executable { get { return located; } }
        public CodexGateway(Func<string> path) { configuredPath = path; }
        public async Task<IAccountSession> OpenAsync()
        {
            string configured = configuredPath();
            if (configured != lastConfigured) { located = null; verifiedStamp = null; lastConfigured = configured; } // Clearing the setting restores detection.
            if (!String.IsNullOrWhiteSpace(configured))
            {
                try { located = Path.GetFullPath(configured.Trim().Trim('"')); }
                catch (Exception ex) { throw new GuardException("The Codex executable path in Settings is not valid. Choose codex.exe again, or clear it to detect automatically.", ex); }
            }
            else if (located == null || !File.Exists(located)) located = Locate();
            if (String.IsNullOrWhiteSpace(located) || !File.Exists(located)) throw new GuardException("Codex CLI was not found. Install the official Codex CLI, or choose codex.exe on the Settings page.");
            var info = new FileInfo(located);
            string stamp = located + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            if (verifiedStamp != stamp)
            {
                string version = await Task.Run(() => VersionOutput(located));
                if (!SupportedVersion(version))
                    throw new GuardException("Codex CLI 0.147.0 or newer is required for selecting a specific reset credit (found: " + (VersionText(version) ?? "unknown version") + "). Update the CLI before enabling automatic resets.");
                verifiedStamp = stamp;
            }
            var session = new CodexSession(located, null);
            try { await session.InitializeAsync(); return session; } catch { session.Dispose(); throw; }
        }
        internal static string VersionText(string output)
        {
            var match = Regex.Match(output ?? "", @"\b(\d+)\.(\d+)\.(\d+)\b"); return match.Success ? match.Value : null;
        }
        internal static bool SupportedVersion(string output)
        {
            var match = Regex.Match(output ?? "", @"codex(?:-cli)?[^\d\r\n]{0,20}(\d+)\.(\d+)\.(\d+)", RegexOptions.IgnoreCase);
            int major, minor;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out major) || !Int32.TryParse(match.Groups[2].Value, out minor)) return false;
            return major > 0 || minor >= 147;
        }
        private static string VersionOutput(string path)
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo(path, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    p.Start(); var read = p.StandardOutput.ReadToEndAsync(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(10000)) { p.Kill(); throw new GuardException("Codex version check timed out."); }
                    return read.GetAwaiter().GetResult();
                }
            }
            catch (GuardException) { throw; }
            catch (Exception ex) { throw new GuardException("The selected Codex executable could not be started.", ex); }
        }
        internal static string Locate()
        {
            var roots = new List<string>();
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string dir = entry.Trim().Trim('"'); if (String.IsNullOrWhiteSpace(dir)) continue;
                try { if (File.Exists(Path.Combine(dir, "codex.exe"))) return Path.Combine(dir, "codex.exe"); roots.Add(Path.Combine(dir, "node_modules", "@openai", "codex")); } catch { }
            }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node_modules", "@openai", "codex"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex"));
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string candidate in new[] {
                    Path.Combine(root, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"),
                    Path.Combine(root, "node_modules", "@openai", "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc", "bin", "codex.exe"),
                    Path.Combine(root, "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe") })
                    if (File.Exists(candidate)) return candidate;
            }
            string plugin = Path.Combine(AuthIdentity.CodexHome, "plugins", ".plugin-appserver", "codex.exe");
            return File.Exists(plugin) ? plugin : null;
        }
    }
    internal sealed class CodexSession : IAccountSession
    {
        private readonly RpcClient rpc;
        private AuthIdentity identity;
        private readonly bool testMode;
        public CodexSession(string path, string testArguments)
        {
            testMode = testArguments != null;
            identity = testMode ? new AuthIdentity { Key = "test-account", Email = "test@example.invalid" } : AuthIdentity.Read();
            rpc = new RpcClient(path, testArguments ?? "app-server --listen stdio://");
        }
        public async Task InitializeAsync()
        {
            await rpc.CallAsync("initialize", new { clientInfo = new { name = "codex_reset_guard", title = "Reset Guard", version = Program.Version }, capabilities = new { experimentalApi = true } });
            rpc.Notify("initialized", new { });
        }
        public async Task<Snapshot> ReadAsync()
        {
            var a = await rpc.CallAsync("account/read", new { refreshToken = false });
            var account = Data.Map(Data.Get(a, "account"));
            if (Data.Text(account, "type") != "chatgpt") throw new GuardException("Sign in to Codex with your ChatGPT account. API-key-only accounts do not have banked reset credits.");
            var result = await rpc.CallAsync("account/rateLimits/read", new { });
            var s = Snapshot.Parse(result, Data.Now);
            s.AccountDisplay = Data.Text(account, "email") ?? "Signed-in account";
            s.Plan = s.Plan ?? Data.Text(account, "planType");
            var current = testMode ? identity : AuthIdentity.Read();
            s.IdentityVerified = identity != null && current != null && identity.Key == current.Key && String.Equals(identity.Email, s.AccountDisplay, StringComparison.OrdinalIgnoreCase);
            s.AccountKey = s.IdentityVerified ? identity.Key : null;
            return s;
        }
        public async Task<string> ConsumeAsync(PendingReset pending)
        {
            var current = testMode ? identity : AuthIdentity.Read();
            if (identity == null || current == null || identity.Key != current.Key || current.Key != pending.AccountKey) throw new GuardException("The Codex account changed before redemption. The request was not sent.");
            if (String.IsNullOrWhiteSpace(pending.CreditId) || String.IsNullOrWhiteSpace(pending.Key)) throw new GuardException("A specific credit and saved request identity are required.");
            var result = await rpc.CallAsync("account/rateLimitResetCredit/consume", new { idempotencyKey = pending.Key, creditId = pending.CreditId });
            return Data.Text(result, "outcome");
        }
        public void Dispose() { rpc.Dispose(); }
    }
    internal sealed class RpcClient : IDisposable
    {
        private readonly Process process;
        private readonly ChildJob job;
        private StreamWriter input;
        private readonly object sync = new object();
        private readonly object writeLock = new object();
        private bool closed;
        private Task writeChain = Task.FromResult(true); // One FIFO writer: messages reach the helper in the order they were sent.
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> calls = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private int nextId;
        private bool disposed;
        internal int TimeoutMilliseconds = 25000;
        public RpcClient(string path, string arguments)
        {
            process = new Process();
            process.StartInfo = new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory };
            // The helper must use the same Codex home whose identity Reset Guard checks.
            if (!String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME"))) process.StartInfo.EnvironmentVariables["CODEX_HOME"] = AuthIdentity.CodexHome;
            process.OutputDataReceived += OnLine;
            process.ErrorDataReceived += delegate { }; // Never persist server stderr or authentication material.
            process.EnableRaisingEvents = true;
            // stdout EOF (null line) is the authoritative end: replies flushed just before exit are still delivered.
            // The exit fallback only covers a descendant that keeps the pipe open.
            process.Exited += delegate { Task.Delay(3000).ContinueWith(_ => Close("The local Codex connection closed.")); };
            try
            {
                // .NET Framework bases redirected stdin on the parent console encoding.
                // A BOM-bearing UTF-8 console encoding would otherwise prefix the first RPC.
                try { Console.InputEncoding = new UTF8Encoding(false); } catch (IOException) { }
                process.Start(); job = new ChildJob(process.Handle);
                input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
            catch (Exception ex) { Dispose(); throw new GuardException("Could not open a local Codex app-server connection.", ex); }
        }
        internal async Task<Dictionary<string, object>> CallAsync(string method, object parameters)
        {
            if (method != "initialize" && method != "account/read" && method != "account/rateLimits/read" && method != "account/rateLimitResetCredit/consume") throw new GuardException("This app only supports account usage and reset-credit operations.");
            int id; var completion = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync)
            {
                if (disposed || closed) throw new GuardException("The local Codex connection is closed.");
                id = ++nextId; calls.Add(id, completion);
            }
            string line = Data.Json(new { id = id, method = method, @params = parameters });
            // Pipe writes run off the caller's thread, so a helper that stops reading cannot freeze the UI; the deadline covers delivery too.
            var write = Enqueue(line);
            var delivery = write.ContinueWith(t => { if (t.IsFaulted) { lock (sync) calls.Remove(id); completion.TrySetException(new GuardException("The request could not be delivered to the local Codex connection.")); } }, TaskContinuationOptions.ExecuteSynchronously);
            if (await Task.WhenAny(completion.Task, Task.Delay(TimeoutMilliseconds)) != completion.Task)
            {
                lock (sync) calls.Remove(id);
                throw new GuardException("Codex did not reply in time. An outstanding reset remains pending with its original request identity.");
            }
            return await completion.Task;
        }
        internal void Notify(string method, object parameters)
        {
            if (method != "initialized") throw new GuardException("Unsupported notification.");
            string line = Data.Json(new { method = method, @params = parameters });
            Enqueue(line);
        }
        private Task Enqueue(string line)
        {
            lock (writeLock) { writeChain = writeChain.ContinueWith(_ => Write(line), TaskScheduler.Default); return writeChain; }
        }
        private void Write(string line)
        {
            if (disposed) throw new ObjectDisposedException("RpcClient");
            input.WriteLine(line); // Runs only on the write chain, one message at a time.
        }
        private void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) { Close("The local Codex connection closed."); return; }
            if (String.IsNullOrWhiteSpace(e.Data)) return;
            if (e.Data.Length > 4 * 1024 * 1024) { FailAll("Codex returned an oversized response."); return; }
            Dictionary<string, object> message; try { message = Data.Parse(e.Data); } catch { return; }
            var id = Data.Integer(message, "id"); if (!id.HasValue || id > Int32.MaxValue) return;
            if (Data.Text(message, "method") != null)
            {
                string reply = Data.Json(new { id = id.Value, error = new { code = -32601, message = "Unsupported client request" } });
                Enqueue(reply);
                return;
            }
            TaskCompletionSource<Dictionary<string, object>> completion;
            lock (sync) { if (!calls.TryGetValue((int)id.Value, out completion)) return; calls.Remove((int)id.Value); }
            if (Data.Get(message, "error") != null)
            {
                // Error text may contain sensitive provider details. Keep only the numeric RPC code.
                var code = Data.Number(Data.Map(Data.Get(message, "error")), "code");
                completion.TrySetException(new GuardException("Codex rejected the request (RPC " + (code.HasValue ? code.ToString() : "error") + "). Check the Codex sign-in and CLI version."));
            }
            else if (!message.ContainsKey("result")) completion.TrySetException(new GuardException("Codex returned an incomplete response."));
            else completion.TrySetResult(Data.Map(Data.Get(message, "result")));
        }
        private void Close(string message)
        {
            lock (sync) closed = true;
            FailAll(message);
        }
        private void FailAll(string message)
        {
            TaskCompletionSource<Dictionary<string, object>>[] pending;
            lock (sync) { pending = calls.Values.ToArray(); calls.Clear(); }
            foreach (var p in pending) p.TrySetException(new GuardException(message));
        }
        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; }
            // Only the helper app-server process and its own job are stopped. The job is closed before stdin so a
            // descendant holding the pipe dies too; stdin is released off this thread so the UI never waits on it.
            try { if (!process.HasExited && !process.WaitForExit(200)) process.Kill(); } catch { }
            if (job != null) job.Dispose();
            FailAll("The local Codex connection was closed.");
            var writer = input; Task.Run(() => { try { if (writer != null) writer.Dispose(); } catch { } try { process.Dispose(); } catch { } });
        }
    }
    internal sealed class ChildJob : IDisposable
    {
        private IntPtr handle;
        public ChildJob(IntPtr process)
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new GuardException("Could not create an isolated process job.");
            var info = new ExtendedLimit(); info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int length = Marshal.SizeOf(typeof(ExtendedLimit)); IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(handle, 9, ptr, (uint)length) || !AssignProcessToJobObject(handle, process)) { Dispose(); throw new GuardException("Could not isolate the helper Codex process."); }
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
