using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
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
                { "Farm monitor state survives store round trip", StoreRoundTrip },
                { "Reset rejects transfers that began in the previous run", ResetRejectsLateTransfer },
                { "Pause resume rejects transfers that crossed the pause boundary", PauseRejectsLateTransfer },
                { "Cart transfer tickets are consumed exactly once", TransferDeduplication },
                { "Reset and transfer accounting stay isolated per character", CharacterIsolation },
                { "Failed writes preserve the last saved rows timer and counts", WriteRollback },
                { "Calculator write failures do not propagate into Cart maintenance", TransferWriteFailure },
                { "Corrupt calculator storage remains intact without disabling the host", CorruptStorage },
                { "Zero unit weight is rejected without changing saved definitions", RejectZeroWeight },
                { "Localized decimal prices cannot be mistaken for grouped integers", DecimalPrices },
                { "Paused grid edits preserve precise configured prices", PausedGridPrecision }
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

        private static void ResetRejectsLateTransfer()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "A", new[] { Item("loot", "Loot", 10, 0, VanillaFarmAutoSource.Etc, 1) });
                service.ResetAt("a", "A", Epoch);
                var beforeReset = service.BeginCartTransfer("a", "A", VanillaFarmAutoSource.Etc);
                service.ResetAt("a", "A", Epoch.AddSeconds(5));
                Assert(!service.RecordCartTransferAt(beforeReset, 300, Epoch.AddSeconds(6)), "Old transfer entered a reset run.");
                Equal(0L, service.SnapshotAt("a", "A", Epoch.AddSeconds(7)).Items[0].Count, "Reset count was polluted.");
                var fresh = service.BeginCartTransfer("a", "A", VanillaFarmAutoSource.Etc);
                Assert(service.RecordCartTransferAt(fresh, 20, Epoch.AddSeconds(8)), "New run did not accept its own transfer.");
            });
        }

        private static void PauseRejectsLateTransfer()
        {
            WithService(service =>
            {
                service.ResetAt("a", "A", Epoch);
                var old = service.BeginCartTransfer("a", "A", VanillaFarmAutoSource.Use);
                service.SetRunningAt("a", "A", false, Epoch.AddSeconds(1));
                Assert(service.BeginCartTransfer("a", "A", VanillaFarmAutoSource.Use) == null, "Paused run issued a ticket.");
                service.SetRunningAt("a", "A", true, Epoch.AddSeconds(2));
                Assert(!service.RecordCartTransferAt(old, 30, Epoch.AddSeconds(3)), "Pre-pause transfer was counted after resume.");
                Equal(0L, service.SnapshotAt("a", "A", Epoch.AddSeconds(4)).UnassignedWeight, "Stale transfer changed unassigned weight.");
            });
        }

        private static void TransferDeduplication()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "A", new[] { Item("loot", "Loot", 10, 0, VanillaFarmAutoSource.Etc, 1) });
                service.ResetAt("a", "A", Epoch);
                var ticket = service.BeginCartTransfer("a", "A", VanillaFarmAutoSource.Etc);
                Assert(service.RecordCartTransferAt(ticket, 123, Epoch.AddSeconds(1)), "First delivery failed.");
                Assert(!service.RecordCartTransferAt(ticket, 123, Epoch.AddSeconds(2)), "Duplicate delivery was counted.");
                Equal(123L, service.SnapshotAt("a", "A", Epoch.AddSeconds(3)).Items[0].Count, "Duplicate quantity reached totals.");
                var replacement = new VanillaFarmMonitorService(new VanillaFarmMonitorStore(service.StorePath));
                var foreign = service.BeginCartTransfer("a", "A", VanillaFarmAutoSource.Etc);
                Assert(!replacement.RecordCartTransferAt(foreign, 123, Epoch.AddSeconds(4)), "A replacement service accepted an old owner's ticket.");
            });
        }

        private static void CharacterIsolation()
        {
            WithService(service =>
            {
                foreach (string id in new[] { "a", "b" })
                {
                    service.ReplaceItems(id, id, new[] { Item("loot", "Loot", 5, 0, VanillaFarmAutoSource.Etc, 1) });
                    service.ResetAt(id, id, Epoch);
                }
                var b = service.BeginCartTransfer("b", "b", VanillaFarmAutoSource.Etc);
                service.ResetAt("a", "a", Epoch.AddSeconds(1));
                Assert(service.RecordCartTransferAt(b, 40, Epoch.AddSeconds(2)), "Reset of A invalidated B.");
                Equal(0L, service.SnapshotAt("a", "a", Epoch.AddSeconds(3)).Items[0].Count, "B polluted A's count.");
                Equal(40L, service.SnapshotAt("b", "b", Epoch.AddSeconds(3)).Items[0].Count, "B lost its count.");
            });
        }

        private static void WriteRollback()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "A", new[] { Item("loot", "Loot", 10, 12, VanillaFarmAutoSource.Etc, 1) });
                string saved = File.ReadAllText(service.StorePath);
                Directory.CreateDirectory(service.StorePath + ".tmp");
                ExpectFailure(() => service.ReplaceItems("a", "A", new[] { Item("other", "Other", 20, 900, VanillaFarmAutoSource.Etc, 1) }));
                Equal("loot", service.SnapshotAt("a", "A", Epoch).Items[0].Id, "Failed save replaced rows in memory.");
                ExpectFailure(() => service.ResetAt("a", "A", Epoch));
                var snapshot = service.SnapshotAt("a", "A", Epoch.AddHours(1));
                Assert(!snapshot.Running, "Failed reset started the timer.");
                Equal(12L, snapshot.Items[0].Count, "Failed reset cleared counts.");
                Equal(saved, File.ReadAllText(service.StorePath), "Failed save altered durable state.");
                Directory.Delete(service.StorePath + ".tmp");
                service.ResetAt("a", "A", Epoch);
                Directory.CreateDirectory(service.StorePath + ".tmp");
                ExpectFailure(() => service.SetRunningAt("a", "A", false, Epoch.AddMinutes(1)));
                Assert(service.SnapshotAt("a", "A", Epoch.AddMinutes(2)).Running, "Failed pause changed run state.");
                Equal(TimeSpan.FromMinutes(2), service.SnapshotAt("a", "A", Epoch.AddMinutes(2)).Elapsed, "Failed pause changed elapsed time.");
            });
        }

        private static void TransferWriteFailure()
        {
            WithService(service =>
            {
                service.ReplaceItems("a", "A", new[] { Item("loot", "Loot", 10, 0, VanillaFarmAutoSource.Etc, 1) });
                service.ResetAt("a", "A", Epoch);
                var ticket = service.BeginCartTransfer("a", "A", VanillaFarmAutoSource.Etc);
                string saved = File.ReadAllText(service.StorePath);
                Directory.CreateDirectory(service.StorePath + ".tmp");
                string error;
                Assert(!service.TryRecordCartTransfer(ticket, 100, out error), "Failed transfer save reported success.");
                Assert(!string.IsNullOrWhiteSpace(error), "Missing-transfer warning was lost.");
                var snapshot = service.SnapshotAt("a", "A", Epoch.AddSeconds(5));
                Equal(0L, snapshot.Items[0].Count, "Failed transfer save leaked an unsaved count.");
                Assert(!string.IsNullOrWhiteSpace(snapshot.Warning), "UI snapshot has no visible incomplete-total warning.");
                Equal(saved, File.ReadAllText(service.StorePath), "Failed transfer changed the file.");
                Directory.Delete(service.StorePath + ".tmp");
                Assert(!service.TryRecordCartTransfer(ticket, 100, out error), "A failed delivery ticket was reused.");
                service.ResetAt("a", "A", Epoch.AddSeconds(6));
                Assert(string.IsNullOrWhiteSpace(service.SnapshotAt("a", "A", Epoch.AddSeconds(7)).Warning), "Reset did not clear the old warning.");
            });
        }

        private static void CorruptStorage()
        {
            WithService(service =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(service.StorePath));
                File.WriteAllText(service.StorePath, "{broken calculator data");
                var unavailable = new VanillaFarmMonitorService(new VanillaFarmMonitorStore(service.StorePath));
                Assert(!string.IsNullOrWhiteSpace(unavailable.SnapshotAt("a", "A", Epoch).Warning), "Corrupt file has no visible diagnostic.");
                ExpectFailure(() => unavailable.ResetAt("a", "A", Epoch));
                Equal("{broken calculator data", File.ReadAllText(service.StorePath), "Corrupt data was replaced with empty defaults.");
                Assert(unavailable.BeginCartTransfer("a", "A", VanillaFarmAutoSource.Use) == null, "Unavailable store accepted automatic accounting.");
            });
        }

        private static void RejectZeroWeight()
        {
            WithService(service =>
            {
                ExpectFailure(() => service.ReplaceItems("a", "A", new[] { Item("bad", "Invalid", 10, 0, VanillaFarmAutoSource.Use, 0) }));
                Equal(0, service.SnapshotAt("a", "A", Epoch).Items.Count, "Zero unit weight was retained.");
            });
        }

        private static readonly Type CardType = typeof(VanillaFarmMonitorPanel).GetNestedType("FarmMonitorCard", BindingFlags.NonPublic);
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void DecimalPrices()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                MethodInfo parse = CardType.GetMethod("ParseDecimal", BindingFlags.Static | BindingFlags.NonPublic);
                foreach (string value in new[] { "585.56", "585,56" })
                    Equal(585.56m, (decimal)parse.Invoke(null, new object[] { value, "Price" }), "Decimal separator inflated a price.");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        private static void PausedGridPrecision()
        {
            WithService(service =>
            {
                const decimal price = 159.625714285714m;
                service.ReplaceItems("a", "A", new[] { Item("loot", "Mixed loot estimate", price, 0, VanillaFarmAutoSource.Etc, 1) });
                using (var help = new ToolTip())
                using (var card = (Control)Activator.CreateInstance(CardType, InstancePrivate, null,
                    new object[] { service, new VanillaReconnectAccount { Id = "a", Label = "A" }, help }, CultureInfo.InvariantCulture))
                {
                    var grid = (DataGridView)CardType.GetField("grid", InstancePrivate).GetValue(card);
                    Assert(!grid.ReadOnly, "Paused rows are not editable.");
                    grid.Rows[0].Cells["Count"].Value = "50";
                    CardType.GetMethod("SaveGrid", InstancePrivate).Invoke(card, new object[0]);
                    var snapshot = service.SnapshotAt("a", "A", Epoch);
                    Equal(price, snapshot.Items[0].ZenyPerItem, "Editing a count rounded an unrelated price.");
                    Equal(50L, snapshot.Items[0].Count, "Paused count edit was not saved.");
                    string output = Environment.GetEnvironmentVariable("FOURRTOOLS_DATA_ROOT");
                    if (string.IsNullOrWhiteSpace(output)) throw new Exception("Isolated artifact root is required.");
                    Directory.CreateDirectory(output);
                    using (var host = new Form { ClientSize = new Size(1100, 360), ShowInTaskbar = false })
                    {
                        card.Dock = DockStyle.Fill;
                        card.AutoSize = false;
                        host.Controls.Add(card);
                        host.Show();
                        foreach (int width in new[] { 520, 1100, 1900 })
                        {
                            host.ClientSize = new Size(width, 360);
                            host.PerformLayout();
                            card.PerformLayout();
                            Application.DoEvents();
                            Assert(grid.Width <= card.ClientSize.Width, "Farm grid overflowed its card.");
                            using (var bitmap = new Bitmap(host.ClientSize.Width, host.ClientSize.Height))
                            {
                                host.DrawToBitmap(bitmap, host.ClientRectangle);
                                bitmap.Save(Path.Combine(output, "farm-monitor-" + width + ".png"), ImageFormat.Png);
                            }
                        }
                        host.Controls.Remove(card);
                        host.Hide();
                    }
                    service.ResetAt("a", "A", Epoch);
                    CardType.GetMethod("RefreshSnapshot", InstancePrivate).Invoke(card, new object[] { true });
                    Assert(grid.ReadOnly, "Running grid allowed edits.");
                    service.SetRunningAt("a", "A", false, Epoch.AddSeconds(10));
                    CardType.GetMethod("RefreshSnapshot", InstancePrivate).Invoke(card, new object[] { true });
                    Assert(!grid.ReadOnly, "Pausing failed to unlock the grid.");
                }
            });
        }

        private static void ExpectFailure(System.Action action)
        {
            bool failed = false;
            try { action(); } catch { failed = true; }
            Assert(failed, "Expected operation to fail.");
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
