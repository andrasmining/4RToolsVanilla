using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json.Linq;
using _4RTools.Model.Vanilla;
using _4RTools.Utils;

namespace Vanilla.Diagnostics.Tests
{
    internal static class Program
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private static int passed;
        private static int failed;

        [STAThread]
        private static int Main(string[] args)
        {
            VanillaIsolatedTestDesktop.AssertCurrent();
            if (args.Length == 1 && args[0] == "--proxy-pattern-tests") return VanillaProxyPatternTests.Run();
            if (args.Length == 1 && args[0] == "--private-updater-tests") return VanillaUpdaterTests.Run();
            if (args.Length == 1 && args[0].StartsWith("--recovery-native-probe", StringComparison.Ordinal))
                return VanillaNativeRecoveryTests.Child(args[0].EndsWith("-ignore-close", StringComparison.Ordinal));
            if (args.Length == 1 && args[0] == "--native-recovery-tests") return VanillaNativeRecoveryTests.Run();
            // These tests only use fake process memory. Never open or enumerate a live process.
            Run("Empty map leaves unsupported values unknown", EmptyMap);
            Run("Readable zero differs from an unsupported value", ReadableZero);
            Run("Observation timestamps distinguish first, unchanged and changed values", Timestamps);
            Run("New source starts fresh observation history", NewSourceHistory);
            Run("Target observations distinguish no target from unavailable", TargetTransitions);
            Run("Status arrays compare by content and cannot mutate snapshots", StatusArrays);
            Run("Module-relative fields follow the actual module base", ModuleRelative);
            Run("Pointer chains follow the target pointer width", PointerChains);
            Run("High 32-bit addresses retain their unsigned value", High32BitAddress);
            Run("64-bit addresses are not truncated", High64BitAddress);
            Run("Address ranges respect both pointer widths and byte counts", AddressRanges);
            Run("Pointer overflow fails without reading a wrapped address", PointerOverflow);
            Run("A null pointer stops discovery", NullPointer);
            Run("Read failure stops the source and clears stale values", ReadFailure);
            Run("Partial reads cannot become observed values", PartialRead);
            Run("Process exit stops subsequent polling", ProcessExit);
            Run("A field map cannot silently attach to another executable", ProcessMismatch);
            Run("Disposal closes the fake transport", Disposal);
            Run("Map serialization round trips addresses and offsets", MapRoundTrip);
            Run("Snapshot export preserves unavailable and zero-valued fields", SnapshotSerialization);
            Run("Malformed memory maps fail explicitly", InvalidMaps);
            Run("Profile settings accept legacy objects, strings and absent sections", ProfileSettings);
            Run("Polling configuration enforces bounded intervals", PollingBounds);
            Run("Demo source works without process memory", Demo);
            Run("Executable fingerprint is stable and rejects malformed images", DiscoveryTests.Run);
            Run("Vanilla attachment requires its shared read-only session", () =>
            {
                try { new _4RTools.Model.Client("Vanilla MMO.exe - 1"); }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.Contains("shared read-only connection")) return;
                    throw;
                }
                throw new Exception("Vanilla reached the legacy attachment path.");
            });
            failed += AutomationTests.Run();
            failed += BuildProfileTests.Run();
            failed += StockBridgeTests.Run();
            failed += ProfileStoreTests.Run();
            failed += LegacyProfileTests.Run();
            failed += VanillaPatcherLauncherTests.Run();
            failed += VanillaLauncherUpdateTests.Run();
            failed += VanillaProxyPatternTests.Run();
            failed += VanillaServiceSelectionTests.Run();
            failed += VanillaCredentialVerifierTests.Run();
            failed += VanillaCharacterSelectionTests.Run();
            failed += VanillaAuthPatternTests.Run();
            failed += VanillaSessionLogTests.Run();
            failed += VanillaReconnectRegressionTests.Run();
            failed += VanillaAutobattleResumeTests.Run();
            failed += VanillaRecoveryWatchdogTests.Run();
            failed += VanillaCharacterRosterTests.Run();
            failed += VanillaTemporaryActionTests.Run();
            failed += VanillaMemoryScannerTests.Run();
            failed += ProcessObservationContextTests.Run();
            failed += VanillaFleetMonitorTests.Run();
            failed += VanillaUpdaterTests.Run();

            Console.WriteLine("Core diagnostics: {0} passed. Total failures across all suites: {1}. All tests used offline data.", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, System.Action test)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine("FAIL " + name + ": " + ex);
            }
        }

        private static void EmptyMap()
        {
            var memory = new FakeMemory();
            using (var source = new MemoryStateSource(memory, new VanillaMemoryMap()))
            {
                VanillaClientState snapshot = source.Poll(Epoch);
                Assert(!snapshot.CurrentHP.IsAvailable, "HP must be unknown.");
                Assert(!snapshot.CurrentSP.IsAvailable, "SP must be unknown.");
                Assert(!snapshot.CurrentTargetId.IsAvailable, "No address must not mean no target.");
                Assert(!snapshot.AutobattleEnabled.IsAvailable, "No address must not mean Autobattle disabled.");
                Equal(0, memory.ReadCount, "Metadata-only polling must not read memory.");
                Assert(!source.IsStopped, "An empty map is a valid diagnostics mode.");
            }
        }

        private static void ReadableZero()
        {
            var memory = new FakeMemory();
            memory.Put(0x100, 0u);
            using (var source = new MemoryStateSource(memory, Map(VanillaField.CurrentHP, "0x100")))
            {
                var snapshot = source.Poll(Epoch);
                Assert(snapshot.CurrentHP.IsAvailable, "A successful read of zero is available.");
                Equal(0u, (uint)snapshot.CurrentHP.UntypedValue, "Read zero exactly.");
                Assert(!snapshot.CurrentSP.IsAvailable, "An unrelated unmapped field remains unknown.");
            }
        }

        private static void Timestamps()
        {
            var memory = new FakeMemory();
            memory.Put(0x100, 20u);
            using (var source = new MemoryStateSource(memory, Map(VanillaField.CurrentHP, "0x100")))
            {
                var first = source.Poll(Epoch).CurrentHP;
                Equal(Epoch, first.LastObservedAtUtc.Value, "First observation time.");
                Assert(!first.LastChangedAtUtc.HasValue, "The first sample does not prove a transition.");
                var same = source.Poll(Epoch.AddSeconds(1)).CurrentHP;
                Equal(Epoch.AddSeconds(1), same.LastObservedAtUtc.Value, "Unchanged value was observed again.");
                Assert(!same.LastChangedAtUtc.HasValue, "Unchanged value does not invent a transition.");
                memory.Put(0x100, 21u);
                var changed = source.Poll(Epoch.AddSeconds(2)).CurrentHP;
                Equal(Epoch.AddSeconds(2), changed.LastChangedAtUtc.Value, "A real change records its time.");
                var repeated = source.Poll(Epoch.AddSeconds(3)).CurrentHP;
                Equal(Epoch.AddSeconds(2), repeated.LastChangedAtUtc.Value, "An unchanged value retains the change time.");
                Equal(20u, (uint)first.UntypedValue, "A new poll must not mutate an earlier snapshot.");
            }
        }

        private static void NewSourceHistory()
        {
            var firstMemory = new FakeMemory();
            firstMemory.Put(0x100, 1u);
            using (var first = new MemoryStateSource(firstMemory, Map(VanillaField.CurrentHP, "0x100")))
            {
                first.Poll(Epoch);
                firstMemory.Put(0x100, 2u);
                Assert(first.Poll(Epoch.AddSeconds(1)).CurrentHP.LastChangedAtUtc.HasValue, "First session changed.");
            }
            var nextMemory = new FakeMemory();
            nextMemory.Put(0x100, 2u);
            using (var next = new MemoryStateSource(nextMemory, Map(VanillaField.CurrentHP, "0x100")))
            {
                Assert(!next.Poll(Epoch.AddSeconds(2)).CurrentHP.LastChangedAtUtc.HasValue,
                    "Even reuse of a PID must not carry idle history into a new source.");
            }
        }

        private static void ModuleRelative()
        {
            foreach (ulong moduleBase in new ulong[] { 0x1000, 0x10000000 })
            {
                var memory = new FakeMemory { MainModuleBaseAddress = moduleBase };
                memory.Put(moduleBase + 0x20, 45u);
                var map = Map(VanillaField.CurrentHP, "0x20");
                map.Fields[VanillaField.CurrentHP].Module = "VanillaTestClient.exe";
                using (var source = new MemoryStateSource(memory, map))
                {
                    var value = source.Poll(Epoch).CurrentHP;
                    Assert(value.IsAvailable, "Module-relative value must resolve.");
                    Equal(moduleBase + 0x20, value.Address.Value, "Use the actual loaded base.");
                    Equal(45u, (uint)value.UntypedValue, "Resolved value.");
                }
            }
        }

        private static void TargetTransitions()
        {
            var memory = new FakeMemory();
            memory.Put(0x100, 0u);
            using (var source = new MemoryStateSource(memory, Map(VanillaField.CurrentTargetId, "0x100")))
            {
                var none = source.Poll(Epoch).CurrentTargetId;
                Assert(none.IsAvailable, "A successfully read zero target ID is available.");
                Equal(0UL, none.Value, "Zero is represented without assuming its in-game meaning.");
                Assert(!none.LastChangedAtUtc.HasValue, "First observation does not prove idle duration.");
                memory.Put(0x100, 42u);
                var selected = source.Poll(Epoch.AddSeconds(1)).CurrentTargetId;
                Equal(42UL, selected.Value, "Target selection is observed.");
                Equal(Epoch.AddSeconds(1), selected.LastChangedAtUtc.Value, "Selection transition timestamp.");
                memory.Put(0x100, 0u);
                var deselected = source.Poll(Epoch.AddSeconds(2)).CurrentTargetId;
                Equal(0UL, deselected.Value, "Target removal is observed.");
                Equal(Epoch.AddSeconds(2), deselected.LastChangedAtUtc.Value, "Removal transition timestamp.");
                Assert(!source.Poll(Epoch.AddSeconds(3)).ActionState.IsAvailable,
                    "A target address must not invent an action or combat state.");
            }
        }

        private static void StatusArrays()
        {
            var memory = new FakeMemory();
            memory.PutBytes(0x100, BitConverter.GetBytes(1u).Concat(BitConverter.GetBytes(999999u)).ToArray());
            var map = Map(VanillaField.StatusEffects, "0x100");
            map.Fields[VanillaField.StatusEffects].Encoding = VanillaValueEncoding.UInt32Array;
            map.Fields[VanillaField.StatusEffects].ByteCount = 8;
            using (var source = new MemoryStateSource(memory, map))
            {
                var first = source.Poll(Epoch).StatusEffects;
                Equal(999999u, first.Value[1], "Retain raw unknown status IDs.");
                first.Value[0] = 77u;
                Equal(1u, first.Value[0], "Callers cannot modify a snapshot's status array.");
                var same = source.Poll(Epoch.AddSeconds(1)).StatusEffects;
                Assert(!same.LastChangedAtUtc.HasValue, "Identical new arrays do not imply a state change.");
                memory.Put(0x104, 8u);
                var changed = source.Poll(Epoch.AddSeconds(2)).StatusEffects;
                Equal(Epoch.AddSeconds(2), changed.LastChangedAtUtc.Value, "Changed array content has a transition time.");
                Equal(999999u, first.Value[1], "Later polls cannot mutate older arrays.");
            }
        }

        private static void PointerChains()
        {
            foreach (int width in new[] { 4, 8 })
            {
                var memory = new FakeMemory { PointerSize = width };
                memory.PutPointer(0x100, 0x200);
                memory.PutPointer(0x204, 0x300);
                memory.Put(0x308, 17u);
                var map = Map(VanillaField.CurrentHP, "0x100");
                map.Fields[VanillaField.CurrentHP].PointerOffsets.Add("4");
                map.Fields[VanillaField.CurrentHP].PointerOffsets.Add("0x8");
                using (var source = new MemoryStateSource(memory, map))
                {
                    var value = source.Poll(Epoch).CurrentHP;
                    Assert(value.IsAvailable, "The pointer chain resolves.");
                    Equal(17u, (uint)value.UntypedValue, "Value at final pointer.");
                    Equal(0x308UL, value.Address.Value, "Final observed address.");
                    Equal(width, memory.Requests[0].Item2, "Read target pointer width.");
                    Equal(width, memory.Requests[1].Item2, "Read target pointer width at every level.");
                }
            }
        }

        private static void High32BitAddress()
        {
            var memory = new FakeMemory { PointerSize = 4 };
            memory.PutPointer(0x100, 0xF0000000);
            memory.Put(0xF0000004, 7u);
            var map = Map(VanillaField.CurrentHP, "0x100");
            map.Fields[VanillaField.CurrentHP].PointerOffsets.Add("4");
            using (var source = new MemoryStateSource(memory, map))
            {
                var value = source.Poll(Epoch).CurrentHP;
                Assert(value.IsAvailable, "The high 32-bit address remains readable.");
                Equal(0xF0000004UL, value.Address.Value, "Do not sign-extend a 32-bit pointer.");
            }
        }

        private static void High64BitAddress()
        {
            var memory = new FakeMemory { PointerSize = 8 };
            memory.Put(0x100000100, 91u);
            using (var source = new MemoryStateSource(memory, Map(VanillaField.CurrentHP, "0x100000100")))
            {
                var value = source.Poll(Epoch).CurrentHP;
                Assert(value.IsAvailable, "An address above 4 GB is supported for a 64-bit target.");
                Equal(0x100000100UL, value.Address.Value, "Preserve all 64 address bits.");
            }
        }

        private static void PointerOverflow()
        {
            foreach (int width in new[] { 4, 8 })
            {
                var memory = new FakeMemory { PointerSize = width };
                memory.PutPointer(0x100, width == 4 ? uint.MaxValue : ulong.MaxValue);
                var map = Map(VanillaField.CurrentHP, "0x100");
                map.Fields[VanillaField.CurrentHP].PointerOffsets.Add("1");
                using (var source = new MemoryStateSource(memory, map))
                {
                    Assert(!source.Poll(Epoch).CurrentHP.IsAvailable, "Overflow cannot yield a value.");
                    Assert(source.IsStopped, "Overflow stops the source.");
                    Equal(1, memory.ReadCount, "Never attempt a read at the wrapped result.");
                }
            }
        }

        private static void AddressRanges()
        {
            ReadOnlyProcessMemory.ValidateRange(0xF0000000, 4, 4);
            ReadOnlyProcessMemory.ValidateRange(uint.MaxValue, 1, 4);
            ReadOnlyProcessMemory.ValidateRange(0x100000100, 8, 8);
            ReadOnlyProcessMemory.ValidateRange(ulong.MaxValue, 1, 8);
            Throws<ArgumentException>(() => ReadOnlyProcessMemory.ValidateRange(0, 4, 4), "Reject null addresses.");
            Throws<ArgumentException>(() => ReadOnlyProcessMemory.ValidateRange(1, 0, 4), "Reject empty reads.");
            Throws<ArgumentException>(() => ReadOnlyProcessMemory.ValidateRange(1, 4, 2), "Reject unknown pointer widths.");
            Throws<ArgumentException>(() => ReadOnlyProcessMemory.ValidateRange(uint.MaxValue, 4, 4), "Reject 32-bit range overflow.");
            Throws<ArgumentException>(() => ReadOnlyProcessMemory.ValidateRange(ulong.MaxValue, 4, 8), "Reject 64-bit range overflow.");
            Throws<ArgumentException>(() => ReadOnlyProcessMemory.ValidateRange(0x100000000, 4, 4), "Reject 64-bit addresses for a 32-bit target.");
        }

        private static void NullPointer()
        {
            var memory = new FakeMemory();
            memory.PutPointer(0x100, 0);
            var map = Map(VanillaField.CurrentHP, "0x100");
            map.Fields[VanillaField.CurrentHP].PointerOffsets.Add("4");
            using (var source = new MemoryStateSource(memory, map))
            {
                Assert(!source.Poll(Epoch).CurrentHP.IsAvailable, "Null pointer is not a valid observation.");
                Assert(source.IsStopped, "Null pointer resolution is stopped.");
                Equal(1, memory.ReadCount, "No read may follow a null pointer.");
            }
        }

        private static void ReadFailure()
        {
            var memory = new FakeMemory();
            memory.Put(0x100, 25u);
            using (var source = new MemoryStateSource(memory, Map(VanillaField.CurrentHP, "0x100")))
            {
                Assert(source.Poll(Epoch).CurrentHP.IsAvailable, "Initial sample succeeds.");
                memory.Failure = new Win32Exception(5, "Test: ReadProcessMemory denied at 0x100 (Win32 5).");
                var failure = source.Poll(Epoch.AddSeconds(1));
                Assert(!failure.CurrentHP.IsAvailable, "A previous good value must not appear current after failure.");
                Assert(source.IsStopped, "Failure must stop this discovery session.");
                Assert(memory.Disposed, "Failure must release the memory transport.");
                Assert(!string.IsNullOrWhiteSpace(source.Status), "The stopped source exposes an error.");
                int readCount = memory.ReadCount;
                source.Poll(Epoch.AddSeconds(2));
                Equal(readCount, memory.ReadCount, "No automatic read retry after a failure.");
            }
        }

        private static void PartialRead()
        {
            var memory = new FakeMemory { PartialResult = true };
            memory.Put(0x100, 25u);
            using (var source = new MemoryStateSource(memory, Map(VanillaField.CurrentHP, "0x100")))
            {
                Assert(!source.Poll(Epoch).CurrentHP.IsAvailable, "A truncated uint is unavailable.");
                Assert(source.IsStopped, "A partial read stops this discovery session.");
                int reads = memory.ReadCount;
                source.Poll(Epoch.AddSeconds(1));
                Equal(reads, memory.ReadCount, "No automatic retry after a partial read.");
            }
        }

        private static void ProcessExit()
        {
            var memory = new FakeMemory();
            memory.Put(0x100, 25u);
            using (var source = new MemoryStateSource(memory, Map(VanillaField.CurrentHP, "0x100")))
            {
                source.Poll(Epoch);
                memory.Exited = true;
                var snapshot = source.Poll(Epoch.AddSeconds(1));
                Assert(!snapshot.CurrentHP.IsAvailable, "An exited process has no current HP sample.");
                Assert(source.IsStopped, "An exited process stops polling.");
                Equal(1, memory.ReadCount, "No read after process exit.");
            }
        }

        private static void ProcessMismatch()
        {
            var memory = new FakeMemory();
            var map = Map(VanillaField.CurrentHP, "0x100");
            map.ProcessName = "AnotherClient";
            bool rejected = false;
            try
            {
                using (var source = new MemoryStateSource(memory, map))
                    rejected = !source.Poll(Epoch).CurrentHP.IsAvailable && source.IsStopped;
            }
            catch (ArgumentException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "An executable mismatch must be rejected explicitly.");
            Equal(0, memory.ReadCount, "Do not read another executable's addresses.");
        }

        private static void Disposal()
        {
            var memory = new FakeMemory();
            var source = new MemoryStateSource(memory, new VanillaMemoryMap());
            source.Dispose();
            source.Dispose();
            Assert(memory.Disposed, "Dispose closes the transport and is safe to repeat.");
        }

        private static void MapRoundTrip()
        {
            var map = Map(VanillaField.CurrentHP, "0x100000100");
            map.Fields[VanillaField.CurrentHP].Module = "VanillaTestClient.exe";
            map.Fields[VanillaField.CurrentHP].PointerOffsets.Add("0x20");
            map.Fields[VanillaField.CurrentHP].Evidence = "Offline regression fixture, not a verified game field.";
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(map);
            var restored = VanillaMemoryMap.Parse(json);
            Equal(map.ProcessName, restored.ProcessName, "Process identity survives serialization.");
            Equal("0x100000100", restored.Fields[VanillaField.CurrentHP].Address, "High address survives serialization.");
            Equal("0x20", restored.Fields[VanillaField.CurrentHP].PointerOffsets.Single(), "Pointer offset survives serialization.");
            Equal(map.Fields[VanillaField.CurrentHP].Evidence, restored.Fields[VanillaField.CurrentHP].Evidence,
                "Evidence remains explicit after serialization.");
        }

        private static void InvalidMaps()
        {
            RejectMap("null");
            RejectMap("{broken json}");
            RejectMap("{\"SchemaVersion\":999,\"Fields\":{}}");
            RejectMap("{\"Fields\":null}");
            RejectMap("{\"SchemaVersion\":1,\"Fields\":null}");
            RejectMap("{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Fields\":{}}");
            RejectMap("{\"SchemaVersion\":1,\"Fields\":{},\"UnknownOption\":true}");
            foreach (string address in new[] { "-1", "0xGG", "0x10000000000000000" })
            {
                var map = Map(VanillaField.CurrentHP, address);
                Throws<ArgumentException>(() => map.Validate(), "Reject an invalid address: " + address);
            }
            var tooLong = Map(VanillaField.CurrentHP, "0x100");
            tooLong.Fields[VanillaField.CurrentHP].PointerOffsets = Enumerable.Repeat("4", 9).ToList();
            Throws<ArgumentException>(() => tooLong.Validate(), "Bound pointer chains to at most eight dereferences.");
            var invalidEncoding = Map(VanillaField.CurrentHP, "0x100");
            invalidEncoding.Fields[VanillaField.CurrentHP].Encoding = (VanillaValueEncoding)999;
            Throws<ArgumentException>(() => invalidEncoding.Validate(), "Reject unknown encoding values.");
        }

        private static void SnapshotSerialization()
        {
            var memory = new FakeMemory();
            memory.Put(0x100, 0u);
            using (var source = new MemoryStateSource(memory, Map(VanillaField.CurrentHP, "0x100")))
            {
                var snapshot = source.Poll(Epoch);
                var exported = Newtonsoft.Json.Linq.JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(snapshot));
                Assert(exported["CurrentHP"]["IsAvailable"].Value<bool>(), "Export retains the successful zero read.");
                Equal(0u, exported["CurrentHP"]["UntypedValue"].Value<uint>(), "Export retains the zero value.");
                Assert(!exported["CurrentSP"]["IsAvailable"].Value<bool>(), "Export retains unavailable status.");
                Equal(Newtonsoft.Json.Linq.JTokenType.Null, exported["CurrentSP"]["UntypedValue"].Type,
                    "Unknown SP must export as null, not zero.");
                Equal(Newtonsoft.Json.Linq.JTokenType.Null, exported["CurrentHP"]["LastChangedAtUtc"].Type,
                    "First export must not invent a transition timestamp.");
            }
            var map = Map(VanillaField.CurrentHP, "0x100");
            Equal("0x100", VanillaMemoryMap.Parse(map.ToJson()).Fields[VanillaField.CurrentHP].Address,
                "The normal map export path remains importable.");
        }

        private static void ProfileSettings()
        {
            var missing = VanillaDiagnosticsSettings.FromToken(null);
            var explicitNull = VanillaDiagnosticsSettings.FromToken(Newtonsoft.Json.Linq.JValue.CreateNull());
            Equal(500, missing.PollIntervalMilliseconds, "Older profiles use safe default polling.");
            Equal(0, VanillaMemoryMap.Parse(missing.MemoryMapJson).Fields.Count, "Defaults invent no addresses.");
            Assert(!object.ReferenceEquals(missing, explicitNull), "Each missing section gets fresh settings.");
            var original = new VanillaDiagnosticsSettings
            {
                PollIntervalMilliseconds = 750,
                MemoryMapJson = Map(VanillaField.CurrentHP, "0x100").ToJson()
            };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(original);
            var objectForm = VanillaDiagnosticsSettings.FromToken(Newtonsoft.Json.Linq.JObject.Parse(json));
            var stringForm = VanillaDiagnosticsSettings.FromToken(new Newtonsoft.Json.Linq.JValue(json));
            Equal(750, objectForm.PollIntervalMilliseconds, "Object-style section loads.");
            Equal(750, stringForm.PollIntervalMilliseconds, "String-style section loads.");
            Equal(original.MemoryMapJson, stringForm.MemoryMapJson, "Embedded map JSON survives legacy string format.");
            Throws<ArgumentException>(() => VanillaDiagnosticsSettings.FromToken(
                Newtonsoft.Json.Linq.JObject.Parse("{\"PollIntervalMilliseconds\":0}")),
                "Malformed persisted polling must not silently activate.");
            Throws<ArgumentException>(() => VanillaDiagnosticsSettings.FromToken(new Newtonsoft.Json.Linq.JValue("null")),
                "A null serialized section must not become usable settings.");
        }

        private static void PollingBounds()
        {
            foreach (int accepted in new[] { 250, 500, 10000 })
                new VanillaDiagnosticsSettings { PollIntervalMilliseconds = accepted }.Validate();
            foreach (int rejected in new[] { int.MinValue, 0, 249, 10001, int.MaxValue })
                Throws<ArgumentException>(() => new VanillaDiagnosticsSettings { PollIntervalMilliseconds = rejected }.Validate(),
                    "Polling interval outside bounds must be rejected: " + rejected);
        }

        private static void RejectMap(string json)
        {
            bool rejected = false;
            try { VanillaMemoryMap.Parse(json); }
            catch (ArgumentException) { rejected = true; }
            catch (Newtonsoft.Json.JsonException) { rejected = true; }
            Assert(rejected, "Malformed map must be rejected: " + json);
        }

        private static void Demo()
        {
            using (var source = new DemoStateSource())
            {
                var first = source.Poll(Epoch);
                var next = source.Poll(Epoch.AddSeconds(1));
                Assert(first.CurrentHP.IsAvailable, "Demo has an observable HP field.");
                Assert(next.CurrentHP.IsAvailable, "Demo can be polled again.");
                Assert(!source.IsStopped, "Demo does not need a running game.");
                Assert(first.IsDemo && next.IsDemo, "Snapshots retain explicit demo provenance.");
                Assert(!first.ProcessId.HasValue, "Demo must not claim a live PID.");
                Assert(source.Status.IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Demo provenance must be visible in connection status.");
            }
        }

        private static VanillaMemoryMap Map(VanillaField field, string address)
        {
            var map = new VanillaMemoryMap { ProcessName = "VanillaTestClient" };
            map.Fields.Add(field, new VanillaFieldMapping
            {
                Address = address,
                Encoding = VanillaValueEncoding.UInt32,
                ByteCount = 4,
                PointerOffsets = new List<string>()
            });
            return map;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(message + " Expected: " + expected + "; actual: " + actual + ".");
        }

        private static void Throws<T>(System.Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private sealed class FakeMemory : IReadOnlyProcessMemory
        {
            private readonly Dictionary<ulong, byte> bytes = new Dictionary<ulong, byte>();
            public int ProcessId { get { return 4242; } }
            public string ProcessName { get { return "VanillaTestClient"; } }
            public int PointerSize { get; set; } = 4;
            public ulong MainModuleBaseAddress { get; set; } = 0x400000;
            public bool IsStopped { get { return Disposed; } }
            public string LastError { get { return Failure == null ? null : Failure.Message; } }
            public bool Disposed { get; private set; }
            public bool Exited { get; set; }
            public bool PartialResult { get; set; }
            public Exception Failure { get; set; }
            public List<Tuple<ulong, int>> Requests { get; } = new List<Tuple<ulong, int>>();
            public int ReadCount { get { return Requests.Count; } }

            public void EnsureAlive()
            {
                if (Exited) throw new InvalidOperationException("Test process exited.");
                if (Disposed) throw new ObjectDisposedException("FakeMemory");
            }

            public ulong GetModuleBase(string moduleName)
            {
                if (!string.IsNullOrEmpty(moduleName) && moduleName != "VanillaTestClient.exe")
                    throw new InvalidOperationException("Test module not loaded: " + moduleName);
                return MainModuleBaseAddress;
            }

            public byte[] ReadBytes(ulong address, int count)
            {
                Requests.Add(Tuple.Create(address, count));
                EnsureAlive();
                if (Failure != null) throw Failure;
                var result = new byte[PartialResult ? Math.Max(0, count - 1) : count];
                for (int i = 0; i < result.Length; i++)
                {
                    byte value;
                    if (!bytes.TryGetValue(checked(address + (ulong)i), out value))
                        throw new InvalidOperationException("Test read outside supplied memory at 0x" + address.ToString("X"));
                    result[i] = value;
                }
                return result;
            }

            public void Put(ulong address, uint value)
            {
                PutBytes(address, BitConverter.GetBytes(value));
            }

            public void PutPointer(ulong address, ulong value)
            {
                PutBytes(address, PointerSize == 4 ? BitConverter.GetBytes(checked((uint)value)) : BitConverter.GetBytes(value));
            }

            public void PutBytes(ulong address, byte[] values)
            {
                for (int i = 0; i < values.Length; i++) bytes[checked(address + (ulong)i)] = values[i];
            }

            public void Dispose() { Disposed = true; }
        }
    }
}
