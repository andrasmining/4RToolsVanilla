using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaStopNotificationTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static int passed, failed;

        public static int Run()
        {
            passed = failed = 0;
            Test("Emergency notice survives closed PID and uses independent mail opt-in", EmergencyClosedDelivery);
            Test("Current emergency mail opt-out suppresses a retained notice", EmergencyOptOut);
            Test("Completion mail requires both current masters and both character switches", CompletionPolicy);
            Test("Unrequested stop mail is not enabled retroactively", Unrequested);
            Test("Stop mail rejects removed, disabled and edited character identities", CharacterAuthorization);
            Test("Stop mail rechecks settings after reserving delivery", RecheckPolicy);
            Test("Inactive stop mail dispatcher sends no queued notification", Inactive);
            Test("SMTP failure retains closed-client hold and bounded retry", SmtpFailure);
            Test("Invalid saved SMTP transport never reaches the sender", InvalidTransport);
            Test("Concurrent stop mail dispatchers reserve a single delivery", ConcurrentDelivery);
            Test("Sent stop notice remains deduplicated after restart", SentRestart);
            Test("Successful SMTP with failed result persistence is not resent", SentPersistenceFailure);
            Test("Interrupted stop mail delivery is not automatically resent", SendingRestart);
            Test("Explicit completion hold clear cancels pending notice", ClearedCompletion);
            Test("Clearing a stop after email reservation cancels unissued delivery", ClearAfterReservation);
            Test("Restored completion hold blocks cold launch, cleanup and outage wait", CompletionStartupGuards);
            Console.WriteLine("Stop notifications: {0} passed; {1} failed. Fake SMTP, isolated records and inert supervisors only.", passed, failed);
            return failed;
        }

        private sealed class InertEnvironment : IVanillaRecoveryRestartEnvironment
        {
            internal int NativeCalls;
            public DateTimeOffset UtcNow { get { return DateTimeOffset.UtcNow; } }
            public TimeSpan MonotonicNow { get { return TimeSpan.Zero; } }
            public DateTime GetStartTimeUtc(int pid) { NativeCalls++; throw new InvalidOperationException("Native process access is forbidden in notification tests."); }
            public void Queue(Action work) { NativeCalls++; throw new InvalidOperationException("Native work is forbidden in notification tests."); }
            public void CloseClient(int pid, DateTime expected, Func<bool> cancelled, Action<Action> owned, bool immediate = false)
            { NativeCalls++; throw new InvalidOperationException("Native close is forbidden in notification tests."); }
        }

        private sealed class Harness : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "4R-stop-mail-" + Guid.NewGuid().ToString("N"));
            internal readonly InertEnvironment Environment = new InertEnvironment();
            internal readonly List<string> Subjects = new List<string>(), Bodies = new List<string>();
            internal readonly VanillaFarmingStopNotice Notice;
            internal VanillaReconnectSupervisor Supervisor;
            internal VanillaWeightAlertSettings Mail = Transport();
            internal bool Active = true;
            internal string Ledger { get { return Path.Combine(Root, "VanillaReconnect", "farming-stops.json"); } }
            internal Harness(VanillaFarmingStopKind kind = VanillaFarmingStopKind.Completed,
                bool requested = true, string emailState = "pending")
            {
                var settings = VanillaReconnectSettings.CreateDefault();
                settings.AutoRecover = false;
                settings.FarmingEmergency.SendEmail = true;
                for (int i = 0; i < settings.Accounts.Count; i++)
                {
                    var account = settings.Accounts[i];
                    account.Enabled = true;
                    account.UserName = "mail-test-user-" + i;
                    account.CharacterName = "Mail Test Farmer " + i;
                    account.CartMaintenanceEnabled = true;
                    account.WeightEmailEnabled = true;
                }
                new VanillaReconnectStore(Root).Save(settings);
                var first = settings.Accounts[0];
                Notice = new VanillaFarmingStopNotice
                {
                    EventId = Guid.NewGuid().ToString("N"), Kind = kind, AccountId = first.Id,
                    UserName = first.UserName, CharacterName = first.CharacterName,
                    ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Evidence = kind == VanillaFarmingStopKind.Emergency
                        ? "Weight 510/1000 (51%); SP 240/1000 (24%); HP 490/1000 (49%); limits Weight >50%, SP <25%, HP <50%"
                        : "Cart 9900/10000 (99%); Weight 500/1000 (50%); Autobattle STOP verified",
                    Detail = kind == VanillaFarmingStopKind.Emergency ? "Emergency stop triggered." : "Farming complete after verified STOP.",
                    CloseRequested = true, CloseState = "closed", CloseDetail = "Affected client exit confirmed.",
                    HoldActive = true, EmailRequested = requested, EmailState = emailState,
                    EmailDetail = "Waiting for notification dispatch.",
                    EmailAttemptId = emailState == "sending" ? Guid.NewGuid().ToString("N") : null
                };
                File.WriteAllText(Ledger, JsonConvert.SerializeObject(new { Version = 1, Stops = new[] { Notice } }));
                Supervisor = new VanillaReconnectSupervisor(Root, Environment);
                Supervisor.SetCharacterSource(() => new VanillaCharacterIdentity[0]);
                Supervisor.SetPositionSource(pid => null, pid => { });
            }

            internal VanillaFarmingStopEmailDispatcher Dispatcher(Action<VanillaWeightAlertSettings, string, string> send = null,
                Func<VanillaWeightAlertSettings> current = null, Func<bool> active = null)
            {
                return new VanillaFarmingStopEmailDispatcher(Supervisor, current ?? (() => Mail.Clone()),
                    send ?? ((settings, subject, body) => { Subjects.Add(subject); Bodies.Add(body); }), active ?? (() => Active));
            }
            internal void EditAccount(Action<VanillaReconnectAccount> edit)
            {
                var settings = Supervisor.Settings; edit(settings.Accounts[0]); Supervisor.Apply(settings, true);
            }
            internal JObject Stored()
            { return (JObject)JObject.Parse(File.ReadAllText(Ledger))["Stops"].Single(stop => (string)stop["EventId"] == Notice.EventId); }
            internal void Restart()
            {
                Supervisor.Dispose(); Supervisor = new VanillaReconnectSupervisor(Root, Environment);
                Supervisor.SetCharacterSource(() => new VanillaCharacterIdentity[0]);
                Supervisor.SetPositionSource(pid => null, pid => { });
            }
            public void Dispose()
            {
                Supervisor?.Dispose();
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
        }

        private static VanillaWeightAlertSettings Transport()
        {
            return new VanillaWeightAlertSettings
            {
                Enabled = true, AutoCartEnabled = true, SmtpHost = "smtp.example.invalid", SmtpPort = 587,
                FromAddress = "sender@example.invalid", ToAddress = "recipient@example.invalid"
            };
        }

        private static void EmergencyClosedDelivery()
        {
            using (var h = new Harness(VanillaFarmingStopKind.Emergency))
            {
                h.Mail.Enabled = h.Mail.AutoCartEnabled = false;
                h.EditAccount(row => { row.WeightEmailEnabled = false; row.CartMaintenanceEnabled = false; });
                Assert(h.Supervisor.Statuses().All(status => !status.ProcessId.HasValue), "Test unexpectedly attached to a client.");
                var dispatcher = h.Dispatcher(); dispatcher.Poll(); dispatcher.Poll();
                Assert(h.Subjects.Count == 1 && h.Subjects[0] == "EMERGENCY STOP: " + h.Notice.CharacterName);
                Assert(h.Bodies[0].Contains(h.Notice.Evidence) && h.Bodies[0].Contains("Stopped at: ")
                    && h.Bodies[0].Contains(h.Notice.CloseDetail) && h.Bodies[0].Contains("Automatic relaunch: blocked"), "Notice lost its retained cause, time or close outcome.");
                Assert((string)h.Stored()["EmailState"] == "sent" && h.Environment.NativeCalls == 0);
            }
        }

        private static void EmergencyOptOut()
        {
            using (var h = new Harness(VanillaFarmingStopKind.Emergency))
            {
                var limits = h.Supervisor.FarmingEmergencySettings; limits.SendEmail = false;
                h.Supervisor.SaveFarmingEmergencySettings(limits); h.Dispatcher().Poll();
                Assert(h.Subjects.Count == 0, "Normal Mail switches overrode emergency mail opt-out.");
            }
        }

        private static void CompletionPolicy()
        {
            foreach (bool mailMaster in new[] { false, true })
            foreach (bool cartMaster in new[] { false, true })
            foreach (bool rowMail in new[] { false, true })
            foreach (bool rowCart in new[] { false, true })
            using (var h = new Harness())
            {
                h.Mail.Enabled = mailMaster; h.Mail.AutoCartEnabled = cartMaster;
                h.EditAccount(row => { row.WeightEmailEnabled = rowMail; row.CartMaintenanceEnabled = rowCart; });
                h.Dispatcher().Poll();
                Assert(h.Subjects.Count == (mailMaster && cartMaster && rowMail && rowCart ? 1 : 0), "Completion mail policy truth table failed.");
                Assert(h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]), "Mail policy changed the completed close hold.");
            }
        }

        private static void Unrequested()
        {
            foreach (VanillaFarmingStopKind kind in new[] { VanillaFarmingStopKind.Emergency, VanillaFarmingStopKind.Completed })
            using (var h = new Harness(kind, false))
            { h.Dispatcher().Poll(); Assert(h.Subjects.Count == 0, "An unrequested historic notification was sent."); }
        }

        private static void CharacterAuthorization()
        {
            foreach (int change in Enumerable.Range(0, 4)) using (var h = new Harness())
            {
                var settings = h.Supervisor.Settings;
                if (change == 0) settings.Accounts[0].Enabled = false;
                if (change == 1) settings.Accounts[0].UserName = "changed-user";
                if (change == 2) settings.Accounts[0].CharacterName = "Changed Character";
                if (change == 3) settings.Accounts.RemoveAt(0);
                h.Supervisor.Apply(settings, true); h.Dispatcher().Poll();
                Assert(h.Subjects.Count == 0, "Another row or reused account ID authorized a stale stop notification.");
            }
        }

        private static void RecheckPolicy()
        {
            using (var h = new Harness())
            {
                int reads = 0;
                h.Dispatcher(current: () => { var value = h.Mail.Clone(); if (++reads >= 2) value.Enabled = false; return value; }).Poll();
                Assert(reads >= 2 && h.Subjects.Count == 0, "Queued settings were not rechecked after reservation.");
                Assert((string)h.Stored()["EmailState"] != "sent");
            }
        }

        private static void Inactive()
        {
            using (var h = new Harness())
            {
                h.Active = false; h.Dispatcher().Poll(); Assert(h.Subjects.Count == 0);
                int activeReads = 0;
                h.Dispatcher(active: () => ++activeReads < 3).Poll();
                Assert(h.Subjects.Count == 0 && activeReads >= 3, "Stopping after reservation still sent mail.");
            }
        }

        private static void SmtpFailure()
        {
            using (var h = new Harness())
            {
                int attempts = 0;
                var before = DateTimeOffset.UtcNow;
                var dispatcher = h.Dispatcher((settings, subject, body) => { attempts++; throw new IOException("Synthetic SMTP outage"); });
                dispatcher.Poll(); dispatcher.Poll();
                var stored = h.Stored();
                Assert(attempts == 1 && (string)stored["EmailState"] == "failed", "Failed mail retried immediately or was reported as sent.");
                Assert((DateTimeOffset)stored["NextEmailAttemptAt"] >= before.AddMinutes(5), "Mail retry backoff was lost.");
                Assert((string)stored["CloseState"] == "closed" && (bool)stored["HoldActive"]
                    && h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]), "SMTP failure undid the closed-client hold.");
                Assert(h.Environment.NativeCalls == 0);
            }
        }

        private static void InvalidTransport()
        {
            using (var h = new Harness())
            {
                h.Mail.SmtpHost = ""; h.Dispatcher().Poll();
                Assert(h.Subjects.Count == 0 && (string)h.Stored()["EmailState"] == "failed", "Invalid SMTP reached the sender or hid its error.");
            }
        }

        private static void ConcurrentDelivery()
        {
            using (var h = new Harness())
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int sends = 0;
                var first = h.Dispatcher((settings, subject, body) =>
                { Interlocked.Increment(ref sends); entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("Test release timed out."); });
                var second = h.Dispatcher((settings, subject, body) => Interlocked.Increment(ref sends));
                Task work = Task.Run(() => first.Poll());
                try
                {
                    Assert(entered.Wait(5000), "First fake SMTP send never began.");
                    first.Poll(); second.Poll(); Assert(sends == 1, "Concurrent dispatch duplicated an in-flight stop notice.");
                }
                finally { release.Set(); Assert(work.Wait(5000), "Fake SMTP worker did not finish."); }
                second.Poll(); Assert(sends == 1 && (string)h.Stored()["EmailState"] == "sent");
            }
        }

        private static void SentRestart()
        {
            using (var h = new Harness())
            {
                h.Dispatcher().Poll(); h.Restart(); h.Dispatcher().Poll();
                Assert(h.Subjects.Count == 1 && h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]));
            }
        }

        private static void SendingRestart()
        {
            using (var h = new Harness(emailState: "sending"))
            {
                h.Dispatcher().Poll(); h.Restart(); h.Dispatcher().Poll();
                Assert(h.Subjects.Count == 0 && h.Supervisor.PendingFarmingStopEmails().Count == 0,
                    "An uncertain interrupted SMTP delivery was resent automatically.");
            }
        }

        private static void SentPersistenceFailure()
        {
            using (var h = new Harness())
            {
                int sends = 0;
                string blockedTemporary = h.Ledger + ".tmp";
                try
                {
                    h.Dispatcher((settings, subject, body) =>
                    { sends++; Directory.CreateDirectory(blockedTemporary); }).Poll();
                    Assert(sends == 1 && (string)h.Stored()["EmailState"] == "sending",
                        "Test did not interrupt persistence after the reserved fake SMTP send.");
                    h.Dispatcher((settings, subject, body) => sends++).Poll();
                    Assert(sends == 1, "Result-persistence failure caused a duplicate send in the same process.");
                }
                finally { if (Directory.Exists(blockedTemporary)) Directory.Delete(blockedTemporary); }
                h.Restart(); h.Dispatcher((settings, subject, body) => sends++).Poll();
                Assert(sends == 1 && h.Supervisor.PendingFarmingStopEmails().Count == 0,
                    "Uncertain durable delivery was resent after restart.");
            }
        }

        private static void ClearedCompletion()
        {
            using (var h = new Harness())
            {
                h.Supervisor.ClearWeightManualHolds(); h.Dispatcher().Poll();
                Assert(h.Subjects.Count == 0 && !h.Supervisor.FarmingCompletionHeld(h.Supervisor.Settings.Accounts[0]));
            }
        }

        private static void CompletionStartupGuards()
        {
            using (var h = new Harness())
            {
                var settings = h.Supervisor.Settings; var account = settings.Accounts[0];
                Assert((bool)Invoke(h.Supervisor, "StartupAccountCancelled", 0, account));
                Assert(!(bool)Invoke(h.Supervisor, "StartupAccountCancelled", 0, settings.Accounts[1]), "Completed character blocked its healthy sibling.");
                Cancelled(() => Invoke(h.Supervisor, "RunOneColdStart", 0, account, settings, 1, 2));
                Cancelled(() => Invoke(h.Supervisor, "RunOneColdStartAttempt", 0, account, settings, 1, 2));
                var runtimes = (IDictionary)Field(h.Supervisor, "runtimes"); var runtime = runtimes[account.Id];
                Cancelled(() => Invoke(h.Supervisor, "WaitForStartupServerAvailability", 0, runtime));
                Cancelled(() => Invoke(h.Supervisor, "CloseColdStartClientForRestart", 0, runtime, 1, new VanillaStartupCloseIdentity(), "test"));
                Assert(h.Environment.NativeCalls == 0, "Restored completed hold allowed native launcher/close work.");
            }
        }

        private static void ClearAfterReservation()
        {
            foreach (VanillaFarmingStopKind kind in new[] { VanillaFarmingStopKind.Emergency, VanillaFarmingStopKind.Completed })
            using (var h = new Harness(kind))
            {
                int reads = 0; bool clearedReserved = false;
                h.Dispatcher(current: () =>
                {
                    if (++reads == 2)
                    {
                        Assert((string)h.Stored()["EmailState"] == "sending", "Clear did not occur after durable reservation.");
                        if (kind == VanillaFarmingStopKind.Emergency) h.Supervisor.ClearFarmingEmergencyHolds();
                        else h.Supervisor.ClearWeightManualHolds();
                        clearedReserved = true;
                    }
                    return h.Mail.Clone();
                }).Poll();
                Assert(clearedReserved && !(bool)h.Stored()["HoldActive"] && h.Subjects.Count == 0,
                    "An explicitly cleared stop still dispatched its reserved notification.");
                Assert((string)h.Stored()["EmailState"] != "sent");
            }
        }

        private static object Field(object value, string name) { return value.GetType().GetField(name, Flags).GetValue(value); }
        private static object Invoke(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethod(name, Flags).Invoke(value, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        private static void Cancelled(Action action)
        { try { action(); } catch (OperationCanceledException) { return; } throw new Exception("A held character was not cancelled before startup work."); }
        private static void Assert(bool value, string detail = "Assertion failed") { if (!value) throw new Exception(detail); }
        private static void Test(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
    }
}
