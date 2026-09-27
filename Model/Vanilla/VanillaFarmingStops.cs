using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace _4RTools.Model.Vanilla
{
    public enum VanillaFarmingStopKind { Emergency, Completed }

    public sealed class VanillaFarmingStopNotice
    {
        public string EventId { get; set; }
        public VanillaFarmingStopKind Kind { get; set; }
        public string AccountId { get; set; }
        public string UserName { get; set; }
        public string CharacterName { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public string Evidence { get; set; }
        public string Detail { get; set; }
        public bool HoldActive { get; set; } = true;
        public bool CloseRequested { get; set; }
        public string CloseState { get; set; } = "not requested";
        public string CloseDetail { get; set; }
        public bool EmailRequested { get; set; }
        public string EmailState { get; set; } = "not requested";
        public string EmailDetail { get; set; }
        public DateTimeOffset? NextEmailAttemptAt { get; set; }
        public string EmailAttemptId { get; set; }
        [JsonIgnore] internal int ProcessId, WeightGeneration, ClosePolicyGeneration;
        [JsonIgnore] internal Guid Session;
        [JsonIgnore] internal DateTime ProcessCreated;
        [JsonIgnore] internal bool AwaitingInputCleanup, ClosePending;
        public VanillaFarmingStopNotice Clone() { return (VanillaFarmingStopNotice)MemberwiseClone(); }
        [JsonIgnore] public string FullDetail
        {
            get
            {
                return ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + ": " + Detail + (string.IsNullOrWhiteSpace(Evidence) ? "" : " " + Evidence)
                    + " Close: " + CloseState + (string.IsNullOrWhiteSpace(CloseDetail) ? "." : " (" + CloseDetail + ").")
                    + " Email: " + EmailState + (string.IsNullOrWhiteSpace(EmailDetail) ? "." : " (" + EmailDetail + ").")
                    + (EmailState == "failed" && NextEmailAttemptAt.HasValue ? " Retry after "
                        + NextEmailAttemptAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "." : "");
            }
        }
    }

    public sealed class VanillaFarmingStopStatus
    {
        public string AccountId { get; set; }
        public string UserName { get; set; }
        public string CharacterName { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public int? ProcessId { get; set; }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private sealed class FarmingStopDocument
        {
            public int Version { get; set; } = 1;
            public List<VanillaFarmingStopNotice> Stops { get; set; } = new List<VanillaFarmingStopNotice>();
        }
        private readonly Dictionary<string, VanillaFarmingStopNotice> farmingStops = new Dictionary<string, VanillaFarmingStopNotice>(StringComparer.Ordinal);
        private string farmingStopPath, farmingStopStoreFailure;
        private bool closeFarmingCompletionClients, sendFarmingCompletionEmail;
        private int farmingCompletionPolicyGeneration;

        private void InitializeFarmingStops()
        {
            farmingStopPath = Path.Combine(Path.GetDirectoryName(store.FilePath), "farming-stops.json");
            try
            {
                if (!File.Exists(farmingStopPath)) return;
                var document = JsonConvert.DeserializeObject<FarmingStopDocument>(File.ReadAllText(farmingStopPath));
                if (document == null || document.Version != 1 || document.Stops == null)
                    throw new InvalidDataException("Farming stop records are empty or unsupported.");
                foreach (var stop in document.Stops)
                {
                    Guid eventId;
                    if (stop == null || !Guid.TryParse(stop.EventId, out eventId)
                        || !Enum.IsDefined(typeof(VanillaFarmingStopKind), stop.Kind)
                        || VanillaCharacterRoster.Key(stop.UserName, stop.CharacterName) == null
                        || string.IsNullOrWhiteSpace(stop.AccountId) || string.IsNullOrWhiteSpace(stop.Detail)
                        || stop.ObservedAt == default(DateTimeOffset) || farmingStops.ContainsKey(stop.EventId))
                        throw new InvalidDataException("Farming stop record identity or evidence is invalid.");
                    if (stop.CloseState == "pending")
                    { stop.CloseState = "cancelled"; stop.CloseDetail = "Application restarted before exit was confirmed; hold retained, no stale close retried."; }
                    if (stop.EmailState == "sending")
                    { stop.EmailState = "uncertain"; stop.EmailDetail = "Application stopped during email delivery; automatic resend withheld to avoid a duplicate."; }
                    farmingStops.Add(stop.EventId, stop);
                }
            }
            catch (Exception ex)
            {
                farmingStopStoreFailure = "Farming stop records could not be read: " + ex.Message + ". Automatic farming remains held until explicit Weight hold clear.";
                VanillaDebugLog.Write("WEIGHT", farmingStopStoreFailure);
            }
        }

        public void SetFarmingCompletionPolicy(bool closeClient, bool sendEmail = false)
        {
            lock (gate)
            {
                sendFarmingCompletionEmail = sendEmail;
                if (closeFarmingCompletionClients == closeClient) return;
                closeFarmingCompletionClients = closeClient;
                farmingCompletionPolicyGeneration++;
            }
        }

        internal bool FarmingCompletionHeld(VanillaReconnectAccount account)
        {
            lock (gate)
            {
                return account != null && (farmingStopStoreFailure != null || weightCompletedHolds.Contains(account.Id)
                    || farmingStops.Values.Any(stop => stop.HoldActive && stop.Kind == VanillaFarmingStopKind.Completed && StopMatches(stop, account)));
            }
        }

        private static bool StopMatches(VanillaFarmingStopNotice stop, VanillaReconnectAccount account)
        {
            return account != null && stop.AccountId == account.Id
                && VanillaCharacterRoster.Key(stop.UserName, stop.CharacterName) == VanillaCharacterRoster.Key(account);
        }

        private void RestoreFarmingCompletionHoldsLocked()
        {
            foreach (string id in weightCompletedHolds.ToArray())
                if (farmingStops.Values.Any(stop => stop.Kind == VanillaFarmingStopKind.Completed && stop.AccountId == id)
                    && !farmingStops.Values.Any(stop => stop.Kind == VanillaFarmingStopKind.Completed && stop.HoldActive
                        && settings.Accounts.Any(account => account.Id == id && StopMatches(stop, account))))
                    weightCompletedHolds.Remove(id);
            foreach (var account in settings.Accounts)
                if (farmingStopStoreFailure != null || farmingStops.Values.Any(stop => stop.HoldActive
                    && stop.Kind == VanillaFarmingStopKind.Completed && StopMatches(stop, account)))
                    weightCompletedHolds.Add(account.Id);
        }

        public IReadOnlyList<VanillaFarmingStopStatus> FarmingStopStatuses()
        {
            lock (gate)
            {
                var result = new List<VanillaFarmingStopStatus>();
                foreach (var account in settings.Accounts)
                {
                    string key = VanillaCharacterRoster.Key(account);
                    FarmingEmergencyHold emergency = null;
                    bool emergencyHeld = key != null && farmingEmergencyHolds.TryGetValue(key, out emergency);
                    var stop = farmingStops.Values.Where(value => (value.HoldActive || (emergencyHeld && value.EventId == emergency.EventId)) && StopMatches(value, account)
                        && (emergencyHeld ? value.Kind == VanillaFarmingStopKind.Emergency : value.Kind == VanillaFarmingStopKind.Completed))
                        .OrderByDescending(value => value.ObservedAt).FirstOrDefault();
                    if (!emergencyHeld && stop == null && farmingStopStoreFailure == null) continue;
                    Runtime runtime; runtimes.TryGetValue(account.Id, out runtime);
                    string detail = stop?.FullDetail ?? (emergencyHeld
                        ? farmingEmergencyHolds[key].ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                            + ": " + farmingEmergencyHolds[key].Detail + " Email: not requested (legacy stop)."
                        : farmingStopStoreFailure);
                    result.Add(new VanillaFarmingStopStatus
                    {
                        AccountId = account.Id, UserName = account.UserName, CharacterName = account.CharacterName,
                        Status = emergencyHeld ? "Emergency" : stop == null ? "Error" : "Completed", Detail = detail,
                        ProcessId = runtime?.ProcessId
                    });
                }
                return result;
            }
        }

        public string FarmingStopDetail(string accountId)
        {
            lock (gate) return FarmingStopStatuses().FirstOrDefault(stop => stop.AccountId == accountId)?.Detail
                ?? "Farming complete: Cart >=99% and carried weight >=50%; Autobattle STOP verified; explicit Weight hold clear required.";
        }

        private void WriteFarmingStopsLocked(IEnumerable<VanillaFarmingStopNotice> values)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(farmingStopPath));
            var retained = values.Where(stop => stop.HoldActive || stop.EmailState == "sending")
                .Concat(values.Where(stop => !stop.HoldActive && stop.EmailState != "sending")
                    .OrderByDescending(stop => stop.ObservedAt).Take(100)).Distinct().ToList();
            string temporary = farmingStopPath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(new FarmingStopDocument { Stops = retained }, Formatting.Indented));
            if (File.Exists(farmingStopPath)) File.Replace(temporary, farmingStopPath, farmingStopPath + ".bak");
            else File.Move(temporary, farmingStopPath);
        }

        private bool PersistFarmingStopsLocked()
        {
            try { WriteFarmingStopsLocked(farmingStops.Values); return true; }
            catch (Exception ex)
            {
                string failure = "Farming stop records could not be saved: " + ex.Message;
                VanillaDebugLog.Write("WEIGHT", failure);
                return false;
            }
        }

        private VanillaFarmingStopNotice RecordEmergencyNoticeLocked(Runtime runtime, FarmingEmergencyHold hold, bool emailRequested)
        {
            VanillaFarmingStopNotice existing;
            if (hold.EventId != null && farmingStops.TryGetValue(hold.EventId, out existing))
            {
                existing.CloseState = "pending"; existing.CloseDetail = "Fresh emergency evidence confirmed after restart.";
                PersistFarmingStopsLocked();
                return existing;
            }
            var notice = new VanillaFarmingStopNotice
            {
                EventId = hold.EventId ?? Guid.NewGuid().ToString("N"), Kind = VanillaFarmingStopKind.Emergency,
                AccountId = runtime.Account.Id, UserName = hold.UserName, CharacterName = hold.CharacterName,
                ObservedAt = hold.ObservedAt, Evidence = hold.Ratios + "; limits: " + hold.Conditions,
                Detail = "Emergency farming STOP; explicit emergency hold clear required.", CloseRequested = true,
                CloseState = "pending", EmailRequested = emailRequested, EmailState = emailRequested ? "pending" : "not requested"
            };
            hold.EventId = notice.EventId;
            farmingStops.Add(notice.EventId, notice);
            // The emergency hold and immediate close remain authoritative even
            // if supplemental notification persistence fails.
            PersistFarmingStopsLocked();
            return notice;
        }

        private void UpdateEmergencyNoticeLocked(FarmingEmergencyHold hold, string closeResult)
        {
            VanillaFarmingStopNotice notice;
            if (hold.EventId == null || !farmingStops.TryGetValue(hold.EventId, out notice)) return;
            notice.CloseState = hold.CloseState;
            notice.CloseDetail = closeResult;
            PersistFarmingStopsLocked();
        }

        internal IReadOnlyList<VanillaFarmingStopNotice> PendingFarmingStopEmails()
        {
            lock (gate)
            {
                if (disposed || farmingStopStoreFailure != null) return new VanillaFarmingStopNotice[0];
                DateTimeOffset now = restartEnvironment.UtcNow;
                return farmingStops.Values.Where(stop => stop.HoldActive && stop.EmailRequested
                    && (stop.EmailState == "pending" || stop.EmailState == "failed")
                    && (!stop.NextEmailAttemptAt.HasValue || stop.NextEmailAttemptAt <= now)
                    && !stop.AwaitingInputCleanup && stop.CloseState != "pending")
                    .Select(stop => stop.Clone()).ToArray();
            }
        }

        internal bool TryReserveFarmingStopEmail(string eventId, out VanillaFarmingStopNotice notice)
        {
            lock (gate)
            {
                notice = null;
                if (!PendingFarmingStopEmails().Any(entry => entry.EventId == eventId)) return false;
                var stop = farmingStops[eventId];
                var candidate = stop.Clone(); candidate.EmailState = "sending"; candidate.EmailAttemptId = Guid.NewGuid().ToString("N");
                candidate.EmailDetail = "Delivery in progress.";
                try { WriteFarmingStopsLocked(farmingStops.Values.Select(item => item == stop ? candidate : item)); }
                catch (Exception ex) { VanillaDebugLog.Write("MAIL", "Stop email reservation could not be saved: " + ex.Message); return false; }
                stop.EmailState = candidate.EmailState; stop.EmailAttemptId = candidate.EmailAttemptId; stop.EmailDetail = candidate.EmailDetail;
                notice = stop.Clone(); return true;
            }
        }

        internal void CompleteFarmingStopEmail(string eventId, string attemptId, bool sent, string detail)
        {
            lock (gate)
            {
                VanillaFarmingStopNotice stop;
                if (!farmingStops.TryGetValue(eventId, out stop) || stop.EmailState != "sending" || stop.EmailAttemptId != attemptId) return;
                stop.EmailState = sent ? "sent" : "failed"; stop.EmailDetail = detail;
                stop.NextEmailAttemptAt = sent ? (DateTimeOffset?)null : restartEnvironment.UtcNow.AddMinutes(5);
                PersistFarmingStopsLocked();
            }
            RaiseUpdated();
        }

        internal bool IsFarmingStopEmailAttemptCurrent(string eventId, string attemptId)
        {
            lock (gate)
            {
                VanillaFarmingStopNotice stop;
                return !disposed && farmingStops.TryGetValue(eventId, out stop) && stop.HoldActive
                    && stop.EmailState == "sending" && stop.EmailAttemptId == attemptId;
            }
        }

        private void ClearFarmingStopRecordsLocked(VanillaFarmingStopKind kind)
        {
            if (farmingStops.Values.Any(stop => stop.Kind == kind && stop.HoldActive && (stop.AwaitingInputCleanup || stop.ClosePending)))
                throw new InvalidOperationException("The farming stop is still completing; wait for input and close cleanup before clearing its hold.");
            var copies = farmingStops.Values.Select(stop => stop.Clone()).ToArray();
            foreach (var stop in copies.Where(stop => stop.Kind == kind && stop.HoldActive))
            {
                stop.HoldActive = false;
                if (stop.EmailState == "pending" || stop.EmailState == "failed")
                { stop.EmailState = "cancelled"; stop.EmailDetail = "Explicit hold-clear request cancelled pending delivery."; }
            }
            WriteFarmingStopsLocked(copies);
            foreach (var copy in copies)
            {
                // Close workers retain the record identity. Clearing another kind
                // must never replace or detach an in-flight incident.
                var current = farmingStops[copy.EventId];
                current.HoldActive = copy.HoldActive;
                current.EmailState = copy.EmailState; current.EmailDetail = copy.EmailDetail;
            }
            if (kind == VanillaFarmingStopKind.Completed) farmingStopStoreFailure = null;
        }

        internal void CompleteWeightFarmingDone(VanillaWeightMaintenanceToken token, string detail, VanillaFleetClientInfo evidence)
        {
            if (token == null) return;
            lock (gate)
            {
                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime)) throw new OperationCanceledException("Completion account changed.");
                CompleteWeightFarmingDone(token, detail, evidence, closeFarmingCompletionClients,
                    sendFarmingCompletionEmail && runtime.Account.EffectiveWeightEmailEnabled);
            }
        }

        internal void CompleteWeightFarmingDone(VanillaWeightMaintenanceToken token, string detail,
            VanillaFleetClientInfo evidence, bool closeClient, bool emailRequested)
        {
            if (token == null) return;
            lock (gate)
            {
                if (WeightMaintenanceCancelled(token)) throw new OperationCanceledException("Farming completion ownership changed.");
                Runtime runtime = runtimes[token.AccountId];
                if (farmingStops.Values.Any(entry => entry.Kind == VanillaFarmingStopKind.Completed && entry.HoldActive && StopMatches(entry, runtime.Account))) return;
                if (evidence != null && (!VanillaWeightCartAutomation.HasFreshQuantityWeights(evidence, runtime.Account, token.ProcessId, restartEnvironment.UtcNow)
                    || !VanillaWeightCartAutomation.IsFarmingComplete(evidence.CartWeightPercent.Value, evidence.WeightPercent.Value)))
                    throw new InvalidOperationException("Fresh combined farming-completion evidence is unavailable.");
                if (closeClient && evidence == null) throw new InvalidOperationException("Closing a completed client requires fresh resource evidence.");
                var stop = new VanillaFarmingStopNotice
                {
                    EventId = Guid.NewGuid().ToString("N"), Kind = VanillaFarmingStopKind.Completed,
                    AccountId = runtime.Account.Id, UserName = runtime.Account.UserName, CharacterName = runtime.Account.CharacterName,
                    ObservedAt = evidence?.Snapshot.SampledAtUtc ?? restartEnvironment.UtcNow,
                    Detail = detail ?? "Farming complete: Cart >=99% AND carried weight >=50%; Autobattle STOP verified.",
                    Evidence = evidence == null ? "Verified completion STOP." : "Cart " + evidence.CurrentCartWeight + "/" + evidence.MaxCartWeight
                        + " (" + evidence.CartWeightPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%), carried weight "
                        + evidence.CurrentWeight + "/" + evidence.MaxWeight + " (" + evidence.WeightPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%).",
                    CloseRequested = closeClient, CloseState = closeClient ? "pending" : "not requested",
                    EmailRequested = emailRequested, EmailState = emailRequested ? "pending" : "not requested",
                    ProcessId = token.ProcessId, WeightGeneration = token.Generation, Session = evidence?.Identity.Session ?? Guid.Empty,
                    ClosePolicyGeneration = farmingCompletionPolicyGeneration,
                    AwaitingInputCleanup = true
                };
                if (closeClient)
                {
                    try { stop.ProcessCreated = restartEnvironment.GetStartTimeUtc(token.ProcessId); }
                    catch (Exception ex) { stop.CloseState = "failed"; stop.CloseDetail = "Client identity unavailable; no close: " + ex.Message; }
                }
                farmingStops.Add(stop.EventId, stop); weightCompletedHolds.Add(token.AccountId); weightManualHolds.Remove(token.AccountId);
                runtime.MovementWatchdog.Reset(); runtime.MovementRecoveryPending = false; runtime.NonMinimizedSince = null;
                if (!PersistFarmingStopsLocked())
                { stop.CloseState = "failed"; stop.CloseDetail = "Durable hold storage failed; no client close authorized."; }
                SetStage(runtime, VanillaReconnectStage.Stopped, stop.FullDetail);
            }
            RaiseUpdated();
        }

        internal void FinishWeightFarmingCompletion(VanillaWeightMaintenanceToken token)
        {
            if (token == null) return;
            lock (gate)
            {
                var stop = farmingStops.Values.FirstOrDefault(item => item.HoldActive && item.Kind == VanillaFarmingStopKind.Completed
                    && item.AccountId == token.AccountId && item.WeightGeneration == token.Generation && item.AwaitingInputCleanup);
                if (stop == null) return;
                stop.AwaitingInputCleanup = false;
                Runtime runtime;
                bool owned = runtimes.TryGetValue(token.AccountId, out runtime) && runtime.ProcessId == token.ProcessId
                    && token.Generation == weightMaintenanceGeneration;
                if (owned) runtime.ScriptRunning = runtime.RecoveryOwned = false;
                if (stop.CloseState == "pending")
                {
                    if (!owned || !running || disposed || !closeFarmingCompletionClients
                        || stop.ClosePolicyGeneration != farmingCompletionPolicyGeneration || FarmingEmergencyHeld(runtime))
                    { stop.CloseState = "cancelled"; stop.CloseDetail = "Completion close cancelled by changed ownership or policy; hold retained."; PersistFarmingStopsLocked(); }
                    else QueueFarmingCompletionCloseLocked(runtime, stop);
                }
            }
            RaiseUpdated();
        }

        private void QueueFarmingCompletionCloseLocked(Runtime runtime, VanillaFarmingStopNotice stop)
        {
            int diagnostic = diagnosticGeneration, policy = stop.ClosePolicyGeneration, operation = runtime.ResumeOperationGeneration;
            bool issued = false;
            stop.ClosePending = true;
            runtime.ScriptRunning = runtime.ClosingForRecovery = true;
            Func<bool> cancelled = () =>
            {
                lock (gate)
                {
                    VanillaFarmingStopNotice current;
                    if (!farmingStops.TryGetValue(stop.EventId, out current) || !ReferenceEquals(current, stop) || !stop.HoldActive) return true;
                    if (issued) return false;
                    Runtime owner;
                    if (disposed || !running || diagnostic != diagnosticGeneration || policy != farmingCompletionPolicyGeneration
                        || !closeFarmingCompletionClients || !runtimes.TryGetValue(stop.AccountId, out owner) || !ReferenceEquals(owner, runtime)
                        || owner.ProcessId != stop.ProcessId || owner.ResumeOperationGeneration != operation
                        || !owner.ScriptRunning || !owner.ClosingForRecovery
                        || !owner.Account.Enabled || !owner.Account.EffectiveCartMaintenanceEnabled || !StopMatches(stop, owner.Account)
                        || FarmingEmergencyHeld(owner)) return true;
                    VanillaCharacterIdentity identity = CurrentCharacter(stop.ProcessId);
                    return identity == null || identity.Session != stop.Session || !VanillaCharacterRoster.Matches(owner.Account, identity, restartEnvironment.UtcNow);
                }
            };
            System.Action work = () =>
            {
                bool exited = false; string error = null;
                try
                {
                    if (cancelled()) throw new OperationCanceledException();
                    restartEnvironment.CloseClient(stop.ProcessId, stop.ProcessCreated, cancelled, action =>
                    { lock (gate) { if (cancelled()) throw new OperationCanceledException(); action(); issued = true; } });
                    exited = true;
                }
                catch (OperationCanceledException) { error = "Close cancelled; completion hold retained."; }
                catch (Exception ex) { error = "Client close failed: " + ex.Message; }
                lock (gate)
                {
                    stop.ClosePending = false; stop.CloseState = exited ? "closed" : error.StartsWith("Close cancelled", StringComparison.Ordinal) ? "cancelled" : "failed";
                    stop.CloseDetail = exited ? "Client exit confirmed; automatic relaunch held." : error;
                    Runtime current;
                    bool ownsRuntime = diagnosticGeneration == diagnostic && runtimes.TryGetValue(stop.AccountId, out current)
                        && ReferenceEquals(current, runtime) && runtime.ProcessId == stop.ProcessId && runtime.ResumeOperationGeneration == operation;
                    if (ownsRuntime) runtime.ScriptRunning = runtime.ClosingForRecovery = runtime.RecoveryOwned = false;
                    if (exited)
                    {
                        if (ownsRuntime)
                        { runtime.ProcessId = null; runtime.CharacterSession = null; runtime.ConfirmedCharacter = null; runtime.ResumeSent = runtime.HasBeenOnline = false; }
                        try { positionClientExited?.Invoke(stop.ProcessId); } catch (Exception ex) { Log("Completed-client reader cleanup failed: " + ex.Message); }
                    }
                    PersistFarmingStopsLocked(); SetStage(runtime, VanillaReconnectStage.Stopped, stop.FullDetail);
                }
                RaiseUpdated();
            };
            try { restartEnvironment.Queue(work); }
            catch (Exception ex)
            { runtime.ScriptRunning = runtime.ClosingForRecovery = false; stop.ClosePending = false; stop.CloseState = "failed";
                stop.CloseDetail = "Cannot queue completion close: " + ex.Message; PersistFarmingStopsLocked(); }
        }
    }
}
