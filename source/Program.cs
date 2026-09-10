using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex Reset Guard")]
[assembly: AssemblyDescription("Opt-in Windows tray automation for existing Codex reset credits")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

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
                string user = Data.Hash(WindowsIdentity.GetCurrent().User.Value).Substring(0, 24);
                bool owns;
                using (var mutex = new Mutex(true, "Local\\CodexResetGuard." + user, out owns))
                {
                    var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexResetGuard.Show." + user);
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
                if (args.Length == 2 && args[0] == "--read-only-check") File.WriteAllText(args[1], Data.Json(new { ok = false, error = message }));
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
