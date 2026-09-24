using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal enum VanillaServiceStep { Proxy, GameServer }

    // The client already selects its default service. Accept it once, in the
    // intended foreground window, without OCR, row clicks or highlight polling.
    internal static class VanillaServiceSelection
    {
        internal static void ConfirmDefault(System.Action activate, System.Action pressEnter,
            Action<int> pause, Func<bool> cancelled, int settleMs = 1000)
        {
            if (activate == null || pressEnter == null || pause == null) throw new ArgumentNullException();
            if (settleMs < 0 || settleMs > 120000) throw new ArgumentOutOfRangeException(nameof(settleMs));
            CheckCancelled(cancelled);
            activate();
            for (int remaining = settleMs; remaining > 0; remaining -= 50)
            {
                CheckCancelled(cancelled);
                pause(Math.Min(50, remaining));
            }
            CheckCancelled(cancelled);
            // Production Press atomically reactivates/verifies this same window
            // immediately before sending the key. Never retry Enter across stages.
            pressEnter();
        }

        private static void CheckCancelled(Func<bool> cancelled)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException("Service confirmation cancelled.");
        }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private void ConfirmDefaultService(VanillaForegroundInput input, VanillaServiceStep step, string logPrefix)
        {
            int settleMs;
            lock (gate) settleMs = step == VanillaServiceStep.Proxy ? settings.GepardWaitMs : settings.StageDelayMs;
            VanillaServiceSelection.ConfirmDefault(() =>
            {
                input.Activate();
                input.MoveCursorAwayFrom(Rectangle.Empty);
            }, () => input.Press(Keys.Enter), Thread.Sleep, input.CancellationRequested, settleMs);
            string detail = logPrefix + (step == VanillaServiceStep.Proxy ? "Proxy" : "Game server")
                + ": sent one Enter for the client's current selection in the active Vanilla window.";
            Log(detail);
            VanillaDebugLog.Write("SERVICE", detail);
        }
    }
}
