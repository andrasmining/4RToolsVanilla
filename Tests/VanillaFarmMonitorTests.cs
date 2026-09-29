using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    public static class VanillaFarmMonitorTests
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        public static int Run()
        {
            int failed = 0;
            var tests = new Dictionary<string, System.Action>
            {
                { "Farm monitor reset preserves definitions and starts a clean run", ResetPreservesDefinitions },
                { "Farm monitor pause and resume count active elapsed time only", PauseResumeTime },
                { "Verified Cart weight deltas become exact configured item quantities", AutomaticCartCounting },
                { "Paused farm monitor ignores automatic Cart transfers", PausedIgnoresTransfers },
                { "Specific Cart source mapping wins before Any fallback", SpecificBeforeAny },
                { "Unmappable Cart deltas remain explicitly unassigned", UnassignedWeight },
                { "Farm monitor rejects ambiguous duplicate automatic mappings", DuplicateAutomaticMapping },
                { "Manual item edits drive total zeny and zeny per hour", ManualValueCalculation },
                { "Farm monitor state survives store round trip", StoreRoundTrip }
            };
            foreach (var test in tests)
            {
                try { test.Value(); Console.WriteLine("PASS " + test.Key); }
                catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Key + ": " + ex); }
            }
            Console.WriteLine("Farming monitor: {0} passed; {1} failed. Tests use only temporary files and synthetic Cart deltas.",
                tests.Count - failed, failed);
            return failed;
        }

        private static void ResetPreservesDefinitions()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "Peco", new[]
                {
                    Item("mastela", "Mastela", 585.56m, 17, VanillaFarmAutoSource.Use, 3),
                    Item("feather", "Feather", 93.83m, 91, VanillaFarmAutoSource.Etc, 1)
                });
                service.ResetAt("a", "Peco", Epoch);
                VanillaFarmMonitorSnapshot snapshot = service.SnapshotAt("a", "Peco", Epoch.AddSeconds(10));
                Assert(snapshot.Running, "Reset should start the new run immediately.");
                Equal(TimeSpan.FromSeconds(10), snapshot.Elapsed, "Reset should start elapsed time at zero.");
                Equal(2, snapshot.Items.Count, "Reset should preserve configured item rows.");
                Assert(snapshot.Items.All(item => item.Count == 0), "Reset should clear all item counts.");
                Equal(0L, snapshot.UnassignedWeight, "Reset should clear unassigned Cart weight.");
            });
        }

        private static void PauseResumeTime()
        {
            WithService(service =>
            {
                service.ResetAt("a", "Runner", Epoch);
                service.SetRunningAt("a", "Runner", false, Epoch.AddMinutes(10));
                Equal(TimeSpan.FromMinutes(10), service.SnapshotAt("a", "Runner", Epoch.AddHours(1)).Elapsed,
                    "Paused wall-clock time must not increase elapsed farming time.");
                service.SetRunningAt("a", "Runner", true, Epoch.AddHours(1));
                service.SetRunningAt("a", "Runner", false, Epoch.AddHours(1).AddMinutes(5));
                Equal(TimeSpan.FromMinutes(15), service.SnapshotAt("a", "Runner", Epoch.AddHours(2)).Elapsed,
                    "Resume should add only the second active interval.");
            });
        }

        private static void AutomaticCartCounting()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "Peco", new[]
                {
                    Item("mastela", "Mastela", 585.56m, 0, VanillaFarmAutoSource.Use, 3),
                    Item("feather", "Feather", 93.83m, 0, VanillaFarmAutoSource.Etc, 1)
                });
                service.ResetAt("a", "Peco", Epoch);
                service.RecordCartTransferAt("a", "Peco", VanillaFarmAutoSource.Use, 300, Epoch.AddSeconds(10));
                service.RecordCartTransferAt("a", "Peco", VanillaFarmAutoSource.Etc, 250, Epoch.AddSeconds(20));
                VanillaFarmMonitorSnapshot snapshot = service.SnapshotAt("a", "Peco", Epoch.AddSeconds(30));
                Equal(100L, snapshot.Items.Single(item => item.Id == "mastela").Count,
                    "300 verified Use Cart weight at 3 weight/item should add 100.");
                Equal(250L, snapshot.Items.Single(item => item.Id == "feather").Count,
                    "250 verified Etc Cart weight at 1 weight/item should add 250.");
                Equal(TimeSpan.FromSeconds(30), snapshot.Elapsed, "Transfer checkpoints must not distort the running timer.");
            });
        }

        private static void PausedIgnoresTransfers()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "Runner", new[] { Item("etc", "Loot", 10, 0, VanillaFarmAutoSource.Etc, 1) });
                service.ResetAt("a", "Runner", Epoch);
                service.SetRunningAt("a", "Runner", false, Epoch.AddSeconds(5));
                service.RecordCartTransferAt("a", "Runner", VanillaFarmAutoSource.Etc, 123, Epoch.AddSeconds(10));
                VanillaFarmMonitorSnapshot snapshot = service.SnapshotAt("a", "Runner", Epoch.AddSeconds(20));
                Equal(0L, snapshot.Items[0].Count, "Paused monitor must not count Cart transfers.");
                Equal(TimeSpan.FromSeconds(5), snapshot.Elapsed, "Paused transfer must not alter elapsed time.");
            });
        }

        private static void SpecificBeforeAny()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "Runner", new[]
                {
                    Item("use", "Use item", 1, 0, VanillaFarmAutoSource.Use, 2),
                    Item("any", "Fallback", 1, 0, VanillaFarmAutoSource.Any, 1)
                });
                service.ResetAt("a", "Runner", Epoch);
                service.RecordCartTransferAt("a", "Runner", VanillaFarmAutoSource.Use, 20, Epoch.AddSeconds(1));
                service.RecordCartTransferAt("a", "Runner", VanillaFarmAutoSource.Etc, 7, Epoch.AddSeconds(2));
                VanillaFarmMonitorSnapshot snapshot = service.SnapshotAt("a", "Runner", Epoch.AddSeconds(3));
                Equal(10L, snapshot.Items.Single(item => item.Id == "use").Count,
                    "Specific Use mapping should receive Use transfers.");
                Equal(7L, snapshot.Items.Single(item => item.Id == "any").Count,
                    "Any should receive only a category without its own mapping.");
            });
        }

        private static void UnassignedWeight()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "Runner", new[]
                {
                    Item("use", "Three weight", 1, 0, VanillaFarmAutoSource.Use, 3)
                });
                service.ResetAt("a", "Runner", Epoch);
                service.RecordCartTransferAt("a", "Runner", VanillaFarmAutoSource.Use, 10, Epoch.AddSeconds(1));
                service.RecordCartTransferAt("a", "Runner", VanillaFarmAutoSource.Etc, 5, Epoch.AddSeconds(2));
                VanillaFarmMonitorSnapshot snapshot = service.SnapshotAt("a", "Runner", Epoch.AddSeconds(3));
                Equal(0L, snapshot.Items[0].Count, "A non-divisible delta must not be guessed as quantity.");
                Equal(10L, snapshot.UnassignedUseWeight, "Non-divisible Use delta should remain unassigned.");
                Equal(5L, snapshot.UnassignedEtcWeight, "Unmapped Etc delta should remain unassigned.");
            });
        }

        private static void DuplicateAutomaticMapping()
        {
            WithService(service =>
            {
                bool rejected = false;
                try
                {
                    service.ReplaceItems("a", "Runner", new[]
                    {
                        Item("one", "One", 1, 0, VanillaFarmAutoSource.Etc, 1),
                        Item("two", "Two", 1, 0, VanillaFarmAutoSource.Etc, 1)
                    });
                }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Two automatic rows for the same category must be rejected as ambiguous.");
                Equal(0, service.SnapshotAt("a", "Runner", Epoch).Items.Count,
                    "A rejected edit must not leave invalid rows applied in memory.");
            });
        }

        private static void ManualValueCalculation()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "Runner", new[]
                {
                    Item("one", "Loot A", 100m, 50, VanillaFarmAutoSource.Manual, 1),
                    Item("two", "Loot B", 250.5m, 4, VanillaFarmAutoSource.Manual, 1)
                });
                service.ResetAt("a", "Runner", Epoch);
                service.SetRunningAt("a", "Runner", false, Epoch.AddHours(2));
                VanillaFarmMonitorSnapshot cleared = service.SnapshotAt("a", "Runner", Epoch.AddHours(2));
                Equal(0m, cleared.TotalZeny, "Reset should clear previous manual counts.");

                var edited = cleared.Items.Select(item => item.Clone()).ToArray();
                edited[0].Count = 50;
                edited[1].Count = 4;
                service.ReplaceItems("a", "Runner", edited);
                VanillaFarmMonitorSnapshot snapshot = service.SnapshotAt("a", "Runner", Epoch.AddHours(3));
                Equal(6002m, snapshot.TotalZeny, "Manual counts should calculate exact total zeny.");
                Equal(3001m, snapshot.ZenyPerHour, "Two active hours should produce exact zeny/hour.");
            });
        }

        private static void StoreRoundTrip()
        {
            string directory = Path.Combine(Path.GetTempPath(), "4RTools-FarmMonitor-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "farm-monitor.json");
            try
            {
                var first = new VanillaFarmMonitorService(new VanillaFarmMonitorStore(path));
                first.ReplaceItems("a", "Persist", new[] { Item("etc", "Loot", 12.5m, 0, VanillaFarmAutoSource.Etc, 1) });
                first.ResetAt("a", "Persist", Epoch);
                first.RecordCartTransferAt("a", "Persist", VanillaFarmAutoSource.Etc, 40, Epoch.AddSeconds(4));

                var second = new VanillaFarmMonitorService(new VanillaFarmMonitorStore(path));
                VanillaFarmMonitorSnapshot snapshot = second.SnapshotAt("a", "Persist", Epoch.AddSeconds(10));
                Assert(snapshot.Running, "Running state should survive store reload.");
                Equal(40L, snapshot.Items[0].Count, "Automatic count should survive store reload.");
                Equal(TimeSpan.FromSeconds(10), snapshot.Elapsed, "Elapsed run timing should survive store reload.");
                Equal(500m, snapshot.TotalZeny, "Persisted count and price should restore total zeny.");
            }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            }
        }

        private static VanillaFarmMonitorItem Item(string id, string name, decimal zeny, long count,
            VanillaFarmAutoSource source, uint unitWeight)
        {
            return new VanillaFarmMonitorItem
            {
                Id = id,
                Name = name,
                ZenyPerItem = zeny,
                Count = count,
                AutoSource = source,
                UnitWeight = unitWeight
            };
        }

        private static void WithService(System.Action<VanillaFarmMonitorService> action)
        {
            string directory = Path.Combine(Path.GetTempPath(), "4RTools-FarmMonitor-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "farm-monitor.json");
            try
            {
                action(new VanillaFarmMonitorService(new VanillaFarmMonitorStore(path)));
            }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            }
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(message + " Expected " + expected + ", got " + actual + ".");
        }
    }
}
