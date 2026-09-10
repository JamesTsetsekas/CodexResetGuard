using System;
using System.IO;
using System.Text;

namespace CodexResetGuard
{
    public interface IStateStore { void Save(GuardState state); }
    public sealed class StateStore : IStateStore
    {
        public string DirectoryPath { get; private set; }
        public string StatePath { get { return Path.Combine(DirectoryPath, "state.json"); } }
        public StateStore(string directory) { DirectoryPath = Path.GetFullPath(directory); }
        public GuardState Load()
        {
            if (!File.Exists(StatePath)) return new GuardState();
            try
            {
                if (new FileInfo(StatePath).Length > 2 * 1024 * 1024) throw new InvalidDataException();
                var state = Data.Serializer().Deserialize<GuardState>(File.ReadAllText(StatePath, Encoding.UTF8));
                if (state == null || state.Version != 1 || state.Rules == null || state.Rules.Validate() != null || state.History == null || state.UsedThisArm < 0) throw new InvalidDataException();
                if (state.Pending != null && (String.IsNullOrWhiteSpace(state.Pending.Key) || String.IsNullOrWhiteSpace(state.Pending.CreditId) || String.IsNullOrWhiteSpace(state.Pending.AccountKey) || state.Pending.BeforeWindows == null)) throw new InvalidDataException();
                if (state.Enabled && (String.IsNullOrWhiteSpace(state.AccountKey) || String.IsNullOrWhiteSpace(state.ArmId))) throw new InvalidDataException();
                return state;
            }
            catch (Exception ex) { throw new GuardException("Saved state could not be read. The app will not run automatic resets until the state file is recovered. Your file has been left intact.", ex); }
        }
        public void Save(GuardState state)
        {
            Directory.CreateDirectory(DirectoryPath);
            string temp = StatePath + ".tmp";
            byte[] bytes = Encoding.UTF8.GetBytes(Data.Json(state));
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(StatePath)) File.Replace(temp, StatePath, null); else File.Move(temp, StatePath);
            }
            catch (Exception ex) { throw new GuardException("Could not save the reset journal. Automatic resets have been stopped; no new request will be sent.", ex); }
        }
    }
    public sealed class GuardException : Exception
    {
        public GuardException(string message) : base(message) { }
        public GuardException(string message, Exception inner) : base(message, inner) { }
    }
}
