using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaServerOutageTests
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private static int passed, failed;

        internal static int Run()
        {
            Test("Confirmed server closure waits the complete fifteen minutes", ExactDeadline);
            Test("One shared server probe serializes all waiting accounts", SharedProbe);
            Test("Repeated closure observations cannot postpone the outage deadline", RepeatedConfirmation);
            Test("A healthy sibling cannot clear an outage or another account's probe", SiblingSuccess);
            Test("The original account cannot clear an outage before it owns a retry", SuccessRequiresProbe);
            Test("An unrelated failure cannot release or postpone the current probe", WrongFailure);
            Test("Failed outage probes wait fifteen minutes from completion", FailedProbe);
            Test("Outage retries remain fifteen minutes without a terminal retry budget", IndefiniteRetries);
            Test("Wall-clock changes do not authorize early server probes", WallClockChanges);
            Test("Monotonic rollback does not authorize an immediate server probe", ClockRollback);
            Test("STOP clears shared outage ownership and pending deadlines", Cancel);
            Test("Supervisor parks outage work without disturbing a healthy sibling", SupervisorParks);
            Test("Supervisor does not close either client before the shared deadline", SupervisorCooldown);
            Test("Due server probe retains one lease through close and replacement", SupervisorProbeLease);
            Test("A normal probe failure uses the outage interval instead of generic backoff", SupervisorProbeFailure);
            Test("Healthy sibling success cannot clear the supervisor outage", SupervisorSiblingSuccess);
            Test("Recovered probe preserves replacement intent for a sibling's old dialog", SupervisorSiblingPending);
            Test("STOP cancels a queued server-probe close without touching clients", SupervisorStop);
            Test("Settings changes cancel the queued server-probe close", SupervisorSettings);
            Test("Failed server-probe close retains its PID and waits fifteen minutes", SupervisorCloseFailure);
            Test("Server Closed needs two fresh terminal observations before shared waiting", TerminalConfirmation);
            Test("An unknown or different terminal message resets server closure confirmation", TerminalInterrupted);
            Test("Cold startup closes only the confirmed unavailable client then releases into waiting", ColdStartConfirmed);
            Test("Cold-start server closure cannot survive changed visual confirmation", ColdStartChanged);
            Test("STOP between cold-start server captures prevents any close", ColdStartCancelled);
            Test("Missing configuration cannot claim or strand the shared server probe", MissingConfiguration);
            Test("An abandoned probe owner releases the shared schedule without closing clients", AbandonedOwner);
            Test("Passive healthy adoption releases a probe without declaring server recovery", PassiveAdoption);
            Test("An adopted sibling awaiting its first gameplay sample remains untouched", AdoptedSibling);
            Test("Only known parked client IDs unblock a scheduled replacement launch", ParkedClientLaunchGate);
            Test("Capacity or unknown-client obstruction releases the reserved probe for fifteen minutes", ObstructedProbe);
            Console.WriteLine("Server outage regressions: {0} passed; {1} failed. Fake clocks, processes and queued workers only.", passed, failed);
            return failed;
        }

        private static VanillaServerOutagePolicy Closed(double seconds = 0)
        {
            var policy = new VanillaServerOutagePolicy();
            policy.Confirm("A", TimeSpan.FromSeconds(seconds), Epoch.AddSeconds(seconds));
            Assert(policy.Active, "Confirmed closure did not activate server-wide waiting.");
            return policy;
        }

        private static bool Probe(VanillaServerOutagePolicy policy, string account, double seconds)
        { return policy.TryBeginProbe(account, TimeSpan.FromSeconds(seconds), Epoch.AddSeconds(seconds)); }

        private static void ExactDeadline()
        {
            var policy = Closed();
            Assert(!Probe(policy, "A", 0) && !Probe(policy, "A", 899.999), "Server was retried before fifteen minutes.");
            Assert(Probe(policy, "A", 900) && policy.ProbeOwner == "A", "Due probe was never released.");
        }

        private static void SharedProbe()
        {
            var policy = Closed();
            Assert(Probe(policy, "B", 900), "A configured waiting sibling could not own the shared probe.");
            Assert(!Probe(policy, "A", 900) && !Probe(policy, "A", 5000), "Two clients owned a simultaneous probe.");
            policy.CompleteVerifiedRecovery("B");
            Assert(!policy.Active && policy.ProbeOwner == null, "Verified recovery did not release the shared outage.");
        }

        private static void RepeatedConfirmation()
        {
            var policy = Closed();
            for (int second = 1; second < 900; second++)
                policy.Confirm(second % 2 == 0 ? "A" : "B", TimeSpan.FromSeconds(second), Epoch.AddSeconds(second));
            Assert(Probe(policy, "A", 900), "Repeated captures perpetually postponed the original probe.");
            policy.Confirm("B", TimeSpan.FromSeconds(901), Epoch.AddSeconds(901));
            Assert(policy.ProbeOwner == "A", "An old sibling dialog stole active probe ownership.");
        }

        private static void SiblingSuccess()
        {
            var policy = Closed();
            policy.CompleteVerifiedRecovery("B");
            Assert(policy.Active, "Healthy sibling cleared the initial outage.");
            Assert(Probe(policy, "A", 900));
            policy.CompleteVerifiedRecovery("B");
            Assert(policy.Active && policy.ProbeOwner == "A", "Healthy sibling cleared another account's active probe.");
        }

        private static void SuccessRequiresProbe()
        {
            var policy = Closed();
            policy.CompleteVerifiedRecovery("A");
            Assert(policy.Active && !Probe(policy, "A", 899), "Unattempted account success bypassed the shared cooldown.");
        }

        private static void WrongFailure()
        {
            var policy = Closed();
            Assert(Probe(policy, "A", 900));
            policy.CompleteFailure("B", TimeSpan.FromSeconds(910), Epoch.AddSeconds(910));
            Assert(policy.ProbeOwner == "A" && !Probe(policy, "B", 1810), "Unrelated failure released an active probe.");
            policy.CompleteVerifiedRecovery("A");
            Assert(!policy.Active);
        }

        private static void FailedProbe()
        {
            var policy = Closed();
            Assert(Probe(policy, "A", 900));
            policy.CompleteFailure("A", TimeSpan.FromSeconds(940), Epoch.AddSeconds(940));
            Assert(policy.Active && policy.ProbeOwner == null, "Failure did not release the shared probe into waiting.");
            Assert(!Probe(policy, "B", 1839.999) && Probe(policy, "B", 1840), "Failed probe used generic short/exponential backoff.");
        }

        private static void IndefiniteRetries()
        {
            var policy = Closed();
            double finished = 0;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                string account = attempt % 2 == 0 ? "A" : "B";
                Assert(!Probe(policy, account, finished + 899.999), "Outage hot retry at attempt " + attempt);
                Assert(Probe(policy, account, finished + 900), "Outage retry budget/deadline prevented attempt " + attempt);
                finished += 905;
                policy.CompleteFailure(account, TimeSpan.FromSeconds(finished), Epoch.AddSeconds(finished));
            }
            Assert(policy.Active && policy.ProbeOwner == null, "Repeated server closure became a terminal failure.");
        }

        private static void WallClockChanges()
        {
            var policy = Closed();
            Assert(!policy.TryBeginProbe("A", TimeSpan.FromSeconds(1), Epoch.AddDays(7)), "Wall-clock jump bypassed fifteen-minute cooldown.");
            Assert(!policy.TryBeginProbe("A", TimeSpan.FromSeconds(899), Epoch.AddDays(-7)), "Wall-clock rollback authorized early retry.");
            Assert(policy.TryBeginProbe("A", TimeSpan.FromSeconds(900), Epoch.AddDays(-7)), "Wall-clock rollback indefinitely blocked a due retry.");
        }

        private static void ClockRollback()
        {
            var policy = Closed(100);
            Assert(!Probe(policy, "A", 50) && policy.Active, "Monotonic rollback released the outage gate.");
        }

        private static void Cancel()
        {
            foreach (bool probing in new[] { false, true })
            {
                var policy = Closed();
                if (probing) Assert(Probe(policy, "A", 900));
                policy.Cancel();
                Assert(!policy.Active && policy.ProbeOwner == null && policy.NextCheckAt == default(DateTimeOffset), "STOP left stale outage state.");
                policy.CompleteFailure("A", TimeSpan.FromSeconds(901), Epoch.AddSeconds(901));
                Assert(!policy.Active, "Late cancelled failure rearmed the outage.");
            }
        }

        private sealed class FakeEnvironment : IVanillaRecoveryRestartEnvironment
        {
            internal double Seconds;
            internal bool FailClose;
            internal readonly Queue<Action> Work = new Queue<Action>();
            internal readonly List<int> Closed = new List<int>();
            public DateTimeOffset UtcNow { get { return Epoch.AddSeconds(Seconds); } }
            public TimeSpan MonotonicNow { get { return TimeSpan.FromSeconds(Seconds); } }
            public DateTime GetStartTimeUtc(int pid) { return Epoch.UtcDateTime; }
            public void Queue(Action work) { Work.Enqueue(work); }
            public void CloseClient(int pid, DateTime expected, Func<bool> cancelled, Action<Action> ownedStep, bool immediate = false)
            {
                if (cancelled()) throw new OperationCanceledException();
                if (FailClose) throw new InvalidOperationException("Synthetic close denial");
                ownedStep(() => { if (cancelled()) throw new OperationCanceledException(); Closed.Add(pid); });
            }
        }

        private sealed class Harness : IDisposable
        {
            internal readonly FakeEnvironment Clock = new FakeEnvironment();
            internal readonly VanillaReconnectSupervisor Supervisor;
            internal readonly object A, B;
            internal VanillaServerOutagePolicy Outage { get { return (VanillaServerOutagePolicy)Get(Supervisor, "serverOutage"); } }
            private readonly string root = Path.Combine(Path.GetTempPath(), "4R-server-outage-" + Guid.NewGuid().ToString("N"));

            internal Harness()
            {
                Supervisor = new VanillaReconnectSupervisor(root, Clock);
                var settings = Supervisor.Settings;
                // Existing test assembly is only a path sentinel; no launcher is ever invoked.
                settings.LaunchExecutable = typeof(VanillaServerOutageTests).Assembly.Location;
                foreach (var account in settings.Accounts)
                {
                    account.UserName = "synthetic-" + account.Id;
                    account.ProtectedPassword = "inert-placeholder-never-decrypted";
                    account.CharacterSlot = 1;
                    account.ProxyNeedsConfiguration = false;
                }
                Set(Supervisor, "settings", settings);
                var runtimes = (IDictionary)Get(Supervisor, "runtimes");
                A = runtimes[Supervisor.Settings.Accounts[0].Id];
                B = runtimes[Supervisor.Settings.Accounts[1].Id];
                foreach (var pair in new[] { Tuple.Create(A, 101), Tuple.Create(B, 102) })
                {
                    Set(pair.Item1, "ProcessId", (int?)pair.Item2);
                    Set(pair.Item1, "ResumeSent", true);
                    Set(pair.Item1, "HasBeenOnline", true);
                    Set(pair.Item1, "Stage", VanillaReconnectStage.Online);
                    string id = ((VanillaReconnectAccount)Get(pair.Item1, "Account")).Id;
                    Set(pair.Item1, "Account", settings.Accounts.Single(account => account.Id == id));
                }
                // Do not call Start or Tick: neither process enumeration nor timers run.
                Set(Supervisor, "running", true);
            }

            internal void Confirm(object runtime)
            {
                Call(Supervisor, "ConfirmServerOutageLocked", runtime);
                Call(Supervisor, "ParkForServerOutageLocked", runtime);
            }
            internal bool Begin(object runtime) { return (bool)Call(Supervisor, "MayStartServerOutageProbeLocked", runtime); }
            internal void QueueClose(object runtime)
            {
                Call(Supervisor, "QueueClientRestart", runtime, Clock.UtcNow, "Synthetic server probe", false,
                    (Func<DateTime>)(() => Epoch.UtcDateTime));
            }
            internal void Success(object runtime)
            {
                Set(runtime, "ScriptRunning", false);
                Set(runtime, "HasBeenOnline", true);
                Set(runtime, "ResumeSent", true);
                Call(Supervisor, "CompleteAutobattleRecoverySuccessLocked", runtime);
            }
            public void Dispose()
            {
                Supervisor.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void SupervisorParks()
        {
            using (var h = new Harness())
            {
                Set(h.A, "ScriptRunning", true);
                Set(h.A, "RecoveryOwned", true);
                h.Confirm(h.A);
                Assert((bool)Get(h.A, "ServerOutagePending") && !(bool)Get(h.A, "ScriptRunning") && !(bool)Get(h.A, "RecoveryOwned"));
                Assert(Get(h.A, "NextRecoveryAt") == null && (VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.WaitingForServer);
                Assert((int?)Get(h.B, "ProcessId") == 102 && (bool)Get(h.B, "HasBeenOnline") && (bool)Get(h.B, "ResumeSent")
                    && !(bool)Get(h.B, "ServerOutagePending") && (VanillaReconnectStage)Get(h.B, "Stage") == VanillaReconnectStage.Online,
                    "Server outage disturbed a healthy sibling.");
                Assert(h.Clock.Work.Count == 0 && h.Clock.Closed.Count == 0);
            }
        }

        private static void SupervisorCooldown()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A);
                foreach (double seconds in new[] { 0, 30, 180, 899.999 })
                {
                    h.Clock.Seconds = seconds;
                    h.QueueClose(h.A);
                    h.QueueClose(h.B);
                    Assert(h.Clock.Work.Count == 0, "Generic recovery bypassed outage cooldown at " + seconds + "s.");
                }
            }
        }

        private static void SupervisorProbeLease()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Confirm(h.B); h.Clock.Seconds = 900;
                h.QueueClose(h.A); h.QueueClose(h.B);
                Assert(h.Clock.Work.Count == 1, "Expected exactly one shared outage probe.");
                h.Clock.Work.Dequeue()();
                Assert(h.Clock.Closed.SequenceEqual(new[] { 101 }) && Get(h.A, "ProcessId") == null);
                Assert((bool)Get(h.A, "RecoveryOwned"), "Probe close released the replacement/login lease early.");
                h.QueueClose(h.B);
                Assert(h.Clock.Work.Count == 0 && (int?)Get(h.B, "ProcessId") == 102);
                Assert(h.Outage.ProbeOwner == ((VanillaReconnectAccount)Get(h.A, "Account")).Id);
            }
        }

        private static void SupervisorProbeFailure()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Clock.Seconds = 900; Assert(h.Begin(h.A));
                h.Clock.Seconds = 970;
                Call(h.Supervisor, "ScheduleRecoveryFailureLocked", h.A, h.Clock.UtcNow, "Synthetic launcher failure");
                Assert(!(bool)Get(h.A, "RecoveryOwned") && !(bool)Get(h.A, "ScriptRunning") && h.Outage.ProbeOwner == null);
                Assert((int)Get(h.A, "RecoveryFailures") == 0, "Outage failure advanced generic exponential backoff.");
                h.Clock.Seconds = 1869.999; Assert(!h.Begin(h.B));
                h.Clock.Seconds = 1870; Assert(h.Begin(h.B));
            }
        }

        private static void SupervisorSiblingSuccess()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Clock.Seconds = 900; Assert(h.Begin(h.A));
                h.Success(h.B);
                Assert(h.Outage.Active && (bool)Get(h.A, "ServerOutagePending"), "Unrelated healthy sibling cleared outage state.");
            }
        }

        private static void SupervisorSiblingPending()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Confirm(h.B); h.Clock.Seconds = 900; Assert(h.Begin(h.A));
                h.Success(h.A);
                Assert(!h.Outage.Active && !(bool)Get(h.A, "ServerOutagePending"), "Recovered probe remained blocked.");
                Assert((bool)Get(h.B, "ServerOutagePending"), "Old sibling dialog lost its pending replacement intent and would relatch the outage.");
                h.QueueClose(h.B);
                Assert(h.Clock.Work.Count == 1, "Recovered server did not release the sibling's pending replacement.");
                h.Clock.Work.Dequeue()();
                Assert(h.Clock.Closed.SequenceEqual(new[] { 102 }) && (int?)Get(h.A, "ProcessId") == 101 && !h.Outage.Active);
            }
        }

        private static void SupervisorStop()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Clock.Seconds = 900; h.QueueClose(h.A);
                Assert(h.Clock.Work.Count == 1);
                h.Supervisor.Stop();
                h.Clock.Work.Dequeue()();
                Assert(!h.Outage.Active && !(bool)Get(h.A, "ServerOutagePending") && h.Clock.Closed.Count == 0
                    && (int?)Get(h.A, "ProcessId") == 101 && (int?)Get(h.B, "ProcessId") == 102,
                    "STOP allowed delayed server-probe input/close.");
            }
        }

        private static void SupervisorSettings()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Clock.Seconds = 900; h.QueueClose(h.A);
                Assert(h.Clock.Work.Count == 1);
                Set(h.Supervisor, "running", false); // Apply must not create a live polling timer in this test.
                h.Supervisor.Apply(h.Supervisor.Settings, false);
                h.Clock.Work.Dequeue()();
                Assert(!h.Outage.Active && h.Clock.Closed.Count == 0, "Settings invalidation did not cancel the old outage probe.");
            }
        }

        private static void SupervisorCloseFailure()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Clock.Seconds = 900; h.QueueClose(h.A);
                Assert(h.Clock.Work.Count == 1);
                h.Clock.FailClose = true; h.Clock.Seconds = 904; h.Clock.Work.Dequeue()();
                Assert((int?)Get(h.A, "ProcessId") == 101 && !((bool)Get(h.A, "RecoveryOwned")) && h.Outage.ProbeOwner == null);
                h.Clock.Seconds = 1803.999; Assert(!h.Begin(h.A));
                h.Clock.Seconds = 1804; Assert(h.Begin(h.A));
            }
        }

        private static void ObserveTerminal(Harness h, VanillaVisualState visual)
        {
            Call(h.Supervisor, "HandleTerminalVisual", h.A, visual, h.Clock.UtcNow,
                (Func<DateTime>)(() => Epoch.UtcDateTime));
        }

        private static void TerminalConfirmation()
        {
            using (var h = new Harness())
            {
                ObserveTerminal(h, VanillaVisualState.ServerClosed);
                Assert(!h.Outage.Active && h.Clock.Work.Count == 0, "One capture authorized server-down recovery.");
                ObserveTerminal(h, VanillaVisualState.ServerClosed);
                Assert(!h.Outage.Active, "Duplicate timestamp counted as independent confirmation.");
                h.Clock.Seconds = 1;
                ObserveTerminal(h, VanillaVisualState.ServerClosed);
                Assert(h.Outage.Active && (VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.WaitingForServer);
                Assert(h.Clock.Work.Count == 0 && h.Clock.Closed.Count == 0, "Confirmed closure caused an immediate connection retry.");
            }
        }

        private static void TerminalInterrupted()
        {
            foreach (var intermediate in new[] { VanillaVisualState.Unknown, VanillaVisualState.ModalDialog,
                VanillaVisualState.Gameplay, VanillaVisualState.Disconnected })
            using (var h = new Harness())
            {
                ObserveTerminal(h, VanillaVisualState.ServerClosed);
                h.Clock.Seconds = 1; ObserveTerminal(h, intermediate);
                h.Clock.Seconds = 2; ObserveTerminal(h, VanillaVisualState.ServerClosed);
                Assert(!h.Outage.Active && h.Clock.Work.Count == 0, "Changed state preserved stale closure proof: " + intermediate);
                h.Clock.Seconds = 3; ObserveTerminal(h, VanillaVisualState.ServerClosed);
                Assert(h.Outage.Active && h.Clock.Work.Count == 0, "Fresh matching pair was not recognized after " + intermediate);
            }
        }

        private static object CloseCold(Harness h, Func<VanillaVisualState> read, Action<int> pause)
        {
            Set(h.Supervisor, "running", false);
            Set(h.Supervisor, "hardenedStartupRunning", true);
            return Call(h.Supervisor, "CloseTerminalBeforeStartup", h.A, 101, 0, h.Supervisor.Settings,
                read, (Func<DateTime>)(() => Epoch.UtcDateTime), pause);
        }

        private static void ColdStartConfirmed()
        {
            using (var h = new Harness())
            {
                Assert((bool)CloseCold(h, () => VanillaVisualState.ServerClosed, ms => h.Clock.Seconds += ms / 1000.0));
                Assert(h.Clock.Closed.SequenceEqual(new[] { 101 }) && Get(h.A, "ProcessId") == null && (int?)Get(h.B, "ProcessId") == 102);
                Assert(h.Outage.Active && h.Outage.ProbeOwner == null && (bool)Get(h.A, "ServerOutagePending")
                    && !(bool)Get(h.A, "RecoveryOwned") && !(bool)Get(h.A, "ScriptRunning"), "Cold-start outage retained its input lease during cooldown.");
                Assert((VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.WaitingForServer && !h.Begin(h.A));
            }
        }

        private static void ColdStartChanged()
        {
            using (var h = new Harness())
            {
                int reads = 0;
                Expect<InvalidOperationException>(() => CloseCold(h,
                    () => ++reads == 1 ? VanillaVisualState.ServerClosed : VanillaVisualState.ModalDialog,
                    ms => h.Clock.Seconds += ms / 1000.0));
                Assert(!h.Outage.Active && h.Clock.Closed.Count == 0, "Unstable cold-start dialog authorized close/outage state.");
            }
        }

        private static void ColdStartCancelled()
        {
            using (var h = new Harness())
            {
                Expect<OperationCanceledException>(() => CloseCold(h, () => VanillaVisualState.ServerClosed, ms => h.Supervisor.Stop()));
                Assert(!h.Outage.Active && h.Clock.Closed.Count == 0 && (int?)Get(h.A, "ProcessId") == 101);
            }
        }

        private static void MissingConfiguration()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Confirm(h.B); h.Clock.Seconds = 900;
                ((VanillaReconnectAccount)Get(h.A, "Account")).ProtectedPassword = null;
                h.QueueClose(h.A);
                Assert(h.Outage.ProbeOwner == null && h.Clock.Work.Count == 0, "Incomplete account reserved the only outage probe.");
                h.QueueClose(h.B);
                Assert(h.Clock.Work.Count == 1 && h.Outage.ProbeOwner == ((VanillaReconnectAccount)Get(h.B, "Account")).Id,
                    "Incomplete account stranded the configured sibling.");
            }
        }

        private static void AbandonedOwner()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Clock.Seconds = 900; h.QueueClose(h.A);
                Assert(h.Clock.Work.Count == 1);
                ((VanillaReconnectAccount)Get(h.A, "Account")).Enabled = false;
                Call(h.Supervisor, "ReconcileServerOutageOwnerLocked");
                h.Clock.Work.Dequeue()();
                Assert(h.Outage.Active && h.Outage.ProbeOwner == null && h.Clock.Closed.Count == 0);
                h.Clock.Seconds = 1799.999; Assert(!h.Begin(h.B));
                h.Clock.Seconds = 1800; Assert(h.Begin(h.B));
            }
        }

        private static void PassiveAdoption()
        {
            using (var h = new Harness())
            {
                h.Confirm(h.A); h.Clock.Seconds = 900; Assert(h.Begin(h.A));
                Call(h.Supervisor, "Bind", h.A, 201, false, "Synthetic passive healthy identity");
                Assert(h.Outage.Active && h.Outage.ProbeOwner == null && !(bool)Get(h.A, "ServerOutagePending")
                    && (bool)Get(h.A, "ResumeSent") && (int?)Get(h.A, "ProcessId") == 201,
                    "Passive adoption falsely cleared server state or armed input against an existing healthy client.");
                Assert(h.Clock.Work.Count == 0 && h.Clock.Closed.Count == 0);
            }
        }

        private static void AdoptedSibling()
        {
            using (var h = new Harness())
            {
                Set(h.B, "HasBeenOnline", false);
                Set(h.B, "ResumeSent", true);
                Set(h.B, "Stage", VanillaReconnectStage.WaitingForGameplay);
                h.Confirm(h.A);
                Assert(!(bool)Get(h.B, "ServerOutagePending") && (int?)Get(h.B, "ProcessId") == 102
                    && (bool)Get(h.B, "ResumeSent") && (VanillaReconnectStage)Get(h.B, "Stage") == VanillaReconnectStage.WaitingForGameplay,
                    "An identity-matched adopted sibling was scheduled for restart before its first gameplay probe.");
            }
        }

        private static void ParkedClientLaunchGate()
        {
            using (var h = new Harness())
            {
                h.Supervisor.SetCharacterSource(() => new VanillaCharacterIdentity[0]);
                h.Confirm(h.A); h.Confirm(h.B); h.Clock.Seconds = 900; h.QueueClose(h.A);
                Assert(h.Clock.Work.Count == 1); h.Clock.Work.Dequeue()();
                Assert((bool)Call(h.Supervisor, "IsParkedServerOutageClient", 102)
                    && !(bool)Call(h.Supervisor, "IsParkedServerOutageClient", 999), "Parked-client exception accepted an unrelated PID.");
                Assert((bool)Call(h.Supervisor, "CanLaunch", h.A, 1, h.Clock.UtcNow), "Known parked sibling prevented the due replacement from launching.");
                Set(h.B, "ServerOutagePending", false);
                Assert(!(bool)Call(h.Supervisor, "CanLaunch", h.A, 1, h.Clock.UtcNow), "Unknown running client bypassed the duplicate-launch guard.");
                Assert(h.Clock.Closed.SequenceEqual(new[] { 101 }), "Launch preflight disturbed the parked sibling.");
            }
        }

        private static void ObstructedProbe()
        {
            foreach (int aliveCount in new[] { 1, 2 })
            using (var h = new Harness())
            {
                h.Supervisor.SetCharacterSource(() => new VanillaCharacterIdentity[0]);
                h.Confirm(h.A); h.Clock.Seconds = 900; h.QueueClose(h.A);
                Assert(h.Clock.Work.Count == 1); h.Clock.Work.Dequeue()();
                h.Clock.Seconds = 905;
                Assert(!(bool)Call(h.Supervisor, "CanLaunch", h.A, aliveCount, h.Clock.UtcNow));
                Assert(h.Outage.Active && h.Outage.ProbeOwner == null && !(bool)Get(h.A, "RecoveryOwned")
                    && !(bool)Get(h.A, "ScriptRunning") && (VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.WaitingForServer,
                    "An obstructed availability check retained its recovery lease indefinitely.");
                Assert(h.Clock.Work.Count == 0 && h.Clock.Closed.SequenceEqual(new[] { 101 }) && (int?)Get(h.B, "ProcessId") == 102,
                    "Capacity checks disturbed another client.");
                h.Clock.Seconds = 1804.999; Assert(!h.Begin(h.A));
                h.Clock.Seconds = 1805; Assert(h.Begin(h.A));
            }
        }

        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }

        private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        private static object Get(object target, string name) { return target.GetType().GetField(name, Fields).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Fields).SetValue(target, value); }
        private static object Call(object target, string name, params object[] arguments)
        {
            try { return target.GetType().GetMethod(name, Fields).Invoke(target, arguments); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }

        private static void Assert(bool condition, string message = "Assertion failed")
        { if (!condition) throw new Exception(message); }

        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
    }
}
