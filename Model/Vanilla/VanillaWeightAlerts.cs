using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using _4RTools.Model.Vanilla.Automation;
using _4RTools.Utils;

namespace _4RTools.Model.Vanilla
{
    internal enum VanillaWeightMailMode
    {
        None,
        CarriedWeight,
        CartMilestones
    }

    public sealed class VanillaWeightAlertSettings
    {
        public int Version { get; set; } = 1;
        public bool Enabled { get; set; }
        public decimal ThresholdPercent { get; set; } = 85m;
        public decimal RearmPercent { get; set; } = 80m;
        public int PollSeconds { get; set; } = 5;
        public int CooldownMinutes { get; set; } = 30;
        public string SmtpHost { get; set; } = "";
        public int SmtpPort { get; set; } = 587;
        public bool UseSsl { get; set; } = true;
        public string SmtpUser { get; set; } = "";
        public string ProtectedSmtpPassword { get; set; } = "";
        public string FromAddress { get; set; } = "";
        public string ToAddress { get; set; } = "";
        public string SubjectPrefix { get; set; } = "[4RTools Vanilla]";

        // UI-only cart maintenance. Memory remains read-only; these settings only drive ordinary window input.
        public bool AutoCartEnabled { get; set; }
        public decimal AutoCartThresholdPercent { get; set; } = 50m;
        public decimal AutoCartRearmPercent { get; set; } = 40m;
        public bool TransferUseItems { get; set; } = true;
        public bool TransferEquipItems { get; set; }
        public bool TransferEtcItems { get; set; } = true;
        // Dedicated Autobattle OFF command used before any Inventory/Cart UI manipulation.
        // Resume/start remains the per-character Recovery hotkey.
        public int AutobattleStopKey { get; set; } = (int)Keys.D3;
        public bool AutobattleStopCtrl { get; set; }
        public bool AutobattleStopAlt { get; set; } = true;
        public bool AutobattleStopShift { get; set; }
        public int InventoryKey { get; set; } = (int)Keys.E;
        public bool InventoryCtrl { get; set; }
        public bool InventoryAlt { get; set; } = true;
        public bool InventoryShift { get; set; }
        public int CartKey { get; set; } = (int)Keys.W;
        public bool CartCtrl { get; set; }
        public bool CartAlt { get; set; } = true;
        public bool CartShift { get; set; }

        public string AutobattleStopHotkeyText { get { return HotkeyText(AutobattleStopCtrl, AutobattleStopAlt, AutobattleStopShift, AutobattleStopKey); } }
        public string InventoryHotkeyText { get { return HotkeyText(InventoryCtrl, InventoryAlt, InventoryShift, InventoryKey); } }
        public string CartHotkeyText { get { return HotkeyText(CartCtrl, CartAlt, CartShift, CartKey); } }

        public VanillaWeightAlertSettings Clone()
        {
            var value = JsonConvert.DeserializeObject<VanillaWeightAlertSettings>(JsonConvert.SerializeObject(this));
            if (value == null) throw new InvalidDataException("Weight-alert settings could not be cloned.");
            return value;
        }

