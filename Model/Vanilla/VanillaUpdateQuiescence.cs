using System.Linq;

namespace _4RTools.Model.Vanilla
{
    public sealed partial class VanillaReconnectSupervisor
    {
        internal bool TryPauseForApplicationUpdate()
        {
            lock (gate)
            {
                if (disposed || hardenedStartupRunning || launcherUpdateResetRunning || temporaryInputOwner != null
                    || runtimes.Values.Any(r => r.ScriptRunning || r.RecoveryOwned || r.ClosingForRecovery)) return false;
                Stop();
                return true;
            }
        }
    }
}
