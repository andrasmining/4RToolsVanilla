using System;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace _4RTools.Model.Vanilla
{
    // Delivery consumes durable character-bound events; a closed process is not
    // needed to authorize or retry a notification. SMTP never owns client input.
    internal sealed class VanillaFarmingStopEmailDispatcher
    {
        private readonly VanillaReconnectSupervisor supervisor;
        private readonly Func<VanillaWeightAlertSettings> currentSettings;
        private readonly System.Action<VanillaWeightAlertSettings, string, string> send;
        private readonly Func<bool> active;
        private int polling;

        internal VanillaFarmingStopEmailDispatcher(VanillaReconnectSupervisor supervisor,
            Func<VanillaWeightAlertSettings> currentSettings,
            System.Action<VanillaWeightAlertSettings, string, string> send, Func<bool> active)
        {
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            this.currentSettings = currentSettings ?? throw new ArgumentNullException(nameof(currentSettings));
            this.send = send ?? throw new ArgumentNullException(nameof(send));
            this.active = active ?? throw new ArgumentNullException(nameof(active));
        }

        internal void Poll()
        {
            if (Interlocked.Exchange(ref polling, 1) != 0) return;
            try
            {
                if (!active()) return;
                foreach (VanillaFarmingStopNotice pending in supervisor.PendingFarmingStopEmails())
                {
                    if (!active()) return;
                    VanillaWeightAlertSettings transport;
                    if (!TryAuthorize(pending, out transport)) continue;
                    VanillaFarmingStopNotice reserved;
                    if (!supervisor.TryReserveFarmingStopEmail(pending.EventId, out reserved)) continue;
                    bool sent = false;
                    string deliveryDetail;
                    try
                    {
                        // Recheck switches and saved identity after reserving, not a
                        // stale PID or settings snapshot from the triggering poll.
                        if (!active() || !TryAuthorize(reserved, out transport)
                            || !supervisor.IsFarmingStopEmailAttemptCurrent(reserved.EventId, reserved.EmailAttemptId))
                        {
                            deliveryDetail = "Delivery deferred because the stop hold, notification settings or character ownership changed.";
                        }
                        else
                        {
                            transport.Validate(true);
                            string subject = (reserved.Kind == VanillaFarmingStopKind.Emergency ? "EMERGENCY STOP: " : "DONE: ")
                                + reserved.CharacterName;
                            string body = "Character: " + reserved.CharacterName + Environment.NewLine
                                + "Reason: " + reserved.Evidence + Environment.NewLine
                                + "Stopped at: " + reserved.ObservedAt.ToString("u", CultureInfo.InvariantCulture) + Environment.NewLine
                                + "Client: " + reserved.CloseState + ". " + reserved.CloseDetail + Environment.NewLine
                                + "Automatic relaunch: blocked until the corresponding stop hold is explicitly cleared.";
                            send(transport, subject, body);
                            sent = true;
                            deliveryDetail = "Notification sent.";
                        }
                    }
                    catch (Exception ex)
                    {
                        deliveryDetail = "Notification failed: " + ex.Message;
                    }
                    // A persistence failure after SMTP success must never be recast
                    // as a send failure, which could deliver the same event twice.
                    supervisor.CompleteFarmingStopEmail(reserved.EventId, reserved.EmailAttemptId, sent, deliveryDetail);
                    VanillaDebugLog.Write("MAIL", "event=farming-stop-email-result eventId=" + reserved.EventId
                        + " kind=" + reserved.Kind + " sent=" + sent + " detail='" + deliveryDetail + "'.");
                }
            }
            catch (Exception ex)
            {
                VanillaDebugLog.Write("MAIL", "event=farming-stop-email-worker-failed reason='" + ex.Message + "'.");
            }
            finally { Interlocked.Exchange(ref polling, 0); }
        }

        private bool TryAuthorize(VanillaFarmingStopNotice notice, out VanillaWeightAlertSettings transport)
        {
            transport = currentSettings();
            if (notice == null || transport == null || !notice.EmailRequested) return false;
            var accounts = supervisor.Settings.Accounts.Where(account => account.Enabled
                && string.Equals(account.Id, notice.AccountId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(VanillaCharacterRoster.Key(account),
                    VanillaCharacterRoster.Key(notice.UserName, notice.CharacterName), StringComparison.Ordinal)).ToArray();
            if (accounts.Length != 1) return false;
            if (notice.Kind == VanillaFarmingStopKind.Emergency)
                return supervisor.FarmingEmergencySettings.SendEmail;
            return notice.Kind == VanillaFarmingStopKind.Completed && transport.AutoCartEnabled && transport.Enabled
                && accounts[0].EffectiveCartMaintenanceEnabled && accounts[0].EffectiveWeightEmailEnabled;
        }
    }
}
