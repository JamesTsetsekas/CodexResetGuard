using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex Reset Guard")]
[assembly: AssemblyDescription("Opt-in Windows tray automation for existing Codex reset credits")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace CodexResetGuard
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--make-icon") { using (var icon = TrayIcons.Create(GuardForm.Accent)) using (var file = File.Create(args[1])) icon.Save(file); return 0; }
                if (args.Length == 2 && args[0] == "--read-only-check") return ReadOnlyCheck(args[1]).GetAwaiter().GetResult();
                try { SetProcessDpiAwareness(2); } catch { }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length == 2 && args[0] == "--ui-smoke") return UiSmoke.Run(args[1]);
                var userSid = WindowsIdentity.GetCurrent().User;
                string user = Data.Hash(userSid.Value).Substring(0, 24);
                // The journal is per user, shared by that user's Windows sessions.
                // Global mutex/events need no SeCreateGlobalPrivilege; restrict their ACLs to this user.
                var mutexSecurity = new MutexSecurity();
                mutexSecurity.AddAccessRule(new MutexAccessRule(userSid, MutexRights.FullControl, AccessControlType.Allow));
                var eventSecurity = new EventWaitHandleSecurity();
                eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(userSid, EventWaitHandleRights.FullControl, AccessControlType.Allow));
                bool owns, eventCreated;
                using (var mutex = new Mutex(true, "Global\\CodexResetGuard." + user, out owns, mutexSecurity))
                {
                    var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\CodexResetGuard.Show." + user, out eventCreated, eventSecurity);
                    if (!owns) { showEvent.Set(); showEvent.Dispose(); return 0; }
                    try
                    {
                        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexResetGuard");
                        var store = new StateStore(directory); var state = store.Load();
                        var gateway = new CodexGateway(() => state.CodexExecutable);
                        var engine = new ResetEngine(state, store, gateway, () => Data.Now);
                        using (var app = new TrayApplication(engine, store, showEvent, args.Length == 0 || args[0] != "--tray")) Application.Run(app);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex)
            {
                string message = ex is GuardException ? ex.Message : "Codex Reset Guard could not start. No reset request was sent during startup.";
                if (args.Length == 2 && args[0] == "--ui-smoke") { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "ui-smoke-error.txt"), ex.ToString()); }
                else if (args.Length == 2 && args[0] == "--read-only-check") File.WriteAllText(args[1], Data.Json(new { ok = false, error = message }));
                else MessageBox.Show(message, "Codex Reset Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        private static async Task<int> ReadOnlyCheck(string report)
        {
            var gateway = new CodexGateway(() => null);
            using (var session = await gateway.OpenAsync())
            {
                var s = await session.ReadAsync();
                File.WriteAllText(report, Data.Json(new { ok = true, identityVerified = s.IdentityVerified, windows = s.Windows, availableCredits = s.AvailableCount, creditDetails = s.Credits.Count, executable = gateway.Executable, modelTurnsStarted = 0, resetRequestsSent = 0 }));
                return s.IdentityVerified ? 0 : 2;
            }
        }
        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int awareness);
    }
}