        public void Validate(bool requireMailTransport = false)
        {
            if (Version != 1) throw new ArgumentException("Unsupported weight-alert settings version.");
            if (ThresholdPercent <= 0 || ThresholdPercent > 100) throw new ArgumentException("Weight warning threshold must be > 0 and <= 100 percent.");
            if (RearmPercent < 0 || RearmPercent >= ThresholdPercent) throw new ArgumentException("Re-arm percentage must be >= 0 and below the warning threshold.");
            if (PollSeconds < 2 || PollSeconds > 60) throw new ArgumentException("Weight polling must be between 2 and 60 seconds.");
            if (CooldownMinutes < 1 || CooldownMinutes > 1440) throw new ArgumentException("Weight-alert cooldown must be between 1 minute and 24 hours.");
            if (SmtpPort < 1 || SmtpPort > 65535) throw new ArgumentException("SMTP port is invalid.");
            if (SmtpHost != null && SmtpHost.Length > 255) throw new ArgumentException("SMTP host is too long.");
            if (SmtpUser != null && SmtpUser.Length > 320) throw new ArgumentException("SMTP username is too long.");
            if (ProtectedSmtpPassword != null && ProtectedSmtpPassword.Length > 8192) throw new ArgumentException("Protected SMTP password is too long.");
            if (SubjectPrefix != null && SubjectPrefix.Length > 120) throw new ArgumentException("Mail subject prefix is too long.");
            if (AutoCartThresholdPercent <= 0 || AutoCartThresholdPercent > 100) throw new ArgumentException("Cart-maintenance threshold must be > 0 and <= 100 percent.");
            if (AutoCartRearmPercent < 0 || AutoCartRearmPercent >= AutoCartThresholdPercent) throw new ArgumentException("Cart-maintenance re-arm percentage must be >= 0 and below its threshold.");
            if (AutobattleStopKey < 8 || AutobattleStopKey > 254)
                throw new ArgumentException("Autobattle STOP hotkey is invalid.");
            if (InventoryKey < 8 || InventoryKey > 254 || CartKey < 8 || CartKey > 254)
                throw new ArgumentException("Inventory/cart hotkeys are invalid.");
            if (AutoCartEnabled && !TransferUseItems && !TransferEquipItems && !TransferEtcItems)
                throw new ArgumentException("Enable at least one inventory category for automatic cart maintenance.");
            if (Enabled || requireMailTransport)
            {
                if (string.IsNullOrWhiteSpace(SmtpHost)) throw new ArgumentException("SMTP host is required while weight e-mail alerts are enabled.");
                ParseAddress(FromAddress, "From address");
                ParseAddress(ToAddress, "Recipient address");
            }
        }

        private static string HotkeyText(bool ctrl, bool alt, bool shift, int key)
        {
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            Keys parsed = (Keys)key;
            int code = (int)parsed;
            parts.Add(code >= (int)Keys.D0 && code <= (int)Keys.D9
                ? (code - (int)Keys.D0).ToString(CultureInfo.InvariantCulture)
                : parsed.ToString());
            return string.Join("+", parts);
        }

