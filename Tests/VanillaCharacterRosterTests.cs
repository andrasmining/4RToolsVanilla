using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaCharacterRosterTests
    {
        private static int passed, failed;
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private static readonly Guid Session = Guid.NewGuid();
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        internal static int Run()
        {
            Test("Incomplete identity defers discovery instead of adding a username-less row", DiscoverUnknown);
            Test("Verified username and one-based slot populate a discovered row", DiscoverKnown);
            Test("Repeated startup discovery preserves description, password, proxy and enabled state", DiscoverTwice);
            Test("Two characters on the same username remain distinct rows", SharedLogin);
            Test("Missing fields alone are enriched, not user choices", Enrichment);
            Test("A username alone cannot identify a character", UsernameAlone);
            Test("Legacy username plus verified slot learns a character without changing its ID", LegacyMatch);
            Test("Ambiguous legacy rows are not auto-merged", AmbiguousLegacy);
            Test("Contradictory username or slot cannot claim a named character", ConflictingIdentity);
            Test("Ambiguous duplicate live names are not imported or assigned", DuplicateLive);
            Test("Stale, future, empty and malformed character observations are ignored", InvalidObservations);
            Test("Removed discovered character stays removed during this app session", Removed);
            Test("Only untouched synthetic defaults are removed after discovery", EmptyDefaults);
            Test("A third enabled character is rejected", EnabledLimit);
            Test("Duplicate username-character pairs are rejected but shared usernames are allowed", DuplicateNames);
            Test("Unknown slot remains null through legacy JSON cloning", UnknownSlot);
            Test("Screenshot legacy rows learn names without a memory slot or extra rows", LegacyWithoutSlot);
            Test("Empty old discovered duplicates fold into configured row IDs", LegacyWithOrphans);
            Test("User-edited discovered rows are never automatically deleted", PreserveEditedOrphan);
            Test("Composite identity permits the same name on different usernames", SameNameDifferentUsers);
            Test("Composite key encoding has no delimiter collisions", KeyCollisions);
            Test("Username-only migration defers when two same-account characters are observed", AmbiguousLiveAccount);
            Test("Known slots disambiguate legacy rows but are never guessed", LegacySlotDisambiguation);
            Test("Removed keys do not suppress another username's character", RemovedPairScope);
            Test("Missing live username cannot assign a named saved character", RequireFullIdentity);
            Test("Legacy migration preserves credentials across save and app restart", MigrationPersistence);
            Test("Missing fields from contradictory identities are not filled", ContradictoryEnrichment);
            Test("A partial character observation cannot mask an ambiguous legacy account", IncompleteLiveAccount);
            Test("Same-PID contradictory snapshots cannot authorize discovery", ConflictingPidSnapshots);
            Test("A transient unavailable username does not fabricate owner replacement", UsernameUnknownOwnership);
            Test("A changed username on the same name and PID cancels old ownership", UsernameChangedOwnership);
            Test("Legacy JSON keeps configured slots and encrypted passwords", LegacyJson);
            Test("Character catalog persists multiple characters per account and IDs", CatalogRoundTrip);
            Test("Stale runtime subset cannot overwrite the authoritative character roster", CatalogAuthority);
            Test("Malformed and future catalogs are preserved without fallback overwrite", BadCatalog);
            Test("A failed catalog validation preserves the existing file", InvalidCatalogSave);
            Test("Unknown or unverified memory identity fields remain unavailable", UnknownMemory);
            Test("Verified memory identity fields populate independently", VerifiedMemory);
            Test("Incoherent, loading, demo and failed snapshots cannot authorize discovery", InvalidMemory);
            Test("Existing clients are assigned by character rather than PID order", AdoptionOrder);
            Test("Missing expected character never adopts another character on the same login", NoWrongCharacter);
            Test("Detect retains the recovery lease across an intentional process exit", DetectDuringClose);
            Test("Detect preserves a failed resume latch", DetectFailed);
            Test("Character changes cancel an owned delayed client action", ChangedCharacter);
            Test("Session replacement cancels ownership even with the same name and PID", ChangedSession);
            Test("Unavailable observations do not fabricate a character replacement", UnavailableOwnership);
            Test("Changing configured character releases the previous PID", EditedCharacter);
            Test("Editing only the description preserves the matched PID", EditedDescription);
            Test("Expected character is validated before an Autobattle hotkey", ExpectedCharacter);
            Test("Automatic enrichment preserves an unrelated recovery worker", PassiveEnrichment);
            Test("An owned successful resume can learn a legacy row identity", LearnedIdentity);
            Console.WriteLine("Character roster: {0} passed; {1} failed. Offline identity, persistence and ownership only.", passed, failed);
            return failed;
        }

        private static VanillaReconnectAccount Row(string name = "Alpha", string user = "login", int? slot = 1)
        { return new VanillaReconnectAccount { Label = "Description " + name, CharacterName = name, UserName = user, CharacterSlot = slot, Enabled = false }; }
        private static VanillaCharacterIdentity Identity(string name = "Alpha", int pid = 101, string user = "login", int? slot = null, DateTimeOffset? at = null, Guid? session = null)
        { return new VanillaCharacterIdentity(pid, session ?? Session, at ?? DateTimeOffset.UtcNow, name, user, slot); }
        private static void DiscoverUnknown()
        {
            var rows = new List<VanillaReconnectAccount>();
            Assert(!VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(user: null, at: Now) }, Now));
            Assert(rows.Count == 0);
        }
        private static void DiscoverKnown()
        {
            var rows = new List<VanillaReconnectAccount>();
            VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(user: "login", slot: 3, at: Now) }, Now);
            Assert(rows.Single().UserName == "login" && rows[0].CharacterSlot == 3 && rows[0].ProxyNeedsConfiguration);
        }
        private static void DiscoverTwice()
        {
            var row = Row(); row.Enabled = true; row.ProtectedPassword = "retained-encrypted-value";
            var rows = new List<VanillaReconnectAccount> { row };
            string before = JsonConvert.SerializeObject(row);
            Assert(!VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(at: Now) }, Now));
            Assert(rows.Count == 1 && before == JsonConvert.SerializeObject(rows[0]));
        }
        private static void SharedLogin()
        {
            var rows = new List<VanillaReconnectAccount>();
            VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(user: "login", slot: 1, at: Now), Identity("Beta", 102, "login", 4, Now) }, Now);
            Assert(rows.Count == 2 && rows.Select(r => r.Id).Distinct().Count() == 2);
            VanillaCharacterRoster.Validate(rows);
        }
        private static void Enrichment()
        {
            var row = Row(user: "", slot: null); row.Label = "Keep description"; row.ProtectedPassword = "secret";
            Assert(VanillaCharacterRoster.FillMissing(row, Identity(user: "login", slot: 5)));
            Assert(row.UserName == "login" && row.CharacterSlot == 5 && row.Label == "Keep description" && row.ProtectedPassword == "secret");
            Assert(!VanillaCharacterRoster.FillMissing(row, Identity("Other", user: "other", slot: 3)));
        }
        private static void UsernameAlone()
        { Assert(!VanillaCharacterRoster.Matches(Row("", slot: null), Identity(user: "login", at: Now), Now)); }
        private static void LegacyMatch()
        {
            var row = Row("", slot: 3); string id = row.Id; row.ProtectedPassword = "old secret";
            var rows = new List<VanillaReconnectAccount> { row };
            VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(user: "login", slot: 3, at: Now) }, Now);
            Assert(rows.Count == 1 && rows[0].CharacterName == "Alpha" && rows[0].Id == id && rows[0].ProtectedPassword == "old secret");
        }
        private static void AmbiguousLegacy()
        {
            var rows = new List<VanillaReconnectAccount> { Row(""), Row("") };
            Assert(!VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(user: "login", slot: 1, at: Now) }, Now));
            Assert(rows.All(r => r.CharacterName == ""));
        }
        private static void ConflictingIdentity()
        {
            Assert(!VanillaCharacterRoster.Matches(Row(), Identity(user: "different", slot: 1, at: Now), Now));
            Assert(!VanillaCharacterRoster.Matches(Row(), Identity(user: "login", slot: 2, at: Now), Now));
            var rows = new List<VanillaReconnectAccount> { Row() };
            Assert(VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(user: "other", at: Now) }, Now) && rows.Count == 2);
            Assert(rows[0].UserName == "login" && rows[1].UserName == "other");
        }
        private static void DuplicateLive()
        {
            var observations = new[] { Identity(at: Now), Identity(pid: 102, at: Now) };
            Assert(VanillaCharacterRoster.FindUnique(Row(), observations, new[] { 101, 102 }, Now) == null);
            var rows = new List<VanillaReconnectAccount>();
            Assert(!VanillaCharacterRoster.MergeObserved(rows, observations, Now));
        }
        private static void InvalidObservations()
        {
            var rows = new List<VanillaReconnectAccount>();
            foreach (var identity in new[] { Identity(at: Now.AddSeconds(-4)), Identity(at: Now.AddSeconds(1)), Identity("", at: Now), Identity("bad\nname", at: Now), Identity(pid: 0, at: Now), Identity(session: Guid.Empty, at: Now) })
                Assert(!VanillaCharacterRoster.MergeObserved(rows, new[] { identity }, Now));
            Assert(rows.Count == 0);
        }
        private static void Removed()
        { var rows = new List<VanillaReconnectAccount>(); Assert(!VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(at: Now) }, Now, new HashSet<string> { VanillaCharacterRoster.Key("login", "Alpha") })); }
        private static void EmptyDefaults()
        {
            var configured = Row("", "saved-login"); configured.Label = "Client 2";
            var rows = new List<VanillaReconnectAccount> { new VanillaReconnectAccount { Label = "Client 1" }, configured };
            VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(at: Now) }, Now);
            Assert(rows.Count == 2 && rows.Any(r => r.Id == configured.Id));
        }
        private static void EnabledLimit()
        { var rows = new[] { Row(), Row("Beta"), Row("Gamma") }; foreach (var r in rows) r.Enabled = true; Expect<InvalidOperationException>(() => VanillaCharacterRoster.Validate(rows)); }
        private static void DuplicateNames()
        { Expect<InvalidOperationException>(() => VanillaCharacterRoster.Validate(new[] { Row(), Row() })); VanillaCharacterRoster.Validate(new[] { Row(), Row("Beta", slot: 2) }); }
        private static void LegacyWithoutSlot()
        {
            var a = Row("", "account-a", 2); a.Label = "Client 1"; a.Enabled = true; a.ProtectedPassword = "secret-a";
            var b = Row("", "account-b", 2); b.Label = "Client 2"; b.Enabled = true; b.ProtectedPassword = "secret-b";
            var rows = new List<VanillaReconnectAccount> { a, b };
            var identities = new[] { Identity("Beta", 102, "account-b", at: Now), Identity("Alpha", 101, "account-a", at: Now) };
            Assert(VanillaCharacterRoster.MergeObserved(rows, identities, Now));
            Assert(rows.Count == 2 && a.CharacterName == "Alpha" && b.CharacterName == "Beta");
            Assert(a.Label == "Client 1" && a.Enabled && a.CharacterSlot == 2 && a.ProtectedPassword == "secret-a" && !a.ProxyNeedsConfiguration);
            Assert(b.Label == "Client 2" && b.Enabled && b.CharacterSlot == 2 && b.ProtectedPassword == "secret-b" && !b.ProxyNeedsConfiguration);
            Assert(!VanillaCharacterRoster.MergeObserved(rows, identities.Reverse(), Now));
        }
        private static VanillaReconnectAccount Orphan()
        { var row = Row(user: "", slot: null); row.Label = row.CharacterName; row.ProxyNeedsConfiguration = true; return row; }
        private static void LegacyWithOrphans()
        {
            var row = Row(""); row.ProtectedPassword = "keep"; row.Enabled = true; string id = row.Id;
            var rows = new List<VanillaReconnectAccount> { Orphan(), row };
            Assert(VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(at: Now) }, Now));
            Assert(rows.Count == 1 && rows[0].Id == id && rows[0].CharacterName == "Alpha" && rows[0].ProtectedPassword == "keep" && rows[0].Enabled);
        }
        private static void PreserveEditedOrphan()
        {
            foreach (Action<VanillaReconnectAccount> change in new Action<VanillaReconnectAccount>[] {
                r => r.ProtectedPassword = "configured", r => r.Label = "My description", r => r.Enabled = true,
                r => r.ProxyNeedsConfiguration = false, r => r.CharacterSlot = 2, r => r.ResumeCtrl = false,
                r => r.CartMaintenanceEnabled = false, r => r.WeightEmailEnabled = false })
            {
                var orphan = Orphan(); change(orphan);
                var rows = new List<VanillaReconnectAccount> { Row(""), orphan };
                string before = JsonConvert.SerializeObject(rows);
                Assert(!VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(at: Now) }, Now));
                Assert(before == JsonConvert.SerializeObject(rows));
            }
        }
        private static void SameNameDifferentUsers()
        {
            var rows = new List<VanillaReconnectAccount>();
            var a = Identity(user: "account-a", at: Now); var b = Identity(pid: 102, user: "account-b", at: Now);
            Assert(VanillaCharacterRoster.MergeObserved(rows, new[] { a, b }, Now));
            Assert(rows.Count == 2); VanillaCharacterRoster.Validate(rows);
            Assert(VanillaCharacterRoster.FindUnique(rows[0], new[] { a, b }, new[] { 101, 102 }, Now).ProcessId == 101);
            Assert(VanillaCharacterRoster.FindUnique(rows[1], new[] { a, b }, new[] { 101, 102 }, Now).ProcessId == 102);
        }
        private static void KeyCollisions()
        {
            Assert(VanillaCharacterRoster.Key("a:b", "c") != VanillaCharacterRoster.Key("a", "b:c"));
            Assert(VanillaCharacterRoster.Key("", "Alpha") == null && VanillaCharacterRoster.Key("login", "") == null);
        }
        private static void AmbiguousLiveAccount()
        {
            var rows = new List<VanillaReconnectAccount> { Row("") };
            Assert(!VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(at: Now), Identity("Beta", 102, at: Now) }, Now));
            Assert(rows.Count == 1 && rows[0].CharacterName == "");
        }
        private static void LegacySlotDisambiguation()
        {
            var rows = new List<VanillaReconnectAccount> { Row("", slot: 1), Row("", slot: 2) };
            Assert(VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(slot: 1, at: Now), Identity("Beta", 102, slot: 2, at: Now) }, Now));
            Assert(rows.Count == 2 && rows[0].CharacterName == "Alpha" && rows[1].CharacterName == "Beta");
        }
        private static void RemovedPairScope()
        {
            var rows = new List<VanillaReconnectAccount>();
            var ignored = new HashSet<string> { VanillaCharacterRoster.Key("account-a", "Alpha") };
            VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(user: "account-a", at: Now), Identity(pid: 102, user: "account-b", at: Now) }, Now, ignored);
            Assert(rows.Count == 1 && rows[0].UserName == "account-b");
        }
        private static void RequireFullIdentity()
        {
            Assert(!VanillaCharacterRoster.Matches(Row(), Identity(user: null, at: Now), Now));
            Assert(!VanillaCharacterRoster.Matches(Row(user: ""), Identity(at: Now), Now));
        }
        private static void MigrationPersistence()
        {
            Temp(root => {
                var store = new VanillaAccountCatalogStore(root); var legacy = Row(""); legacy.ProtectedPassword = "preserved";
                store.Save(new[] { legacy, Orphan() }); var rows = store.Load(new VanillaReconnectAccount[0]);
                VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(at: Now) }, Now); store.Save(rows);
                var reloaded = store.Load(new[] { legacy });
                Assert(reloaded.Count == 1 && reloaded[0].Id == legacy.Id && reloaded[0].CharacterName == "Alpha" && reloaded[0].ProtectedPassword == "preserved");
                Assert(!VanillaCharacterRoster.MergeObserved(reloaded, new[] { Identity(at: Now) }, Now));
            });
        }
        private static void ContradictoryEnrichment()
        {
            var row = Row("", "correct", null);
            Assert(!VanillaCharacterRoster.FillMissing(row, Identity(user: "wrong", slot: 4)));
            Assert(row.CharacterName == "" && row.CharacterSlot == null);
        }
        private static void IncompleteLiveAccount()
        {
            var rows = new List<VanillaReconnectAccount> { Row("") };
            Assert(!VanillaCharacterRoster.MergeObserved(rows, new[] { Identity(at: Now), Identity("Beta", 102, user: null, at: Now) }, Now));
            Assert(rows.Count == 1 && rows[0].CharacterName == "");
        }
        private static void ConflictingPidSnapshots()
        {
            var rows = new List<VanillaReconnectAccount>();
            var observed = new[] { Identity(at: Now), Identity(user: "other", at: Now) };
            Assert(!VanillaCharacterRoster.MergeObserved(rows, observed, Now));
            Assert(VanillaCharacterRoster.FindUnique(Row(), observed, new[] { 101 }, Now) == null);
        }
        private static void UsernameUnknownOwnership()
        {
            using (var h = new Harness()) {
                h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false);
                h.Observed[0] = Identity(user: null);
                Assert(!(bool)Call(h.Supervisor, "CharacterOwnershipChanged", h.Runtime(h.A), 101));
            }
        }
        private static void UsernameChangedOwnership()
        {
            using (var h = new Harness()) {
                h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false);
                h.Observed[0] = Identity(user: "other");
                bool called = false;
                Expect<OperationCanceledException>(() => Call(h.Supervisor, "RunOwnedClientStep", h.Runtime(h.A), 101,
                    (Func<bool>)(() => false), (Func<bool>)(() => { called = true; return true; })));
                Assert(!called);
            }
        }

        private static void UnknownSlot()
        { var r = Row(slot: null).Clone(); Assert(!r.CharacterSlot.HasValue); Expect<InvalidOperationException>(() => r.RequiredCharacterSlot()); }
        private static void LegacyJson()
        {
            var r = JsonConvert.DeserializeObject<VanillaReconnectAccount>("{\"Id\":\"old\",\"Label\":\"old description\",\"UserName\":\"saved\",\"CharacterSlot\":4,\"ProtectedPassword\":\"encrypted\"}");
            Assert(r.Id == "old" && r.CharacterSlot == 4 && r.CharacterName == "" && r.ProtectedPassword == "encrypted");
        }
        private static void CatalogRoundTrip()
        {
            Temp(root => { var store = new VanillaAccountCatalogStore(root); var rows = new[] { Row(), Row("Beta", slot: 4), Row("Gamma", slot: 5) };
                rows[0].ProtectedPassword = "keep"; store.Save(rows); store.Save(rows);
                var actual = store.Load(new VanillaReconnectAccount[0]);
                Assert(actual.Count == 3 && actual[0].Id == rows[0].Id && actual[0].ProtectedPassword == "keep" && actual[1].CharacterSlot == 4);
            });
        }
        private static void CatalogAuthority()
        {
            Temp(root => { var store = new VanillaAccountCatalogStore(root); var row = Row(); row.ProtectedPassword = "keep"; store.Save(new[] { row });
                var stale = row.Clone(); stale.CharacterName = ""; stale.ProtectedPassword = "";
                var actual = store.Load(new[] { stale }); Assert(actual.Count == 1 && actual[0].CharacterName == "Alpha" && actual[0].ProtectedPassword == "keep"); });
        }
        private static void BadCatalog()
        {
            foreach (var text in new[] { "{broken", "{\"Version\":2,\"Accounts\":[]}" })
                Temp(root => { var store = new VanillaAccountCatalogStore(root); File.WriteAllText(store.FilePath, text);
                    Expect<Exception>(() => store.Load(new[] { Row() })); Expect<Exception>(() => store.Save(new[] { Row() }));
                    Assert(File.ReadAllText(store.FilePath) == text); });
        }
        private static void InvalidCatalogSave()
        {
            Temp(root => { var store = new VanillaAccountCatalogStore(root); store.Save(new[] { Row() }); string old = File.ReadAllText(store.FilePath);
                Expect<InvalidOperationException>(() => store.Save(new[] { Row(), Row() })); Assert(File.ReadAllText(store.FilePath) == old); });
        }
        private static StateValue<T> Value<T>(T value, StateValidation validation = StateValidation.Valid, DateTimeOffset? at = null)
        { return new StateValue<T>(value) { IsAvailable = true, Validation = validation, LastObservedAtUtc = at ?? Now }; }
        private static VanillaClientState State(StateValidation validation = StateValidation.Valid)
        {
            return new VanillaClientState { ProcessId = 101, SessionId = Session, SampledAtUtc = Now,
                Fields = new Dictionary<VanillaField, StateValue> {
                    { VanillaField.CharacterName, Value("Alpha", validation) }, { VanillaField.UserName, Value("login", validation) },
                    { VanillaField.CharacterSlot, Value(3, validation) } } };
        }
        private static void UnknownMemory()
        {
            var state = State(StateValidation.Unverified); var identity = VanillaCharacterIdentity.FromState(state);
            Assert(!identity.IsFresh(Now) && identity.UserName == null && identity.CharacterSlot == null);
            var name = State(); name.Fields[VanillaField.UserName].IsAvailable = false; name.Fields[VanillaField.CharacterSlot].Validation = StateValidation.Invalid;
            identity = VanillaCharacterIdentity.FromState(name); Assert(identity.CharacterName == "Alpha" && identity.UserName == null && identity.CharacterSlot == null);
        }
        private static void VerifiedMemory()
        { var identity = VanillaCharacterIdentity.FromState(State()); Assert(identity.IsFresh(Now) && identity.UserName == "login" && identity.CharacterSlot == 3); }
        private static void InvalidMemory()
        {
            var old = State(); old.Fields[VanillaField.CharacterName].LastObservedAtUtc = Now.AddSeconds(-1);
            Assert(!VanillaCharacterIdentity.FromState(old).IsFresh(Now));
            var loading = State(); ((Dictionary<VanillaField, StateValue>)loading.Fields)[VanillaField.Loading] = Value(true);
            Assert(VanillaCharacterIdentity.FromState(loading) == null);
            var demo = State(); demo.IsDemo = true; Assert(VanillaCharacterIdentity.FromState(demo) == null);
            var failed = State(); failed.Error = "read failed"; Assert(VanillaCharacterIdentity.FromState(failed) == null);
        }
        private sealed class Harness : IDisposable
        {
            internal readonly VanillaReconnectSupervisor Supervisor;
            internal readonly List<VanillaCharacterIdentity> Observed = new List<VanillaCharacterIdentity>();
            internal readonly VanillaReconnectAccount A, B;
            private readonly string root = Path.Combine(Path.GetTempPath(), "4R-roster-" + Guid.NewGuid().ToString("N"));
            internal Harness()
            {
                Supervisor = new VanillaReconnectSupervisor(root);
                A = Row(); B = Row("Beta", slot: 4); A.Enabled = B.Enabled = true;
                var config = Supervisor.Settings; config.Accounts = new List<VanillaReconnectAccount> { A, B }; Supervisor.Apply(config, false);
                Supervisor.SetCharacterSource(() => Observed.ToArray());
            }
            internal object Runtime(VanillaReconnectAccount row) { return ((IDictionary)Get(Supervisor, "runtimes"))[row.Id]; }
            public void Dispose() { Supervisor.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static void AdoptionOrder()
        { using (var h = new Harness()) { h.Observed.Add(Identity("Beta", 101)); h.Observed.Add(Identity("Alpha", 102)); Assert(h.Supervisor.AdoptCharacterClients(new[] { 101, 102 }, false) == 2); Assert((int?)Get(h.Runtime(h.A), "ProcessId") == 102 && (int?)Get(h.Runtime(h.B), "ProcessId") == 101); } }
        private static void NoWrongCharacter()
        { using (var h = new Harness()) { h.Observed.Add(Identity("Gamma", 101, "login", 2)); Assert(h.Supervisor.AdoptCharacterClients(new[] { 101 }, false) == 0 && Get(h.Runtime(h.A), "ProcessId") == null); } }
        private static void DetectDuringClose()
        { using (var h = new Harness()) { h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); var r = h.Runtime(h.A); Set(r, "ScriptRunning", true); Set(r, "RecoveryOwned", true); h.Observed.Clear(); h.Supervisor.AdoptCharacterClients(new int[0], false); Assert((int?)Get(r, "ProcessId") == 101 && (bool)Get(r, "ScriptRunning") && (bool)Get(r, "RecoveryOwned")); } }
        private static void DetectFailed()
        { using (var h = new Harness()) { h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); var r = h.Runtime(h.A); Set(r, "ResumeVerificationFailed", true); Set(r, "ResumeSent", false); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); Assert((bool)Get(r, "ResumeVerificationFailed") && !(bool)Get(r, "ResumeSent")); } }
        private static void ChangedCharacter()
        { using (var h = new Harness()) { h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); h.Observed[0] = Identity("Gamma"); bool called = false; Expect<OperationCanceledException>(() => Call(h.Supervisor, "RunOwnedClientStep", h.Runtime(h.A), 101, (Func<bool>)(() => false), (Func<bool>)(() => { called = true; return true; }))); Assert(!called); } }
        private static void ChangedSession()
        { using (var h = new Harness()) { h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); h.Observed[0] = Identity(session: Guid.NewGuid()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); Assert((Guid?)Get(h.Runtime(h.A), "CharacterSession") == h.Observed[0].Session && !(bool)Get(h.Runtime(h.A), "ScriptRunning")); } }
        private static void UnavailableOwnership()
        { using (var h = new Harness()) { h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); h.Observed.Clear(); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); Assert((int?)Get(h.Runtime(h.A), "ProcessId") == 101); } }
        private static void EditedCharacter()
        { using (var h = new Harness()) { h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); var s = h.Supervisor.Settings; s.Accounts[0].CharacterName = "Gamma"; h.Supervisor.Apply(s, false); Assert(Get(h.Runtime(h.A), "ProcessId") == null); } }
        private static void EditedDescription()
        { using (var h = new Harness()) { h.Observed.Add(Identity()); h.Supervisor.AdoptCharacterClients(new[] { 101 }, false); var s = h.Supervisor.Settings; s.Accounts[0].Label = "Changed description"; h.Supervisor.Apply(s, false); Assert((int?)Get(h.Runtime(h.A), "ProcessId") == 101); } }
        private static void ExpectedCharacter()
        {
            var state = State(); state.SampledAtUtc = DateTimeOffset.UtcNow; foreach (var v in state.Fields.Values) v.LastObservedAtUtc = state.SampledAtUtc;
            VanillaReconnectSupervisor.ValidateExpectedCharacter(Row(slot: 3), state);
            Expect<InvalidOperationException>(() => VanillaReconnectSupervisor.ValidateExpectedCharacter(Row("Beta", slot: 3), state));
            Expect<InvalidOperationException>(() => VanillaReconnectSupervisor.ValidateExpectedCharacter(Row("", "other"), state));
            VanillaReconnectSupervisor.ValidateExpectedCharacter(Row("", "login"), state);
            state.Fields[VanillaField.UserName].Validation = StateValidation.Invalid;
            Expect<InvalidOperationException>(() => VanillaReconnectSupervisor.ValidateExpectedCharacter(Row("", "login"), state));
        }
        private static void PassiveEnrichment()
        {
            using (var h = new Harness()) {
                var cfg = h.Supervisor.Settings; cfg.Accounts[0].UserName = ""; cfg.Accounts[0].CharacterSlot = null; h.Supervisor.Apply(cfg, false);
                h.Observed.Add(Identity(user: "login", slot: 3)); h.Observed.Add(Identity("Beta", 102)); h.Supervisor.AdoptCharacterClients(new[] { 101, 102 }, false);
                var busy = h.Runtime(h.B); Set(busy, "ScriptRunning", true); Set(busy, "RecoveryOwned", true); int generation = (int)Get(h.Supervisor, "resumeVerificationGeneration");
                var enriched = h.Supervisor.Settings.Accounts[0].Clone(); enriched.UserName = "login"; enriched.CharacterSlot = 3;
                h.Supervisor.EnrichCharacterMetadata(new[] { enriched, h.B });
                Assert(h.Supervisor.Settings.Accounts[0].UserName == "login" && h.Supervisor.Settings.Accounts[0].CharacterSlot == 3);
                Assert((bool)Get(busy, "ScriptRunning") && (bool)Get(busy, "RecoveryOwned") && (int)Get(h.Supervisor, "resumeVerificationGeneration") == generation);
            }
        }
        private static void LearnedIdentity()
        {
            using (var h = new Harness()) {
                var cfg = h.Supervisor.Settings; cfg.Accounts[0].CharacterName = ""; h.Supervisor.Apply(cfg, false);
                var r = h.Runtime(h.A); Set(r, "ProcessId", (int?)101); Set(r, "ResumeSent", true); Set(r, "ConfirmedCharacter", Identity());
                Assert(h.Supervisor.ConfirmedCharacters()[h.A.Id].CharacterName == "Alpha");
                Set(r, "ResumeVerificationFailed", true); Assert(!h.Supervisor.ConfirmedCharacters().ContainsKey(h.A.Id));
            }
        }
        private static object Get(object owner, string name) { return owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Flags).SetValue(owner, value); }
        private static object Call(object owner, string method, params object[] args)
        { try { return owner.GetType().GetMethod(method, Flags).Invoke(owner, args); } catch (TargetInvocationException ex) { throw ex.InnerException; } }
        private static void Temp(Action<string> action)
        { string root = Path.Combine(Path.GetTempPath(), "4R-roster-file-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { action(root); } finally { Directory.Delete(root, true); } }
        private static void Expect<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        private static void Assert(bool condition) { if (!condition) throw new Exception("Character roster assertion failed."); }
        private static void Test(string name, Action action)
        { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
    }
}
