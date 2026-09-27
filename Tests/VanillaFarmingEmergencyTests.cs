using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaFarmingEmergencyTests
    {
        private static int passed, failed;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static int Run()
        {
            passed = failed = 0;
            Test("Emergency requires all three strict thresholds", Thresholds);
            Test("Emergency defaults and legacy settings retain original limits", SettingsDefaults);
            Test("Emergency settings reject null and out-of-range limits", SettingsValidation);
            Test("Custom decimal emergency limits preserve strict AND boundaries", CustomThresholds);
            Test("Emergency limits persist across restart and return isolated snapshots", SettingsPersistence);
            Test("Unrelated stale Recovery saves retain the latest emergency limits", StaleRecoverySave);
            Test("Emergency settings save preserves runtimes, leases and existing holds", SettingsIsolation);
            Test("Failed emergency settings save leaves active limits and queued close unchanged", SettingsSaveFailure);
            Test("Failed ordinary Recovery save preserves runtime ownership, generations and emergency holds", RecoverySettingsSaveFailure);
            Test("Changed emergency limits cancel an unissued close without clearing its history", ChangedLimitsCancelQueuedClose);
            Test("Emergency limit changes after native close retain exit confirmation", ChangedLimitsAfterIssuedClose);
            Test("Saving unchanged emergency limits does not cancel a queued close", UnchangedLimitsKeepQueuedClose);
            Test("Legacy holds without configured-limit history remain compatible", LegacyHold);
            Test("Emergency accepts verified zero HP and SP", ZeroVitals);
            Test("Emergency rejects stale, future, demo and errored samples", InvalidSamples);
            Test("Emergency rejects every unavailable, unverified or mixed-time field", InvalidFields);
            Test("Emergency rejects invalid maxima and incoherent current values", InvalidPairs);
            Test("Emergency rejects wrong process, session and loading state", Context);
            Test("Emergency closes only affected character without recovery or Cart enabled", IndependentProtection);
            Test("Emergency requires one exact enabled account identity", AccountIdentity);
            Test("Emergency does not requeue an already held identity", Deduplicated);
            Test("Emergency holds survive process restart and explicit clear is durable", Persistence);
            Test("Unreadable emergency hold file fails closed until explicit clear", CorruptStore);
            Test("Emergency close denial retains hold and exact failure", CloseFailure);
            Test("Emergency creation-time failure sends no close", PinFailure);
            Test("STOP cancels queued emergency close and retains hold", StopCancellation);
            Test("Settings changes cancel queued emergency close and retain hold", SettingsCancellation);
            Test("Replacement session or process cancels queued emergency close", ReplacementCancellation);
            Test("Emergency retains active input lease through confirmed exit", ActiveInputLease);
            Test("Emergency clear cannot revive a pending close or active input worker", ClearWhileBusy);
            Test("Clearing ordinary Weight holds cannot clear emergency holds", WeightClearIsolation);
            Test("Emergency held Cart token cancels before further input", CartCancellation);
            Test("Emergency prevents direct recovery restart for a held character", RestartBlocked);
            Test("Emergency protects a uniquely observed replacement while supervision is off", StaleRuntime);
            Test("Emergency rejects duplicate fresh observed character identities", DuplicateObservedIdentity);
            Test("Fleet identity disappearance after termination still permits exit confirmation", IdentityGoneAfterClose);
            Test("Restored pending close requires fresh danger and queues once", RestoredPending);
            Test("Restored failed close remains held without automatic retry", RestoredFailure);
            Test("Two endangered clients each close without waiting for recovery", BothAffected);
            Test("Emergency store write failure retains protection and still closes proven danger", SaveFailure);
            Test("Supervisor clearing an exited PID cannot cancel pinned exit confirmation", RuntimePidGoneAfterClose);
            Test("An inactive recovery lease cannot strand an emergency hold", InactiveRecoveryLease);
            Test("Emergency clear preserves unrelated errors and remaining Weight holds", ClearIsolation);
            Test("Emergency email opt-in is captured and mail-only edits do not cancel close", EmergencyEmailOptIn);
            Test("Restored emergency close reuses one durable email incident", EmergencyNoticeRestart);
            Test("Completed farming persists before cleanup and optional close", CompletionPersistence);
            Test("Completion closes only after input cleanup and preserves its exit proof", CompletionClose);
            Test("Completion requires both fresh exact capacity thresholds", CompletionEvidence);
            Test("Changed completion close policy cancels queued close and retains hold", CompletionPolicyCancellation);
            Test("Completion close denial retains visible result and pending notification", CompletionCloseFailure);
            Test("Completion record write failure holds only the affected character and sends no close", CompletionStorageFailure);
            Test("Clearing emergency holds cannot detach a queued completion close", ClearKindIsolation);
            Test("Completed holds survive restart and clear durably without clearing emergencies", CompletionClear);
            Test("Changing saved character identity does not inherit an old completion hold", CompletionIdentityChange);
            Test("Unreadable stop ledger fails closed with Error status until explicit clear", CompletionCorruptStore);
            Test("Completion records read current global and row policies after STOP", CompletionCurrentPolicy);
            Test("Failed primary emergency hold clear retains the actual sent-email history", EmergencyClearHistoryFailure);
            Console.WriteLine("Farming emergency: {0} passed; {1} failed. Synthetic state, isolated hold files and fake processes only.", passed, failed);
            return failed;
        }

        private static VanillaClientState State(int pid = 101, uint weight = 510, uint sp = 240, uint hp = 490)
        {
            var values = new Dictionary<VanillaField, object>
            {
                { VanillaField.UserName, pid == 101 ? "account-a" : "account-b" },
                { VanillaField.CharacterName, pid == 101 ? "Farmer A" : "Farmer B" },
                { VanillaField.Map, "field" }, { VanillaField.CurrentHP, hp }, { VanillaField.MaxHP, 1000U },
                { VanillaField.CurrentSP, sp }, { VanillaField.MaxSP, 1000U },
                { VanillaField.CurrentWeight, weight }, { VanillaField.MaxWeight, 1000U },
                { VanillaField.X, 10 }, { VanillaField.Y, 20 }
            };
            var state = VanillaClientState.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, null, values, null, null);
            state.ProcessId = pid;
            foreach (StateValue field in state.Fields.Values.Where(field => field.IsAvailable)) field.Validation = StateValidation.Valid;
            return state;
        }

        private static bool Danger(VanillaClientState state, DateTimeOffset? now = null, int pid = 101)
        { VanillaFarmingEmergencyEvidence evidence; return VanillaFarmingEmergency.TryEvaluate(state, pid, now ?? DateTimeOffset.UtcNow, out evidence); }

        private static void Thresholds()
        {
            Assert(Danger(State()), "All three dangerous values were rejected.");
            foreach (uint weight in new[] { 0U, 499U, 500U }) Assert(!Danger(State(weight: weight)), "Weight boundary was inclusive.");
            foreach (uint sp in new[] { 250U, 251U, 1000U }) Assert(!Danger(State(sp: sp)), "SP boundary was inclusive.");
            foreach (uint hp in new[] { 500U, 501U, 1000U }) Assert(!Danger(State(hp: hp)), "HP boundary was inclusive.");
            Assert(Danger(State(weight: 501, sp: 249, hp: 499)), "Strict values just inside thresholds were rejected.");
        }

        private static VanillaFarmingEmergencySettings CustomLimits()
        { return new VanillaFarmingEmergencySettings { WeightAbovePercent = 63.3m, SpBelowPercent = 17.5m, HpBelowPercent = 42.7m }; }

        private static void AssertLimits(VanillaFarmingEmergencySettings value, decimal weight = 50m, decimal sp = 25m, decimal hp = 50m)
        { Assert(value != null && value.WeightAbovePercent == weight && value.SpBelowPercent == sp && value.HpBelowPercent == hp); }

        private static void RejectSettings(Action action)
        { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid emergency settings were accepted."); }

        private static void SettingsDefaults()
        {
            var settings = VanillaReconnectSettings.CreateDefault();
            AssertLimits(settings.FarmingEmergency);
            Assert(settings.FarmingEmergency.Conditions == "Weight >50%, SP <25% and HP <50%");
            var serialized = JObject.FromObject(settings);
            serialized.Remove("FarmingEmergency");
            var legacy = JsonConvert.DeserializeObject<VanillaReconnectSettings>(serialized.ToString());
            legacy.Validate(); AssertLimits(legacy.FarmingEmergency);
            settings.FarmingEmergency = CustomLimits();
            var clone = settings.Clone();
            AssertLimits(clone.FarmingEmergency, 63.3m, 17.5m, 42.7m);
            clone.FarmingEmergency.WeightAbovePercent = 1;
            Assert(settings.FarmingEmergency.WeightAbovePercent == 63.3m, "Reconnect clone shared mutable emergency settings.");
            Assert(JObject.FromObject(settings.FarmingEmergency)["Conditions"] == null, "Computed conditions were persisted as a second setting.");
        }

        private static void SettingsValidation()
        {
            foreach (decimal weight in new[] { -0.1m, 100m, 101m })
                RejectSettings(() => new VanillaFarmingEmergencySettings { WeightAbovePercent = weight }.Validate());
            foreach (decimal vital in new[] { -0.1m, 0m, 100.1m })
            {
                RejectSettings(() => new VanillaFarmingEmergencySettings { SpBelowPercent = vital }.Validate());
                RejectSettings(() => new VanillaFarmingEmergencySettings { HpBelowPercent = vital }.Validate());
            }
            new VanillaFarmingEmergencySettings { WeightAbovePercent = 0, SpBelowPercent = 100, HpBelowPercent = 100 }.Validate();
            new VanillaFarmingEmergencySettings { WeightAbovePercent = 99.9m, SpBelowPercent = 0.1m, HpBelowPercent = 0.1m }.Validate();
            RejectSettings(() => new VanillaFarmingEmergencySettings { WeightAbovePercent = 99.99m }.Validate());
            RejectSettings(() => new VanillaFarmingEmergencySettings { SpBelowPercent = 0.05m }.Validate());
            RejectSettings(() => new VanillaFarmingEmergencySettings { HpBelowPercent = 42.75m }.Validate());
            var settings = VanillaReconnectSettings.CreateDefault(); settings.FarmingEmergency = null;
            RejectSettings(settings.Validate);
            var serialized = JObject.FromObject(VanillaReconnectSettings.CreateDefault()); serialized["FarmingEmergency"] = JValue.CreateNull();
            RejectSettings(() => JsonConvert.DeserializeObject<VanillaReconnectSettings>(serialized.ToString()).Validate());
            using (var h = new Harness())
            {
                RejectSettings(() => h.Supervisor.SaveFarmingEmergencySettings(null));
                RejectSettings(() => h.Supervisor.SaveFarmingEmergencySettings(new VanillaFarmingEmergencySettings { SpBelowPercent = 0 }));
                AssertLimits(h.Supervisor.FarmingEmergencySettings);
                File.WriteAllText(Path.Combine(h.Root, "VanillaReconnect", "reconnect.json"), serialized.ToString());
                RejectSettings(() => new VanillaReconnectStore(h.Root).Load());
            }
        }

        private static void CustomThresholds()
        {
            var limits = CustomLimits();
            foreach (var sample in new[] { State(weight: 633, sp: 174, hp: 426), State(weight: 634, sp: 175, hp: 426),
                State(weight: 634, sp: 174, hp: 427) })
            {
                VanillaFarmingEmergencyEvidence ignored;
                Assert(!VanillaFarmingEmergency.TryEvaluate(sample, 101, sample.SampledAtUtc, limits, out ignored),
                    "A custom threshold boundary was inclusive or AND conditions were weakened.");
            }
            var state = State(weight: 634, sp: 174, hp: 426);
            VanillaFarmingEmergencyEvidence evidence;
            Assert(VanillaFarmingEmergency.TryEvaluate(state, 101, state.SampledAtUtc, limits, out evidence));
            Assert(evidence.Conditions == "Weight >63.3%, SP <17.5% and HP <42.7%");
            limits.WeightAbovePercent = 90;
            Assert(evidence.Conditions.Contains("63.3"), "Evidence history changed with a mutable settings object.");
            using (var h = new Harness())
            {
                h.Supervisor.SaveFarmingEmergencySettings(CustomLimits());
                Assert(!h.Observe(State()), "Production evaluation ignored configured limits.");
                Assert(h.Observe(State(weight: 634, sp: 174, hp: 426)) && h.E.Work.Count == 1);
            }
        }

        private static void SettingsPersistence()
        {
            using (var h = new Harness())
            {
                var value = CustomLimits(); h.Supervisor.SaveFarmingEmergencySettings(value);
                value.WeightAbovePercent = 1;
                var snapshot = h.Supervisor.FarmingEmergencySettings; snapshot.SpBelowPercent = 1;
                AssertLimits(h.Supervisor.FarmingEmergencySettings, 63.3m, 17.5m, 42.7m);
                h.Restart(); AssertLimits(h.Supervisor.FarmingEmergencySettings, 63.3m, 17.5m, 42.7m);
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains(CustomLimits().Conditions));
            }
        }

        private static void StaleRecoverySave()
        {
            using (var h = new Harness())
            {
                var cached = h.Supervisor.Settings;
                h.Supervisor.SaveFarmingEmergencySettings(CustomLimits());
                cached.PollMs += 500; h.Supervisor.Apply(cached, true);
                AssertLimits(h.Supervisor.FarmingEmergencySettings, 63.3m, 17.5m, 42.7m);
                h.Restart(); AssertLimits(h.Supervisor.FarmingEmergencySettings, 63.3m, 17.5m, 42.7m);
                Assert(h.Supervisor.Settings.PollMs == cached.PollMs, "Unrelated Recovery setting did not save.");
            }
        }

        private static void SettingsIsolation()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe()); h.E.Work.Dequeue()();
                Set(h.B, "ProcessId", (int?)102); Set(h.B, "ScriptRunning", true); Set(h.B, "RecoveryOwned", true);
                object settings = Get(h.Supervisor, "settings"), runtimes = Get(h.Supervisor, "runtimes");
                int diagnostic = (int)Get(h.Supervisor, "diagnosticGeneration"), resume = (int)Get(h.Supervisor, "resumeVerificationGeneration");
                string historic = (string)Get(h.A, "Detail");
                h.Supervisor.SaveFarmingEmergencySettings(CustomLimits());
                Assert(ReferenceEquals(settings, Get(h.Supervisor, "settings")) && ReferenceEquals(runtimes, Get(h.Supervisor, "runtimes")));
                Assert((int)Get(h.Supervisor, "diagnosticGeneration") == diagnostic && (int)Get(h.Supervisor, "resumeVerificationGeneration") == resume);
                Assert((int?)Get(h.B, "ProcessId") == 102 && (bool)Get(h.B, "ScriptRunning") && (bool)Get(h.B, "RecoveryOwned"));
                Assert(h.HeldA && !h.HeldB && (string)Get(h.A, "Detail") == historic, "Settings save cleared or rewrote an existing hold.");
                h.Restart(); Assert(h.HeldA && h.Supervisor.FarmingEmergencyStatus.Contains("Limits: Weight >50%, SP <25% and HP <50%"));
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains(CustomLimits().Conditions));
            }
        }

        private static void SettingsSaveFailure()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe());
                string path = Path.Combine(h.Root, "VanillaReconnect", "reconnect.json");
                string before = File.ReadAllText(path);
                Directory.CreateDirectory(path + ".tmp");
                try { h.Supervisor.SaveFarmingEmergencySettings(CustomLimits()); throw new Exception("Undurable limits became active."); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                AssertLimits(h.Supervisor.FarmingEmergencySettings);
                Assert(File.ReadAllText(path) == before);
                h.E.Work.Dequeue()(); Assert(h.HeldA && h.E.Closed.SequenceEqual(new[] { 101 }), "Failed save cancelled an already authorized close.");
            }
        }

        private static void RecoverySettingsSaveFailure()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe());
                Set(h.B, "ProcessId", (int?)102); Set(h.B, "ScriptRunning", true); Set(h.B, "RecoveryOwned", true);
                object settings = Get(h.Supervisor, "settings"), runtimes = Get(h.Supervisor, "runtimes");
                int diagnostic = (int)Get(h.Supervisor, "diagnosticGeneration"), resume = (int)Get(h.Supervisor, "resumeVerificationGeneration");
                string detail = (string)Get(h.A, "Detail");
                string path = h.Supervisor.SettingsPath, before = File.ReadAllText(path);
                var edited = h.Supervisor.Settings; edited.PollMs += 500; edited.Accounts[1].CharacterName = "Changed unsaved identity";
                Directory.CreateDirectory(path + ".tmp");
                try { h.Supervisor.Apply(edited, true); throw new Exception("Failed Recovery save was accepted."); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Assert(ReferenceEquals(settings, Get(h.Supervisor, "settings")) && ReferenceEquals(runtimes, Get(h.Supervisor, "runtimes")));
                Assert((int)Get(h.Supervisor, "diagnosticGeneration") == diagnostic && (int)Get(h.Supervisor, "resumeVerificationGeneration") == resume);
                Assert((int?)Get(h.B, "ProcessId") == 102 && (bool)Get(h.B, "ScriptRunning") && (bool)Get(h.B, "RecoveryOwned"));
                Assert(File.ReadAllText(path) == before && h.HeldA && !h.HeldB && (string)Get(h.A, "Detail") == detail);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }), "Failed ordinary save cancelled an authorized emergency close.");
            }
        }

        private static void ChangedLimitsCancelQueuedClose()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe()); h.Supervisor.SaveFarmingEmergencySettings(CustomLimits()); h.E.Work.Dequeue()();
                Assert(h.HeldA && h.E.Closed.Count == 0 && h.Supervisor.FarmingEmergencyStatus.Contains("Close cancelled"));
                h.Restart(); Assert(h.HeldA && h.E.Work.Count == 0);
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains("Limits: Weight >50%, SP <25% and HP <50%"));
            }
        }

        private static void ChangedLimitsAfterIssuedClose()
        {
            using (var h = new Harness())
            {
                h.E.AfterOwned = () => h.Supervisor.SaveFarmingEmergencySettings(CustomLimits());
                Assert(h.Observe()); h.E.Work.Dequeue()();
                Assert(h.HeldA && h.E.Closed.SequenceEqual(new[] { 101 }) && h.Forgotten.SequenceEqual(new[] { 101 }));
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains("exit confirmed"), "Threshold edit cancelled issued native-close exit confirmation.");
            }
        }

        private static void UnchangedLimitsKeepQueuedClose()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe()); h.Supervisor.SaveFarmingEmergencySettings(h.Supervisor.FarmingEmergencySettings); h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }) && h.HeldA);
            }
        }

        private static void LegacyHold()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe()); h.E.Work.Dequeue()();
                string path = Path.Combine(h.Root, "VanillaReconnect", "farming-emergency-holds.json");
                var document = JObject.Parse(File.ReadAllText(path));
                foreach (JObject hold in document["Holds"])
                { hold.Remove("Conditions"); hold["Detail"] = "EMERGENCY STOP: legacy evidence; explicit clear required."; }
                File.WriteAllText(path, document.ToString()); h.Restart();
                Assert(h.HeldA && !h.HeldB && h.Supervisor.FarmingEmergencyStatus.Contains("legacy evidence"));
            }
        }
        private static void ZeroVitals() { Assert(Danger(State(sp: 0, hp: 0)), "Verified zero values must not become unknown."); }
        private static void InvalidSamples()
        {
            var state = State(); Assert(Danger(state, state.SampledAtUtc.AddMilliseconds(1000)));
            Assert(!Danger(state, state.SampledAtUtc.AddMilliseconds(1001))); Assert(!Danger(state, state.SampledAtUtc.AddTicks(-1)));
            state.IsDemo = true; Assert(!Danger(state)); state.IsDemo = false;
            state.Error = "read denied"; Assert(!Danger(state)); Assert(!Danger(null));
        }
        private static void InvalidFields()
        {
            foreach (VanillaField field in new[] { VanillaField.UserName, VanillaField.CharacterName, VanillaField.Map,
                VanillaField.CurrentHP, VanillaField.MaxHP, VanillaField.CurrentSP, VanillaField.MaxSP,
                VanillaField.CurrentWeight, VanillaField.MaxWeight })
            {
                var state = State(); state.Fields[field].Validation = StateValidation.Unverified; Assert(!Danger(state), field + " unverified accepted");
                state = State(); state.Fields[field].IsAvailable = false; Assert(!Danger(state), field + " unavailable accepted");
                state = State(); state.Fields[field].LastObservedAtUtc = state.SampledAtUtc.AddTicks(-1); Assert(!Danger(state), field + " stale field accepted");
            }
        }
        private static void InvalidPairs()
        {
            foreach (VanillaField field in new[] { VanillaField.MaxHP, VanillaField.MaxSP, VanillaField.MaxWeight })
            { var state = State(); Replace(state, field, 0U); Assert(!Danger(state)); }
            foreach (VanillaField field in new[] { VanillaField.CurrentHP, VanillaField.CurrentSP, VanillaField.CurrentWeight })
            { var state = State(); Replace(state, field, 1001U); Assert(!Danger(state)); }
        }
        private static void Context()
        {
            var state = State(); Assert(!Danger(state, pid: 102)); state.SessionId = Guid.Empty; Assert(!Danger(state));
            state = State(); Replace(state, VanillaField.Loading, true); Assert(!Danger(state));
            state = State(); Replace(state, VanillaField.ClientReady, false); Assert(!Danger(state));
        }

        private sealed class Env : IVanillaRecoveryRestartEnvironment
        {
            internal bool Denied, PinDenied;
            internal Action AfterOwned;
            internal readonly Queue<Action> Work = new Queue<Action>();
            internal readonly List<int> Closed = new List<int>();
            internal readonly List<bool> Immediate = new List<bool>();
            public DateTimeOffset UtcNow { get { return DateTimeOffset.UtcNow; } }
            public TimeSpan MonotonicNow { get { return TimeSpan.Zero; } }
            public DateTime GetStartTimeUtc(int pid)
            { if (PinDenied) throw new InvalidOperationException("creation-time denied"); return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); }
            public void Queue(Action work) { Work.Enqueue(work); }
            public void CloseClient(int pid, DateTime expected, Func<bool> cancelled, Action<Action> owned, bool immediate = false)
            {
                if (cancelled()) throw new OperationCanceledException();
                Assert(expected == GetStartTimeUtc(pid), "Wrong creation-time pin.");
                if (Denied) throw new InvalidOperationException("Win32 5 access denied");
                owned(() => { Closed.Add(pid); Immediate.Add(immediate); });
                AfterOwned?.Invoke();
                if (cancelled()) throw new OperationCanceledException("Cancelled while confirming pinned process exit.");
            }
        }

        private sealed class Harness : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "4R-emergency-" + Guid.NewGuid().ToString("N"));
            internal readonly Env E = new Env();
            internal VanillaReconnectSupervisor Supervisor;
            internal readonly Dictionary<int, VanillaClientState> Samples = new Dictionary<int, VanillaClientState>();
            internal readonly List<int> Forgotten = new List<int>();
            internal object A, B;
            internal Harness()
            {
                Supervisor = new VanillaReconnectSupervisor(Root, E);
                var config = Supervisor.Settings;
                for (int index = 0; index < 2; index++)
                {
                    config.Accounts[index].Enabled = true;
                    config.Accounts[index].UserName = index == 0 ? "account-a" : "account-b";
                    config.Accounts[index].CharacterName = index == 0 ? "Farmer A" : "Farmer B";
                    config.Accounts[index].CartMaintenanceEnabled = false;
                }
                config.AutoRecover = false;
                Supervisor.Apply(config, true);
                BindObjects();
            }
            private void BindObjects()
            {
                var runtimes = (IDictionary)Get(Supervisor, "runtimes");
                A = runtimes[Supervisor.Settings.Accounts[0].Id]; B = runtimes[Supervisor.Settings.Accounts[1].Id];
                Supervisor.SetCharacterSource(() => Samples.Values.Select(VanillaCharacterIdentity.FromState).ToArray());
                Supervisor.SetPositionSource(pid => null, Forgotten.Add);
            }
            internal void Restart()
            { Supervisor.Dispose(); Supervisor = new VanillaReconnectSupervisor(Root, E); BindObjects(); }
            internal bool Observe(VanillaClientState sample = null)
            {
                sample = sample ?? State(); Samples[sample.ProcessId.Value] = sample;
                return Supervisor.ObserveFarmingEmergency(new VanillaFleetClientInfo { ProcessId = sample.ProcessId.Value, Snapshot = sample });
            }
            internal bool HeldA { get { return Supervisor.FarmingEmergencyHeld(Supervisor.Settings.Accounts[0]); } }
            internal bool HeldB { get { return Supervisor.FarmingEmergencyHeld(Supervisor.Settings.Accounts[1]); } }
            public void Dispose()
            { Supervisor.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }

        private static void IndependentProtection()
        {
            using (var h = new Harness())
            {
                Set(h.B, "ProcessId", (int?)102); Set(h.B, "ScriptRunning", true);
                Assert(!h.Supervisor.IsRunning && !h.Supervisor.Settings.AutoRecover);
                Assert(h.Observe()); Assert(h.HeldA && !h.HeldB && h.E.Work.Count == 1);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }) && h.E.Immediate.Single());
                Assert((int?)Get(h.B, "ProcessId") == 102 && (bool)Get(h.B, "ScriptRunning"), "Healthy sibling lease changed.");
                Assert(h.Forgotten.SequenceEqual(new[] { 101 }));
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains("exit confirmed"));
            }
        }
        private static void AccountIdentity()
        {
            using (var h = new Harness())
            {
                var sample = State(); Replace(sample, VanillaField.UserName, "wrong"); Assert(!h.Observe(sample));
                var settings = h.Supervisor.Settings; settings.Accounts[0].Enabled = false; h.Supervisor.Apply(settings, false);
                Assert(!h.Observe());
                settings = h.Supervisor.Settings; settings.Accounts[0].Enabled = true;
                settings.Accounts[1].UserName = "account-a"; settings.Accounts[1].CharacterName = "Farmer A";
                h.Supervisor.Apply(settings, false); Assert(!h.Observe()); Assert(h.E.Work.Count == 0);
            }
        }
        private static void Deduplicated()
        { using (var h = new Harness()) { Assert(h.Observe()); Assert(h.Observe(State(hp: 900))); Assert(h.E.Work.Count == 1); } }
        private static void Persistence()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe()); h.E.Work.Dequeue()(); h.Restart(); Assert(h.HeldA && !h.HeldB);
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains("SP 240/1000"));
                h.Supervisor.ClearFarmingEmergencyHolds(); Assert(!h.HeldA); h.Restart(); Assert(!h.HeldA);
            }
        }
        private static void CorruptStore()
        {
            using (var h = new Harness())
            {
                File.WriteAllText(Path.Combine(h.Root, "VanillaReconnect", "farming-emergency-holds.json"), "{bad-json");
                h.Restart(); Assert(h.HeldA && h.HeldB); Assert(h.Supervisor.FarmingEmergencyStatus.Contains("could not be read"));
                h.Supervisor.ClearFarmingEmergencyHolds(); Assert(!h.HeldA && !h.HeldB); h.Restart(); Assert(!h.HeldA);
            }
        }
        private static void CloseFailure()
        {
            using (var h = new Harness())
            {
                h.E.Denied = true; Set(h.A, "ProcessId", (int?)101); Assert(h.Observe()); h.E.Work.Dequeue()();
                Assert(h.HeldA && h.E.Closed.Count == 0 && (int?)Get(h.A, "ProcessId") == 101);
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains("Win32 5 access denied"));
            }
        }
        private static void PinFailure()
        { using (var h = new Harness()) { h.E.PinDenied = true; Assert(h.Observe()); Assert(h.HeldA && h.E.Work.Count == 0); Assert(h.Supervisor.FarmingEmergencyStatus.Contains("creation-time denied")); } }
        private static void StopCancellation()
        { using (var h = new Harness()) { Assert(h.Observe()); h.Supervisor.Stop(); h.E.Work.Dequeue()(); Assert(h.HeldA && h.E.Closed.Count == 0); } }
        private static void SettingsCancellation()
        { using (var h = new Harness()) { Assert(h.Observe()); var config = h.Supervisor.Settings; config.PollMs += 500; h.Supervisor.Apply(config, false); h.E.Work.Dequeue()(); Assert(h.HeldA && h.E.Closed.Count == 0); } }
        private static void ReplacementCancellation()
        {
            foreach (bool session in new[] { true, false }) using (var h = new Harness())
            { Assert(h.Observe()); if (session) h.Samples[101] = State(); else Set(h.A, "ProcessId", (int?)999); h.E.Work.Dequeue()(); Assert(h.HeldA && h.E.Closed.Count == 0); }
        }
        private static void ActiveInputLease()
        {
            using (var h = new Harness())
            {
                Set(h.A, "ProcessId", (int?)101); Set(h.A, "ScriptRunning", true); Assert(h.Observe()); h.E.Work.Dequeue()();
                Assert((bool)Get(h.A, "ScriptRunning") && (int?)Get(h.A, "ProcessId") == 101, "Lease was released before input finally.");
                Set(h.A, "ScriptRunning", false); h.Supervisor.ClearFarmingEmergencyHolds(); Assert(Get(h.A, "ProcessId") == null);
            }
        }
        private static void ClearWhileBusy()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe()); Throws(h.Supervisor.ClearFarmingEmergencyHolds); Assert(h.HeldA);
                h.E.Work.Dequeue()(); Set(h.A, "ScriptRunning", true); Throws(h.Supervisor.ClearFarmingEmergencyHolds); Assert(h.HeldA);
            }
        }
        private static void WeightClearIsolation()
        { using (var h = new Harness()) { Assert(h.Observe()); h.E.Work.Dequeue()(); h.Supervisor.ClearWeightManualHolds(); Assert(h.HeldA); } }
        private static void CartCancellation()
        {
            using (var h = new Harness())
            {
                var config = h.Supervisor.Settings; config.Accounts[0].CartMaintenanceEnabled = true; h.Supervisor.Apply(config, false);
                Set(h.Supervisor, "running", true); Set(h.A, "ProcessId", (int?)101);
                var sample = State(); h.Samples[101] = sample;
                VanillaWeightMaintenanceToken token; string reason;
                Assert(h.Supervisor.TryBeginWeightMaintenance(101, out token, out reason), reason);
                Assert(h.Observe(sample)); Assert(h.Supervisor.WeightMaintenanceCancelled(token), "Cart token remained authorized after emergency.");
                h.E.Work.Dequeue()(); h.Supervisor.CompleteWeightMaintenance(token, false, "late completion");
                Assert(h.HeldA && !(bool)Get(h.A, "ScriptRunning"));
            }
        }
        private static void RestartBlocked()
        {
            using (var h = new Harness())
            {
                var config = h.Supervisor.Settings; config.AutoRecover = true; h.Supervisor.Apply(config, false);
                Set(h.Supervisor, "running", true); Set(h.A, "ProcessId", (int?)101);
                Assert(h.Observe()); h.E.Denied = true; h.E.Work.Dequeue()();
                h.Supervisor.RunLoginNow(h.Supervisor.Settings.Accounts[0].Id); Assert(h.E.Work.Count == 0 && h.HeldA);
            }
        }

        private static void StaleRuntime()
        {
            using (var h = new Harness())
            {
                Set(h.A, "ProcessId", (int?)999); Set(h.A, "CharacterSession", (Guid?)Guid.NewGuid());
                Assert(h.Observe()); h.E.Work.Dequeue()(); Assert(h.E.Closed.SequenceEqual(new[] { 101 }));
                Assert((int?)Get(h.A, "ProcessId") == 999, "Emergency rewrote a different PID binding.");
            }
        }
        private static void DuplicateObservedIdentity()
        {
            using (var h = new Harness())
            {
                var duplicate = State(); duplicate.ProcessId = 999; h.Samples[999] = duplicate;
                Assert(!h.Observe()); Assert(!h.HeldA && h.E.Work.Count == 0);
            }
        }
        private static void IdentityGoneAfterClose()
        {
            using (var h = new Harness())
            {
                h.E.AfterOwned = () => h.Samples.Clear(); Assert(h.Observe()); h.E.Work.Dequeue()();
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains("exit confirmed"), "Successful exit became identity cancellation.");
                Assert(h.Forgotten.SequenceEqual(new[] { 101 }));
            }
        }
        private static void RestoredPending()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe()); h.E.Work.Clear(); h.Restart(); Assert(h.HeldA && h.E.Work.Count == 0);
                Assert(h.Observe(State(hp: 900))); Assert(h.E.Work.Count == 0, "Restored intent closed without fresh danger.");
                var sample = State(); Assert(h.Observe(sample)); Assert(h.E.Work.Count == 1);
                Assert(h.Observe(sample)); Assert(h.E.Work.Count == 1); h.E.Work.Dequeue()(); Assert(h.E.Closed.Count == 1);
            }
        }
        private static void RestoredFailure()
        {
            using (var h = new Harness())
            {
                h.E.Denied = true; Assert(h.Observe()); h.E.Work.Dequeue()(); h.Restart();
                Assert(h.Observe()); Assert(h.HeldA && h.E.Work.Count == 0);
            }
        }
        private static void BothAffected()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe(State())); Assert(h.Observe(State(pid: 102))); Assert(h.E.Work.Count == 2);
                h.E.Work.Dequeue()(); h.E.Work.Dequeue()();
                Assert(h.HeldA && h.HeldB && h.E.Closed.SequenceEqual(new[] { 101, 102 }));
            }
        }
        private static void SaveFailure()
        {
            using (var h = new Harness())
            {
                string path = Path.Combine(h.Root, "VanillaReconnect", "farming-emergency-holds.json");
                Directory.CreateDirectory(path);
                Assert(h.Observe()); h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }) && h.HeldA && h.HeldB);
                Assert(h.Supervisor.FarmingEmergencyStatus.Contains("could not be saved"));
                try { h.Supervisor.ClearFarmingEmergencyHolds(); throw new Exception("Undurable clear succeeded."); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Assert(h.HeldA);
            }
        }
        private static void RuntimePidGoneAfterClose()
        {
            using (var h = new Harness())
            {
                Set(h.A, "ProcessId", (int?)101); h.E.AfterOwned = () => Set(h.A, "ProcessId", null);
                Assert(h.Observe()); h.E.Work.Dequeue()(); Assert(h.Supervisor.FarmingEmergencyStatus.Contains("exit confirmed"));
            }
        }
        private static void InactiveRecoveryLease()
        {
            using (var h = new Harness())
            {
                Set(h.A, "RecoveryOwned", true); Assert(h.Observe()); Assert(!(bool)Get(h.A, "RecoveryOwned"));
                h.E.Denied = true; h.E.Work.Dequeue()(); h.Supervisor.ClearFarmingEmergencyHolds(); Assert(!h.HeldA);
            }
        }
        private static void ClearIsolation()
        {
            using (var h = new Harness())
            {
                Set(h.B, "Stage", VanillaReconnectStage.Error); Set(h.B, "Detail", "Independent configuration error");
                Assert(h.Observe()); h.E.Work.Dequeue()();
                ((HashSet<string>)Get(h.Supervisor, "weightManualHolds")).Add(h.Supervisor.Settings.Accounts[0].Id);
                h.Supervisor.ClearFarmingEmergencyHolds();
                Assert(!h.HeldA && (string)Get(h.A, "Detail") == "Weight/Cart manual hold remains after emergency hold clear");
                Assert((string)Get(h.B, "Detail") == "Independent configuration error");
            }
        }

        private static void EmergencyEmailOptIn()
        {
            using (var h = new Harness())
            {
                Assert(!h.Supervisor.FarmingEmergencySettings.SendEmail);
                var settings = h.Supervisor.FarmingEmergencySettings; settings.SendEmail = true;
                h.Supervisor.SaveFarmingEmergencySettings(settings); Assert(h.Observe());
                settings.SendEmail = false; h.Supervisor.SaveFarmingEmergencySettings(settings);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }), "Email-only edit cancelled emergency close.");
                var pending = h.Supervisor.PendingFarmingStopEmails();
                Assert(pending.Count == 1 && pending[0].EmailRequested && pending[0].Kind == VanillaFarmingStopKind.Emergency);
                h.Restart(); Assert(!h.Supervisor.FarmingEmergencySettings.SendEmail && h.Supervisor.PendingFarmingStopEmails().Count == 1);
            }
            using (var h = new Harness())
            {
                Assert(h.Observe()); h.E.Work.Dequeue()();
                var settings = h.Supervisor.FarmingEmergencySettings; settings.SendEmail = true; h.Supervisor.SaveFarmingEmergencySettings(settings);
                Assert(h.Supervisor.PendingFarmingStopEmails().Count == 0, "Opt-in retroactively authorized an old stop email.");
            }
        }

        private static void EmergencyNoticeRestart()
        {
            foreach (bool missingLedger in new[] { false, true })
            using (var h = new Harness())
            {
                var settings = h.Supervisor.FarmingEmergencySettings; settings.SendEmail = true; h.Supervisor.SaveFarmingEmergencySettings(settings);
                Assert(h.Observe()); h.E.Work.Clear();
                string path = Path.Combine(h.Root, "VanillaReconnect", "farming-stops.json");
                string original = (string)JObject.Parse(File.ReadAllText(path))["Stops"][0]["EventId"];
                if (missingLedger) File.Delete(path); // Replay a crash after durable hold intent but before ledger creation.
                h.Restart(); Assert(h.Observe()); h.E.Work.Dequeue()();
                Assert(JObject.Parse(File.ReadAllText(path))["Stops"].Count() == 1);
                Assert(h.Supervisor.PendingFarmingStopEmails().Single().EventId == original, "Restored emergency created duplicate email incidents.");
            }
        }

        private static VanillaFleetClientInfo CompletionClient(uint carried = 500, uint cart = 9900)
        {
            var state = State(weight: carried, sp: 900, hp: 900);
            Replace(state, VanillaField.CurrentCartWeight, cart); Replace(state, VanillaField.MaxCartWeight, 10000U);
            return new VanillaFleetClientInfo { ProcessId = 101, Snapshot = state, Identity = VanillaCharacterIdentity.FromState(state),
                WeightVerified = true, CartWeightVerified = true, CurrentWeight = carried, MaxWeight = 1000,
                CurrentCartWeight = cart, MaxCartWeight = 10000 };
        }

        private static VanillaWeightMaintenanceToken BeginCompletion(Harness h, VanillaFleetClientInfo client)
        {
            var settings = h.Supervisor.Settings; settings.Accounts[0].CartMaintenanceEnabled = true;
            h.Supervisor.Apply(settings, false); Set(h.Supervisor, "running", true); Set(h.A, "ProcessId", (int?)101);
            h.Samples[101] = client.Snapshot;
            VanillaWeightMaintenanceToken token; string reason;
            Assert(h.Supervisor.TryBeginWeightMaintenance(101, out token, out reason), reason);
            return token;
        }

        private static void CompletionPersistence()
        {
            using (var h = new Harness())
            {
                var client = CompletionClient(); var token = BeginCompletion(h, client);
                h.Supervisor.CompleteWeightFarmingDone(token, "Both limits reached; STOP verified.", client, false, true);
                Assert(h.Supervisor.IsWeightCompletedHold(token.AccountId) && (bool)Get(h.A, "ScriptRunning") && h.E.Work.Count == 0);
                Assert(h.Supervisor.PendingFarmingStopEmails().Count == 0, "Email escaped before input cleanup.");
                h.Supervisor.FinishWeightFarmingCompletion(token);
                Assert(!(bool)Get(h.A, "ScriptRunning") && h.Supervisor.PendingFarmingStopEmails().Count == 1);
                h.Restart(); Assert(h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]));
                var status = h.Supervisor.FarmingStopStatuses().Single();
                Assert(status.Status == "Completed" && status.ProcessId == null && status.Detail.Contains("9900/10000"));
                Assert(!(bool)Call(h.Supervisor, "CanLaunch", h.A, 0, DateTimeOffset.UtcNow), "Persisted completion was eligible for relaunch.");
            }
        }

        private static void CompletionClose()
        {
            using (var h = new Harness())
            {
                var client = CompletionClient(); var token = BeginCompletion(h, client);
                h.Supervisor.SetFarmingCompletionPolicy(true);
                h.Supervisor.CompleteWeightFarmingDone(token, "Verified STOP", client, true, true);
                Assert(h.E.Work.Count == 0 && (bool)Get(h.A, "ScriptRunning"));
                h.Supervisor.FinishWeightFarmingCompletion(token);
                Assert(h.E.Work.Count == 1 && (bool)Get(h.A, "ScriptRunning") && (bool)Get(h.A, "ClosingForRecovery"));
                Assert(h.Supervisor.PendingFarmingStopEmails().Count == 0);
                h.E.AfterOwned = () => h.Supervisor.SetFarmingCompletionPolicy(false);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }) && !h.E.Immediate.Single() && Get(h.A, "ProcessId") == null);
                Assert(!(bool)Get(h.A, "ScriptRunning") && h.Supervisor.PendingFarmingStopEmails().Single().CloseState == "closed");
                Assert(!h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[1]));
            }
        }

        private static void CompletionEvidence()
        {
            foreach (string invalid in new[] { "carried", "cart", "unverified", "identity", "stale" })
            using (var h = new Harness())
            {
                var client = CompletionClient(invalid == "carried" ? 499U : 500U, invalid == "cart" ? 9899U : 9900U);
                var token = BeginCompletion(h, client);
                if (invalid == "unverified") client.CartWeightVerified = false;
                if (invalid == "identity") client.Identity = null;
                if (invalid == "stale") client.Snapshot.SampledAtUtc = DateTimeOffset.UtcNow.AddSeconds(-4);
                Throws(() => h.Supervisor.CompleteWeightFarmingDone(token, "untrusted", client, false, false));
                Assert(!h.Supervisor.IsWeightCompletedHold(token.AccountId) && h.E.Work.Count == 0);
            }
        }

        private static void CompletionPolicyCancellation()
        {
            foreach (bool beforeCleanup in new[] { true, false })
            using (var h = new Harness())
            {
                var client = CompletionClient(); var token = BeginCompletion(h, client); h.Supervisor.SetFarmingCompletionPolicy(true);
                h.Supervisor.CompleteWeightFarmingDone(token, "Verified STOP", client, true, true);
                if (beforeCleanup) { h.Supervisor.SetFarmingCompletionPolicy(false); h.Supervisor.SetFarmingCompletionPolicy(true); }
                h.Supervisor.FinishWeightFarmingCompletion(token);
                if (!beforeCleanup) { h.Supervisor.SetFarmingCompletionPolicy(false); h.E.Work.Dequeue()(); }
                Assert(h.E.Closed.Count == 0 && h.Supervisor.IsWeightCompletedHold(token.AccountId));
                Assert(h.Supervisor.PendingFarmingStopEmails().Single().CloseState == "cancelled");
            }
        }

        private static void CompletionCloseFailure()
        {
            using (var h = new Harness())
            {
                var client = CompletionClient(); var token = BeginCompletion(h, client); h.Supervisor.SetFarmingCompletionPolicy(true);
                h.Supervisor.CompleteWeightFarmingDone(token, "Verified STOP", client, true, true);
                h.Supervisor.FinishWeightFarmingCompletion(token); h.E.Denied = true; h.E.Work.Dequeue()();
                Assert(h.Supervisor.IsWeightCompletedHold(token.AccountId) && h.E.Closed.Count == 0 && (int?)Get(h.A, "ProcessId") == 101);
                Assert(h.Supervisor.PendingFarmingStopEmails().Single().CloseState == "failed");
                Assert(h.Supervisor.FarmingStopStatuses().Single().Detail.Contains("access denied"));
            }
        }

        private static void CompletionStorageFailure()
        {
            using (var h = new Harness())
            {
                var client = CompletionClient(); var token = BeginCompletion(h, client); h.Supervisor.SetFarmingCompletionPolicy(true);
                Directory.CreateDirectory(Path.Combine(h.Root, "VanillaReconnect", "farming-stops.json.tmp"));
                h.Supervisor.CompleteWeightFarmingDone(token, "Verified STOP", client, true, true);
                h.Supervisor.FinishWeightFarmingCompletion(token);
                Assert(h.E.Work.Count == 0 && h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]));
                Assert(!h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[1]), "A stop-record save failure held the healthy sibling.");
                VanillaFarmingStopNotice reservation;
                Assert(!h.Supervisor.TryReserveFarmingStopEmail(h.Supervisor.PendingFarmingStopEmails().Single().EventId, out reservation));
            }
        }

        private static void ClearKindIsolation()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe(State(pid: 102))); h.E.Work.Dequeue()();
                var client = CompletionClient(); var token = BeginCompletion(h, client); h.Supervisor.SetFarmingCompletionPolicy(true);
                h.Supervisor.CompleteWeightFarmingDone(token, "Verified STOP", client, true, true); h.Supervisor.FinishWeightFarmingCompletion(token);
                h.Supervisor.ClearFarmingEmergencyHolds(); h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 102, 101 }) && h.Supervisor.PendingFarmingStopEmails().Single().CloseState == "closed");
            }
        }

        private static void CompletionClear()
        {
            using (var h = new Harness())
            {
                Assert(h.Observe(State(pid: 102))); h.E.Work.Dequeue()();
                var client = CompletionClient(); var token = BeginCompletion(h, client);
                h.Supervisor.CompleteWeightFarmingDone(token, "Verified STOP", client, false, true); h.Supervisor.FinishWeightFarmingCompletion(token);
                h.Restart(); h.Supervisor.ClearWeightManualHolds();
                Assert(!h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]) && h.HeldB);
                h.Restart(); Assert(!h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]) && h.HeldB);
                Assert(h.Supervisor.PendingFarmingStopEmails().Count == 0);
            }
        }

        private static void CompletionIdentityChange()
        {
            using (var h = new Harness())
            {
                var client = CompletionClient(); var token = BeginCompletion(h, client);
                h.Supervisor.CompleteWeightFarmingDone(token, "Verified STOP", client, false, true); h.Supervisor.FinishWeightFarmingCompletion(token);
                var settings = h.Supervisor.Settings; settings.Accounts[0].CharacterName = "Different character";
                Set(h.Supervisor, "running", false); h.Supervisor.Apply(settings, false);
                Assert(!h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]) && h.Supervisor.FarmingStopStatuses().Count == 0);
            }
        }

        private static void CompletionCorruptStore()
        {
            using (var h = new Harness())
            {
                File.WriteAllText(Path.Combine(h.Root, "VanillaReconnect", "farming-stops.json"), "{bad-json"); h.Restart();
                Assert(h.Supervisor.Settings.Accounts.All(h.Supervisor.FarmingCompletionHeld));
                Assert(h.Supervisor.FarmingStopStatuses().All(stop => stop.Status == "Error"));
                h.Supervisor.ClearWeightManualHolds(); h.Restart(); Assert(!h.Supervisor.Settings.Accounts.Any(h.Supervisor.FarmingCompletionHeld));
            }
        }

        private static void CompletionCurrentPolicy()
        {
            foreach (bool disableRow in new[] { false, true })
            using (var h = new Harness())
            {
                var client = CompletionClient(); var token = BeginCompletion(h, client);
                token.Account.WeightEmailEnabled = true;
                h.Supervisor.SetFarmingCompletionPolicy(true, true);
                if (disableRow) ((VanillaReconnectAccount)Get(h.A, "Account")).WeightEmailEnabled = false;
                else h.Supervisor.SetFarmingCompletionPolicy(false, false);
                h.Supervisor.CompleteWeightFarmingDone(token, "Verified STOP", client);
                h.Supervisor.FinishWeightFarmingCompletion(token);
                if (disableRow) h.E.Work.Dequeue()();
                Assert(h.Supervisor.PendingFarmingStopEmails().Count == 0, "A stale policy/token authorized a completion email.");
                if (!disableRow) Assert(h.E.Work.Count == 0 && h.E.Closed.Count == 0, "A stale close policy survived STOP.");
            }
        }

        private static void EmergencyClearHistoryFailure()
        {
            using (var h = new Harness())
            {
                var settings = h.Supervisor.FarmingEmergencySettings; settings.SendEmail = true; h.Supervisor.SaveFarmingEmergencySettings(settings);
                Assert(h.Observe()); h.E.Work.Dequeue()();
                VanillaFarmingStopNotice reserved;
                Assert(h.Supervisor.TryReserveFarmingStopEmail(h.Supervisor.PendingFarmingStopEmails().Single().EventId, out reserved));
                h.Supervisor.CompleteFarmingStopEmail(reserved.EventId, reserved.EmailAttemptId, true, "Recorded synthetic delivery.");
                Directory.CreateDirectory(Path.Combine(h.Root, "VanillaReconnect", "farming-emergency-holds.json.tmp"));
                try { h.Supervisor.ClearFarmingEmergencyHolds(); throw new Exception("Primary hold clear unexpectedly succeeded."); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Assert(h.HeldA && h.Supervisor.FarmingStopStatuses().Single().Detail.Contains("Email: sent"));
                h.Restart(); Assert(h.HeldA && h.Supervisor.FarmingStopStatuses().Single().Detail.Contains("Recorded synthetic delivery"));
            }
        }

        private static object Call(object target, string name, params object[] arguments)
        {
            try { return target.GetType().GetMethod(name, Flags).Invoke(target, arguments); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }

        private static void Replace<T>(VanillaClientState state, VanillaField field, T value)
        {
            var fields = state.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
            fields[field] = new StateValue<T>(value) { IsAvailable = true, Validation = StateValidation.Valid, LastObservedAtUtc = state.SampledAtUtc };
            state.Fields = fields;
        }
        private static object Get(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static void Assert(bool value, string reason = "Assertion failed") { if (!value) throw new Exception(reason); }
        private static void Throws(Action action)
        { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected InvalidOperationException."); }
        private static void Test(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
    }
}