        private static void ParseAddress(string text, string caption)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException(caption + " is required.");
            try { new MailAddress(text); }
            catch (FormatException ex) { throw new ArgumentException(caption + " is invalid: " + ex.Message); }
        }
    }

    public sealed class VanillaWeightAlertStore
    {
        private readonly string path;
        public VanillaWeightAlertStore()
        {
            VanillaAppData.InitializeAndMigrateLegacy(AppDomain.CurrentDomain.BaseDirectory);
            path = Path.Combine(VanillaAppData.RootDirectory, "weight-alerts.json");
        }
        public string FilePath { get { return path; } }

        public VanillaWeightAlertSettings Load()
        {
            if (!File.Exists(path)) return new VanillaWeightAlertSettings();
            var value = JsonConvert.DeserializeObject<VanillaWeightAlertSettings>(File.ReadAllText(path));
            if (value == null) throw new InvalidDataException("Weight-alert settings are empty.");
            value.Validate(false);
            return value;
        }

        public void Save(VanillaWeightAlertSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var copy = settings.Clone();
            copy.Validate(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(copy, Formatting.Indented));
            if (File.Exists(path))
            {
                string backup = path + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(temp, path, backup);
            }
            else File.Move(temp, path);
        }

        public string ProtectPassword(string clearText) { return string.IsNullOrEmpty(clearText) ? "" : VanillaSecretProtector.Protect(clearText); }
        public string UnprotectPassword(string protectedText) { return string.IsNullOrWhiteSpace(protectedText) ? "" : VanillaSecretProtector.Unprotect(protectedText); }
    }

    public sealed class VanillaWeightObservation
    {
        public int ProcessId { get; internal set; }
        public string CharacterName { get; internal set; }
        public uint? CurrentWeight { get; internal set; }
        public uint? MaxWeight { get; internal set; }
        public decimal? Percent { get; internal set; }
        public bool Verified { get; internal set; }
        public uint? CurrentCartWeight { get; internal set; }
        public uint? MaxCartWeight { get; internal set; }
        public decimal? CartPercent { get; internal set; }
        public bool CartVerified { get; internal set; }
        public string Build { get; internal set; }
        public string Error { get; internal set; }
    }

    public sealed class VanillaWeightAlertService : IDisposable
    {
        internal static VanillaWeightMailMode ResolveMailMode(bool globalMailEnabled, bool characterMailEnabled,
            bool globalCartEnabled, bool characterCartEnabled)
        {
            if (!globalMailEnabled || !characterMailEnabled) return VanillaWeightMailMode.None;
            return globalCartEnabled && characterCartEnabled
                ? VanillaWeightMailMode.CartMilestones
                : VanillaWeightMailMode.CarriedWeight;
        }
        internal static bool IsCombinedCapacityReached(VanillaWeightObservation observation)
        {
            return observation != null && observation.Verified && observation.CartVerified
                && observation.Percent.HasValue && observation.CartPercent.HasValue
                && VanillaWeightCartAutomation.IsFarmingComplete(
                    observation.CartPercent.Value, observation.Percent.Value);
        }

        // Resolve policy again immediately before dispatch. A queued completion must not
        // reuse Mail/Cart switches or SMTP settings captured before the user edited them.
        private bool TryGetAutomaticMailSettings(int processId, string accountId,
            VanillaWeightMailMode expectedMode, out VanillaWeightAlertSettings current)
        {
            current = null;
            lock (gate)
            {
                if (disposed || timer == null) return false;
                current = settings.Clone();
            }
            if (string.IsNullOrWhiteSpace(accountId)
                || !string.Equals(accountId, supervisor.ManagedAccountIdForProcess(processId),
                    StringComparison.OrdinalIgnoreCase)) return false;
            return ResolveMailMode(current.Enabled, supervisor.IsWeightEmailEnabledForProcess(processId),
                current.AutoCartEnabled, supervisor.IsCartMaintenanceEnabledForProcess(processId)) == expectedMode
                && MilestoneMailConfigured(current);
        }

        internal const int PrecisionCartRetrySeconds = 60;
        private readonly VanillaWeightAlertStore store;
        private readonly VanillaFleetMonitor fleetMonitor;
        private readonly VanillaReconnectSupervisor supervisor;
        private readonly VanillaWeightCartAutomation cartAutomation;
        private readonly object gate = new object();
        private readonly Dictionary<string, AlertState> states = new Dictionary<string, AlertState>(StringComparer.OrdinalIgnoreCase);
        private System.Threading.Timer timer;
        private VanillaWeightAlertSettings settings;
        private IReadOnlyList<VanillaWeightObservation> latest = new VanillaWeightObservation[0];
        private string status = "Weight alerts stopped.";
        private int polling;
        private bool disposed;

        public event System.Action<string> StatusChanged;
        public VanillaWeightAlertStore Store { get { return store; } }
        public bool IsRunning { get { lock (gate) return timer != null && !disposed; } }
        public string Status { get { lock (gate) return status; } }
        public IReadOnlyList<VanillaWeightObservation> Latest { get { lock (gate) return latest.ToArray(); } }
        public VanillaWeightAlertSettings Settings { get { lock (gate) return settings.Clone(); } }

        public VanillaWeightAlertService(string baseDirectory, VanillaFleetMonitor fleetMonitor, VanillaReconnectSupervisor supervisor)
        {
            if (fleetMonitor == null) throw new ArgumentNullException(nameof(fleetMonitor));
            if (supervisor == null) throw new ArgumentNullException(nameof(supervisor));
            store = new VanillaWeightAlertStore();
            this.fleetMonitor = fleetMonitor;
            this.supervisor = supervisor;
            cartAutomation = new VanillaWeightCartAutomation(fleetMonitor, supervisor);
            settings = store.Load();
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed || timer != null) return;
                timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(settings.PollSeconds));
                SetStatusLocked(settings.AutoCartEnabled ? "Weight manager started; automatic cart maintenance is enabled."
                    : settings.Enabled ? "Weight manager started; e-mail alerts are enabled."
                    : "Weight manager started. Verified weight memory remains visible while actions are disabled.");
            }
        }

        public void ApplySettings(VanillaWeightAlertSettings value, bool save)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            value.Validate(false);
            if (value.Enabled) value.Validate(true);
            lock (gate)
            {
                settings = value.Clone();
                if (save) store.Save(settings);
                if (timer != null) timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(settings.PollSeconds));
                SetStatusLocked(settings.AutoCartEnabled ? "Automatic cart maintenance enabled."
                    : settings.Enabled ? "Weight e-mail alerts enabled." : "Weight actions disabled.");
            }
        }

        internal static bool MilestoneMailConfigured(VanillaWeightAlertSettings value)
        {
            if (value == null) return false;
            try
            {
                var probe = value.Clone();
                // Transport validation is separate from authorization. Every automatic
                // dispatch must also pass the current shared and per-character Mail policy.
                probe.Enabled = true;
                probe.Validate(true);
                return true;
            }
            catch { return false; }
        }

        public void SendTest(VanillaWeightAlertSettings value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            value.Validate(true);
            SendMail(value, "SMTP test", "4RTools Vanilla weight-alert SMTP test succeeded at " + DateTimeOffset.Now.ToString("u", CultureInfo.InvariantCulture) + ".");
        }

        private void Poll()
        {
            if (disposed || Interlocked.Exchange(ref polling, 1) != 0) return;
            try
            {
                VanillaWeightAlertSettings current;
                lock (gate) current = settings.Clone();
                IReadOnlyList<VanillaWeightObservation> observations = fleetMonitor.Poll().Select(FromFleet).ToArray();
                lock (gate) latest = observations.ToArray();
                bool anyVerified = false;
                foreach (VanillaWeightObservation observation in observations)
                {
                    if (!observation.Verified || !observation.Percent.HasValue) continue;
                    anyVerified = true;

                    bool characterCart = supervisor.IsCartMaintenanceEnabledForProcess(observation.ProcessId);
                    bool characterMail = supervisor.IsWeightEmailEnabledForProcess(observation.ProcessId);
                    bool cartEnabled = current.AutoCartEnabled && characterCart;
                    VanillaWeightMailMode mailMode = ResolveMailMode(
                        current.Enabled, characterMail, current.AutoCartEnabled, characterCart);

                    if (cartEnabled)
                    {
                        ProcessFarmingMilestones(current, observation, mailMode == VanillaWeightMailMode.CartMilestones);
                        ProcessAutoCart(current, observation);
                    }
                    else if (mailMode == VanillaWeightMailMode.CarriedWeight)
                    {
                        ProcessObservation(current, observation);
                    }
                }
                if (!anyVerified) SetStatus("Weight manager waiting for verified CurrentWeight/MaxWeight mappings. " + ObservationSummary(observations));
                else if (!current.Enabled && !current.AutoCartEnabled)
                    SetStatus("Weight actions disabled. " + ObservationSummary(observations));
                else
                    SetStatus("Weight monitor active. " + ObservationSummary(observations));
            }
            catch (Exception ex) { SetStatus("Weight monitor error: " + ex.Message); }
            finally { Interlocked.Exchange(ref polling, 0); }
        }

        private static VanillaWeightObservation FromFleet(VanillaFleetClientInfo item)
        {
            decimal? percent = null, cartPercent = null;
            if (item != null && item.WeightVerified && item.CurrentWeight.HasValue && item.MaxWeight.HasValue && item.MaxWeight.Value > 0)
                percent = item.CurrentWeight.Value * 100m / item.MaxWeight.Value;
            if (item != null && item.CartWeightVerified && item.CurrentCartWeight.HasValue && item.MaxCartWeight.HasValue && item.MaxCartWeight.Value > 0)
                cartPercent = item.CurrentCartWeight.Value * 100m / item.MaxCartWeight.Value;
            return new VanillaWeightObservation
            {
                ProcessId = item?.ProcessId ?? 0, CharacterName = item?.CharacterName ?? "Vanilla MMO",
                CurrentWeight = item?.CurrentWeight, MaxWeight = item?.MaxWeight, Percent = percent,
                Verified = item != null && item.WeightVerified && percent.HasValue,
                CurrentCartWeight = item?.CurrentCartWeight, MaxCartWeight = item?.MaxCartWeight,
                CartPercent = cartPercent, CartVerified = item != null && item.CartWeightVerified && cartPercent.HasValue,
                Build = item?.Build, Error = item?.Error
            };
        }

        private void ProcessFarmingMilestones(VanillaWeightAlertSettings current, VanillaWeightObservation observation,
            bool mailEnabled)
        {
            if (!observation.CartVerified || !observation.CartPercent.HasValue) return;
            string accountId = supervisor.ManagedAccountIdForProcess(observation.ProcessId);
            if (string.IsNullOrWhiteSpace(accountId)) return;
            string key = "FARM:" + accountId;
            AlertState state;
            bool cartAtDoneThreshold = observation.CartPercent.Value >= VanillaWeightCartAutomation.FarmingDoneCartPercent;
            lock (gate)
            {
                if (!states.TryGetValue(key, out state)) states[key] = state = new AlertState();

                // Only the combined capacity threshold is a mail event. A full Cart
                // alone still leaves carried capacity available and must not send mail.
                if (!cartAtDoneThreshold)
                {
                    state.DoneNotified = false;
                    state.FarmingDone = false;
                    state.CompletionStopping = false;
                    state.NextMilestoneMailAt = DateTimeOffset.MinValue;
                    return;
                }
            }

            if (!IsCombinedCapacityReached(observation)) return;
            bool milestoneMail = mailEnabled && MilestoneMailConfigured(current);

            bool alreadyDone;
            lock (gate) alreadyDone = state.FarmingDone;
            alreadyDone = alreadyDone || supervisor.IsWeightCompletedHold(accountId);
            if (alreadyDone)
            {
                lock (gate) state.FarmingDone = true;
                if (milestoneMail) TrySendDoneMail(current, observation, accountId, state);
                return;
            }

            bool startStop;
            lock (gate)
            {
                startStop = !state.CompletionStopping && DateTimeOffset.UtcNow >= state.NextCompletionAttemptAt;
                if (startStop) state.CompletionStopping = true;
            }
            if (!startStop) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool stopped = cartAutomation.StopForFarmingCompletion(observation.ProcessId, current,
                        text => SetStatus(text));
                    if (!stopped)
                    {
                        lock (gate)
                            state.NextCompletionAttemptAt = DateTimeOffset.UtcNow.AddSeconds(
                                VanillaWeightCartAutomation.TransientCartRetrySeconds);
                        return;
                    }
                    lock (gate)
                    {
                        state.FarmingDone = true;
                        state.NextCompletionAttemptAt = DateTimeOffset.MinValue;
                    }
                    if (milestoneMail) TrySendDoneMail(current, observation, accountId, state);
                }
                finally
                {
                    lock (gate) state.CompletionStopping = false;
                }
            });
        }

        private void TrySendDoneMail(VanillaWeightAlertSettings current, VanillaWeightObservation observation,
            string accountId, AlertState state)
        {
            lock (gate)
            {
                if (state.DoneNotified || state.Sending || DateTimeOffset.UtcNow < state.NextMilestoneMailAt) return;
                state.Sending = true;
            }
            try
            {
                if (!IsCombinedCapacityReached(observation)
                    || !supervisor.IsWeightCompletedHold(accountId)
                    || !TryGetAutomaticMailSettings(observation.ProcessId, accountId,
                        VanillaWeightMailMode.CartMilestones, out current)) return;
                SendMail(current, "DONE: " + observation.CharacterName,
                    "DONE" + Environment.NewLine
                    + "Character: " + observation.CharacterName + Environment.NewLine
                    + "Cart: " + observation.CurrentCartWeight + " / " + observation.MaxCartWeight + " ("
                    + observation.CartPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%)" + Environment.NewLine
                    + "Carried weight: " + observation.CurrentWeight + " / " + observation.MaxWeight + " ("
                    + observation.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%)" + Environment.NewLine
                    + "Autobattle: OFF" + Environment.NewLine
                    + "Observed: " + DateTimeOffset.Now.ToString("u", CultureInfo.InvariantCulture));
                lock (gate)
                {
                    state.DoneNotified = true;
                    state.NextMilestoneMailAt = DateTimeOffset.MinValue;
                }
                VanillaDebugLog.Write("WEIGHT", "event=farming-done-email-sent accountId=" + accountId
                    + " pid=" + observation.ProcessId + " character='" + observation.CharacterName + "'.");
                SetStatus("DONE notification sent for " + observation.CharacterName + ".");
            }
            catch (Exception ex)
            {
                lock (gate) state.NextMilestoneMailAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                VanillaDebugLog.Write("WEIGHT", "event=farming-done-email-failed accountId=" + accountId
                    + " pid=" + observation.ProcessId + " reason='" + ex.Message + "'.");
            }
            finally { lock (gate) state.Sending = false; }
        }

        private void ProcessAutoCart(VanillaWeightAlertSettings current, VanillaWeightObservation observation)
        {
            string accountId = supervisor.ManagedAccountIdForProcess(observation.ProcessId);
            if (string.IsNullOrWhiteSpace(accountId)) return;
            string key = "CART:" + accountId;
            AlertState state;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (gate)
            {
                if (!states.TryGetValue(key, out state)) states[key] = state = new AlertState();
                if (observation.Percent.Value <= current.AutoCartRearmPercent)
                {
                    state.CartArmed = true;
                    state.NextCartAttemptAt = DateTimeOffset.MinValue;
                }
                if (observation.CartVerified && observation.CartPercent.HasValue
                    && VanillaWeightCartAutomation.IsFarmingComplete(
                        observation.CartPercent.Value, observation.Percent.Value)) return;
                if (observation.CartVerified && observation.CartPercent.HasValue
                    && observation.CartPercent.Value >= VanillaWeightCartAutomation.CartFullPercent) return;
                if (!current.AutoCartEnabled || observation.Percent.Value < current.AutoCartThresholdPercent
                    || !state.CartArmed || state.CartRunning || state.ManualHold || now < state.NextCartAttemptAt
                    || supervisor.IsWeightManualHold(accountId)) return;
                state.CartRunning = true;
                state.CartArmed = false;
                state.NextCartAttemptAt = DateTimeOffset.MinValue;
            }
            VanillaDebugLog.Write("WEIGHT", "event=cart-request trigger=automatic-threshold accountId=" + accountId
                + " pid=" + observation.ProcessId + " character='" + observation.CharacterName
                + "' percent=" + observation.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture) + ".");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    SetStatus("Weight threshold reached for " + observation.CharacterName + "; starting UI-only cart maintenance.");
                    VanillaWeightCartResult result = cartAutomation.Run(observation.ProcessId, current, text => SetStatus(text), "automatic-threshold");
                    lock (gate)
                    {
                        state.ManualHold = result.RequiresManualIntervention;
                        if (result.RetryLater && !result.RequiresManualIntervention)
                        {
                            int retrySeconds = result.RetryAfterSeconds > 0
                                ? result.RetryAfterSeconds : VanillaWeightCartAutomation.TransientCartRetrySeconds;
                            state.CartArmed = true;
                            state.NextCartAttemptAt = DateTimeOffset.UtcNow.AddSeconds(retrySeconds);
                        }
                        else if (result.Deferred)
                        {
                            state.CartArmed = true;
                            state.NextCartAttemptAt = DateTimeOffset.MinValue;
                        }
                        else if (result.StoppedForCartSafety && !result.CartFull && !result.RequiresManualIntervention)
                        {
                            // Near-full Cart: keep farming, then retry after a bounded delay so newly
                            // acquired Peco Feathers can finish a remainder that Mastela cannot fill.
                            state.CartArmed = true;
                            state.NextCartAttemptAt = DateTimeOffset.UtcNow.AddSeconds(PrecisionCartRetrySeconds);
                        }
                    }
                    SetStatus(result.Message);
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        state.CartArmed = true;
                        state.NextCartAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(PrecisionCartRetrySeconds, current.PollSeconds * 2));
                    }
                    VanillaDebugLog.Write("WEIGHT", "event=cart-failed trigger=automatic-threshold accountId=" + accountId
                        + " pid=" + observation.ProcessId + " reason='" + ex.Message + "'.");
                    SetStatus("Automatic cart maintenance failed for " + observation.CharacterName + ": " + ex.Message);
                }
                finally { lock (gate) state.CartRunning = false; }
            });
        }

        internal string RunCartNow(string accountId)
        {
            int pid;
            VanillaReconnectAccount account;
            string reason;
            if (!supervisor.TryResolveOnlineManagedCharacter(accountId, out pid, out account, out reason))
                throw new InvalidOperationException(reason);
            if (!account.EffectiveCartMaintenanceEnabled)
                throw new InvalidOperationException("Cart maintenance is disabled for this character.");

            VanillaWeightAlertSettings current;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(VanillaWeightAlertService));
                current = settings.Clone();
            }
            if (!current.TransferUseItems && !current.TransferEquipItems && !current.TransferEtcItems)
                throw new InvalidOperationException("Select at least one inventory category in the Weight tab first.");

            string key = "CART:" + account.Id;
            AlertState state;
            lock (gate)
            {
                if (!states.TryGetValue(key, out state)) states[key] = state = new AlertState();
                if (state.CartRunning) throw new InvalidOperationException("Cart maintenance is already running for this character.");
                if (state.ManualHold || supervisor.IsWeightManualHold(account.Id))
                    throw new InvalidOperationException("This character is on manual Cart hold; clear it after inspecting/emptying the Cart.");
                state.CartRunning = true;
                state.CartArmed = false;
            }

            VanillaDebugLog.Write("WEIGHT", "event=cart-manual-request account='" + account.Label + "' accountId="
                + account.Id + " pid=" + pid + ".");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    SetStatus("Manual TESTS request: starting UI-only Cart maintenance for " + account.Label + ".");
                    VanillaWeightCartResult result = cartAutomation.Run(pid, current, text => SetStatus(text), "manual-test");
                    lock (gate)
                    {
                        state.ManualHold = result.RequiresManualIntervention;
                        if (result.RetryLater && !result.RequiresManualIntervention)
                        {
                            state.CartArmed = true;
                            state.NextCartAttemptAt = DateTimeOffset.UtcNow.AddSeconds(
                                result.RetryAfterSeconds > 0 ? result.RetryAfterSeconds
                                    : VanillaWeightCartAutomation.TransientCartRetrySeconds);
                        }
                        else if (result.Deferred)
                        {
                            state.CartArmed = true;
                            state.NextCartAttemptAt = DateTimeOffset.MinValue;
                        }
                    }
                    SetStatus(result.Message);
                }
                catch (Exception ex)
                {
                    lock (gate) state.CartArmed = true;
                    VanillaDebugLog.Write("WEIGHT", "event=cart-failed trigger=manual-test accountId=" + account.Id
                        + " pid=" + pid + " reason='" + ex.Message + "'.");
                    SetStatus("Manual Cart maintenance failed for " + account.Label + ": " + ex.Message);
                }
                finally { lock (gate) state.CartRunning = false; }
            });
            return "Weight/Cart clean test queued for " + account.Label + " (PID " + pid + ").";
        }

        public void ClearManualHolds()
        {
            lock (gate) foreach (AlertState state in states.Values)
            {
                state.ManualHold = false;
                state.CartArmed = true;
                state.FarmingDone = false;
                state.CompletionStopping = false;
                state.DoneNotified = false;
                state.NextCartAttemptAt = DateTimeOffset.MinValue;
                state.NextCompletionAttemptAt = DateTimeOffset.MinValue;
                state.NextMilestoneMailAt = DateTimeOffset.MinValue;
            }
            supervisor.ClearWeightManualHolds();
            SetStatus("Weight manual holds cleared. Automatic cart maintenance can run again after the threshold is reached.");
        }

        private void ProcessObservation(VanillaWeightAlertSettings current, VanillaWeightObservation observation)
        {
            string accountId = supervisor.ManagedAccountIdForProcess(observation.ProcessId);
            string key = string.IsNullOrWhiteSpace(accountId)
                ? "PID:" + observation.ProcessId.ToString(CultureInfo.InvariantCulture)
                : "MAIL:" + accountId;
            AlertState state;
            lock (gate)
            {
                if (!states.TryGetValue(key, out state)) states[key] = state = new AlertState();
                if (observation.Percent.Value <= current.RearmPercent) state.Armed = true;
            }
            if (observation.Percent.Value < current.ThresholdPercent) return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (gate)
            {
                if (!state.Armed || state.Sending || now < state.NextAttemptAt || (state.LastSentAt.HasValue && now - state.LastSentAt.Value < TimeSpan.FromMinutes(current.CooldownMinutes))) return;
                state.Sending = true;
            }
            try
            {
                if (!TryGetAutomaticMailSettings(observation.ProcessId, accountId,
                    VanillaWeightMailMode.CarriedWeight, out current)
                    || observation.Percent.Value < current.ThresholdPercent) return;
                string percent = observation.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture);
                string subject = "Weight warning: " + observation.CharacterName + " " + percent + "%";
                string body = "Character: " + observation.CharacterName + Environment.NewLine
                    + "Weight: " + observation.CurrentWeight + " / " + observation.MaxWeight + " (" + percent + "%)" + Environment.NewLine
                    + "Configured warning threshold: " + current.ThresholdPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + Environment.NewLine
                    + "Re-arm below: " + current.RearmPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + Environment.NewLine
                    + "Observed: " + DateTimeOffset.Now.ToString("u", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Build: " + observation.Build;
                SendMail(current, subject, body);
                lock (gate) { state.Armed = false; state.LastSentAt = now; state.NextAttemptAt = DateTimeOffset.MinValue; }
                VanillaDebugLog.Write("WEIGHT", "event=weight-email-sent accountId=" + (accountId ?? "unknown")
                    + " pid=" + observation.ProcessId + " character='" + observation.CharacterName + "' percent=" + percent + ".");
                SetStatus("Weight warning e-mail sent for " + observation.CharacterName + " at " + percent + "%.");
            }
            catch (Exception ex)
            {
                lock (gate) state.NextAttemptAt = now + TimeSpan.FromMinutes(Math.Min(5, current.CooldownMinutes));
                VanillaDebugLog.Write("WEIGHT", "event=weight-email-failed accountId=" + (accountId ?? "unknown")
                    + " pid=" + observation.ProcessId + " character='" + observation.CharacterName + "' reason='" + ex.Message + "'.");
                SetStatus("Weight warning e-mail failed for " + observation.CharacterName + ": " + ex.Message);
            }
            finally { lock (gate) state.Sending = false; }
        }

        private void SendMail(VanillaWeightAlertSettings value, string subject, string body)
        {
            string password = store.UnprotectPassword(value.ProtectedSmtpPassword);
            try
            {
                using (var message = new MailMessage(new MailAddress(value.FromAddress), new MailAddress(value.ToAddress)))
                using (var client = new SmtpClient(value.SmtpHost, value.SmtpPort))
                {
                    message.Subject = (value.SubjectPrefix ?? "").Trim() + (string.IsNullOrWhiteSpace(value.SubjectPrefix) ? "" : " ") + subject;
                    message.Body = body;
                    client.EnableSsl = value.UseSsl;
                    client.DeliveryMethod = SmtpDeliveryMethod.Network;
                    client.UseDefaultCredentials = false;
                    if (!string.IsNullOrWhiteSpace(value.SmtpUser)) client.Credentials = new NetworkCredential(value.SmtpUser, password);
                    client.Send(message);
                }
            }
            finally { password = null; }
        }

        private static string ObservationSummary(IReadOnlyList<VanillaWeightObservation> observations)
        {
            if (observations == null || observations.Count == 0) return "No Vanilla clients running.";
            return string.Join(" | ", observations.Select(item =>
            {
                string carried = item.Verified && item.Percent.HasValue
                    ? item.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%"
                    : "weight unavailable";
                string cart = item.CartVerified && item.CartPercent.HasValue
                    ? "Cart " + item.CartPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%"
                    : "Cart unavailable";
                return item.CharacterName + " " + carried + ", " + cart;
            }));
        }

        private void SetStatus(string value)
        {
            System.Action<string> handler;
            lock (gate) { SetStatusLocked(value); handler = StatusChanged; }
            try { handler?.Invoke(value); } catch { }
        }
        private void SetStatusLocked(string value) { status = value ?? ""; }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                timer?.Dispose(); timer = null;
            }
        }

        private sealed class AlertState
        {
            public bool Armed = true;
            public bool Sending;
            public bool CartArmed = true;
            public bool CartRunning;
            public bool ManualHold;
            public bool DoneNotified;
            public bool FarmingDone;
            public bool CompletionStopping;
            public DateTimeOffset? LastSentAt;
            public DateTimeOffset NextAttemptAt = DateTimeOffset.MinValue;
            public DateTimeOffset NextCartAttemptAt = DateTimeOffset.MinValue;
            public DateTimeOffset NextCompletionAttemptAt = DateTimeOffset.MinValue;
            public DateTimeOffset NextMilestoneMailAt = DateTimeOffset.MinValue;
        }
    }
}
