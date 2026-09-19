using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using Newtonsoft.Json;
using _4RTools.Model.Vanilla;
using _4RTools.Utils;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaMemoryAndWeightTests
    {
        public static int Run()
        {
            int failed = 0;
            failed += Test("Memory finder parses decimal and hexadecimal values", ParseValues);
            failed += Test("Memory finder rejects values outside selected width", ParseBounds);
            failed += Test("Memory finder copies all candidates with module offsets", CandidateClipboard);
            failed += Test("Configured launcher resolves adjacent Vanilla executable", ConfiguredExecutableResolution);
            failed += Test("Weight fields accept UInt32 memory mappings", WeightMappings);
            failed += Test("Cart weight fields accept UInt32 memory mappings", CartWeightMappings);
            failed += Test("Known farming loot weights support capacity-safe Cart fill", CartCapacityRules);
            failed += Test("Weight alert thresholds enforce re-arm hysteresis", WeightThresholds);
            failed += Test("Enabled weight e-mail alerts require SMTP transport", WeightMailValidation);
            failed += Test("Cart milestone mail uses saved SMTP independently of weight-warning switch", MilestoneMailPolicy);
            failed += Test("Weight cart settings validate independent UI automation", WeightCartSettings);
            failed += Test("Legacy Weight settings inherit dedicated Alt+3 Autobattle STOP", WeightCartStopHotkeyMigration);
            failed += Test("Weight policy is independently switchable per character", WeightPolicyPerCharacter);
            failed += Test("Inventory vision finds toggled slot panel and occupied slot", InventoryVision);
            failed += Test("Cart first-slot classifier distinguishes empty from occupied and rotates safe destinations", CartFirstSlotClassifier);
            failed += Test("Inventory category rail is detected independent of location and scale", InventoryCategoryRail);
            failed += Test("Cart category selection accepts verified highlight transitions and safe retries", InventoryCategorySelectionVerification);
            failed += Test("Quantity Enter is armed only by positive quantity dialog structure", QuantityPromptGuard);
            failed += Test("Smart Teleport settings are per character with 60s default", SmartTeleportSettings);
            failed += Test("Smart Teleport idle trigger uses only fresh verified X/Y", SmartTeleportTracker);
            failed += Test("Smart Teleport Enter requires a positive warp-selection popup", SmartTeleportPopupGuard);
            failed += Test("Debug bundle summarizes recent teleport and cart actions", DebugActionSummary);
            failed += VanillaUtf8MemoryDiscoveryTests.Run();
            return failed;
        }

        private static int Test(string name, System.Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex); return 1; }
        }

        private static void ParseValues()
        {
            Equal(123L, VanillaMemoryDiscoverySession.ParseSearchValue(VanillaMemoryScanValueType.UInt32, "123"), "decimal");
            Equal(0xFFFFFFFFL, VanillaMemoryDiscoverySession.ParseSearchValue(VanillaMemoryScanValueType.UInt32, "0xFFFFFFFF"), "uint hex");
            Equal(-1L, VanillaMemoryDiscoverySession.ParseSearchValue(VanillaMemoryScanValueType.Int32, "0xFFFFFFFF"), "signed hex");
        }

        private static void ParseBounds()
        {
            Throws(() => VanillaMemoryDiscoverySession.ParseSearchValue(VanillaMemoryScanValueType.Byte, "256"));
            Throws(() => VanillaMemoryDiscoverySession.ParseSearchValue(VanillaMemoryScanValueType.UInt16, "-1"));
            Throws(() => VanillaMemoryDiscoverySession.ParseSearchValue(VanillaMemoryScanValueType.Int16, "32768"));
        }

        private static void CandidateClipboard()
        {
            var candidates = new List<VanillaMemoryCandidate>
            {
                new VanillaMemoryCandidate { Address = 0x401000, MainModuleOffset = 0x1000, PreviousValue = 383, CurrentValue = 388 },
                new VanillaMemoryCandidate { Address = 0xD362EC, MainModuleOffset = 0xD352EC, PreviousValue = 2630, CurrentValue = 2630 }
            };
            string text = VanillaMemoryDiscoveryPanel.FormatCandidatesForClipboard(9928, VanillaMemoryScanValueType.UInt32, VanillaMemoryScanScope.MainModule, candidates);
            Contains(text, "PID=9928\tType=UInt32\tScope=MainModule\tCandidates=2", "clipboard metadata");
            Contains(text, "0x00401000\t0x1000\t383\t388\t5", "first candidate");
            Contains(text, "0x00D362EC\t0xD352EC\t2630\t2630\t0", "second candidate");
        }

        private static void ConfiguredExecutableResolution()
        {
            string directory = Path.Combine(Path.GetTempPath(), "4rtools-vanilla-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string launcher = Path.Combine(directory, "Vanilla Launcher.exe");
                string client = Path.Combine(directory, "Vanilla MMO.exe");
                File.WriteAllBytes(launcher, new byte[] { 1 });
                File.WriteAllBytes(client, new byte[] { 2 });
                Equal(client, ReadOnlyProcessMemory.ResolveVanillaExecutableFromLaunch(launcher), "adjacent client");
                Equal(client, ReadOnlyProcessMemory.ResolveVanillaExecutableFromLaunch(client), "direct client");
                if (ReadOnlyProcessMemory.ResolveVanillaExecutableFromLaunch(Path.Combine(directory, "missing.exe")) != client)
                    throw new Exception("A configured launcher path in the Vanilla directory must still resolve the adjacent client.");
            }
            finally { try { Directory.Delete(directory, true); } catch { } }
        }

        private static void WeightMappings()
        {
            string json = "{\"SchemaVersion\":1,\"ProcessName\":\"Vanilla MMO.exe\",\"Fields\":{"
                + "\"CurrentWeight\":{\"Module\":\"Vanilla MMO.exe\",\"Address\":\"0x100\",\"Encoding\":\"UInt32\"},"
                + "\"MaxWeight\":{\"Module\":\"Vanilla MMO.exe\",\"Address\":\"0x104\",\"Encoding\":\"UInt32\"}}}";
            VanillaMemoryMap map = VanillaMemoryMap.Parse(json);
            if (!map.Fields.ContainsKey(VanillaField.CurrentWeight) || !map.Fields.ContainsKey(VanillaField.MaxWeight))
                throw new Exception("Weight fields were not retained by the memory map.");
        }

        private static void CartWeightMappings()
        {
            string json = "{\"SchemaVersion\":1,\"ProcessName\":\"Vanilla MMO.exe\",\"Fields\":{"
                + "\"CurrentCartWeight\":{\"Module\":\"Vanilla MMO.exe\",\"Address\":\"0x200\",\"Encoding\":\"UInt32\"},"
                + "\"MaxCartWeight\":{\"Module\":\"Vanilla MMO.exe\",\"Address\":\"0x204\",\"Encoding\":\"UInt32\"}}}";
            VanillaMemoryMap map = VanillaMemoryMap.Parse(json);
            if (!map.Fields.ContainsKey(VanillaField.CurrentCartWeight) || !map.Fields.ContainsKey(VanillaField.MaxCartWeight))
                throw new Exception("Cart weight fields were not retained by the memory map.");
        }

        private static void CartCapacityRules()
        {
            Equal(3L, VanillaWeightCartAutomation.KnownItemUnitWeightForCategory(0).Value, "Mastela Fruit unit weight");
            Equal(1L, VanillaWeightCartAutomation.KnownItemUnitWeightForCategory(2).Value, "Peco Feather unit weight");
            if (VanillaWeightCartAutomation.KnownItemUnitWeightForCategory(1).HasValue)
                throw new Exception("Equip must remain unknown until an item weight is explicitly verified.");
            Equal(16L, VanillaWeightCartAutomation.CapacitySafeQuantity(9950, 10000, 3), "Mastela capacity quantity");
            Equal(50L, VanillaWeightCartAutomation.CapacitySafeQuantity(9950, 10000, 1), "Peco Feather capacity quantity");
            Equal(0L, VanillaWeightCartAutomation.CapacitySafeQuantity(9998, 10000, 3), "Mastela cannot fill a 2-weight remainder");
            Equal(2L, VanillaWeightCartAutomation.CapacitySafeQuantity(9998, 10000, 1), "Peco Feather fills a 2-weight remainder");
            Equal(0L, VanillaWeightCartAutomation.CapacitySafeQuantity(10000, 10000, 1), "full Cart quantity");
            if (VanillaWeightCartAutomation.PrecisionThresholdPercent != 75m
                || VanillaWeightCartAutomation.FarmingDoneCartPercent != 99m
                || VanillaWeightCartAutomation.FarmingDoneCarryPercent != 50m)
                throw new Exception("Cart precision or farming completion threshold changed unexpectedly.");
            if (VanillaWeightCartAutomation.IsFarmingComplete(98.99m, 60m)
                || VanillaWeightCartAutomation.IsFarmingComplete(99m, 49.99m)
                || !VanillaWeightCartAutomation.IsFarmingComplete(99m, 50m)
                || !VanillaWeightCartAutomation.IsFarmingComplete(100m, 80m))
                throw new Exception("Farming completion must require Cart >=99% and carried weight >=50%.");
            if (VanillaWeightCartAutomation.RequiresPrecisionFill(74.999m)
                || !VanillaWeightCartAutomation.RequiresPrecisionFill(75m)
                || !VanillaWeightCartAutomation.RequiresPrecisionFill(95m)
                || !VanillaWeightCartAutomation.RequiresPrecisionFill(100m))
                throw new Exception("Cart precision filling must start exactly at 75%.");
            if (VanillaWeightAlertService.PrecisionCartRetrySeconds != 60)
                throw new Exception("Near-full Cart retry cadence changed unexpectedly.");
            if (VanillaWeightCartAutomation.TransferAttemptLimit < 3)
                throw new Exception("Cart transfers must attempt at least three slow drags before deferring.");
            if (VanillaWeightCartAutomation.TransientCartRetrySeconds != 60)
                throw new Exception("Transient Cart failures must retry after one minute.");
            if (!VanillaWeightCartAutomation.HpDamageExceeded(100m, 89.9m)
                || VanillaWeightCartAutomation.HpDamageExceeded(100m, 90m)
                || VanillaWeightCartAutomation.HpDamageAbortPercent != 10m)
                throw new Exception("Stopped Cart HP guard must abort only after a >10 percentage-point drop.");
            if (VanillaWeightCartAutomation.TransferSettleMs < 700
                || VanillaWeightCartAutomation.QuantityPromptTimeoutMs < 3000
                || VanillaWeightCartAutomation.CartProgressTimeoutMs < 4000
                || VanillaWeightCartAutomation.TransferRetryPauseMs < 1000)
                throw new Exception("Cart transfer pacing became too aggressive for lag tolerance.");
            if (VanillaForegroundInput.DeliberateDragStartHoldMs < 200
                || VanillaForegroundInput.DeliberateDragMoveSteps < 4
                || VanillaForegroundInput.DeliberateDragStepDelayMs < 20
                || VanillaForegroundInput.DeliberateDragStepDelayMs > 50
                || VanillaForegroundInput.DeliberateDragDestinationHoldMs < 250
                || VanillaForegroundInput.DeliberateDragPostReleaseMs < 400)
                throw new Exception("Cart drag must keep deliberate source/drop holds while cursor travel stays fast.");
        }

        private static void WeightThresholds()
        {
            var settings = new VanillaWeightAlertSettings { ThresholdPercent = 85m, RearmPercent = 80m };
            settings.Validate(false);
            settings.RearmPercent = 85m;
            Throws(() => settings.Validate(false));
            settings.RearmPercent = 90m;
            Throws(() => settings.Validate(false));
            settings.RearmPercent = 80m; settings.ThresholdPercent = 0m;
            Throws(() => settings.Validate(false));
        }

        private static void MilestoneMailPolicy()
        {
            var settings = new VanillaWeightAlertSettings
            {
                Enabled = false,
                SmtpHost = "smtp.example.invalid",
                SmtpPort = 587,
                FromAddress = "sender@example.invalid",
                ToAddress = "receiver@example.invalid"
            };
            if (!VanillaWeightAlertService.MilestoneMailConfigured(settings))
                throw new Exception("Valid saved SMTP must enable Cart-full/DONE milestone mail even when carried-weight warnings are off.");
            settings.SmtpHost = "";
            if (VanillaWeightAlertService.MilestoneMailConfigured(settings))
                throw new Exception("Milestone mail was enabled without a valid SMTP transport.");
        }

        private static void WeightCartSettings()
        {
            var settings = new VanillaWeightAlertSettings
            {
                AutoCartEnabled = true, AutoCartThresholdPercent = 50m, AutoCartRearmPercent = 40m,
                TransferUseItems = true, TransferEtcItems = true, TransferEquipItems = false
            };
            settings.Validate(false);
            if (settings.AutobattleStopHotkeyText != "Alt+3"
                || settings.InventoryHotkeyText != "Alt+E" || settings.CartHotkeyText != "Alt+W")
                throw new Exception("Default Autobattle STOP / Inventory / Cart hotkeys changed unexpectedly.");
            var account = new VanillaReconnectAccount
            {
                ResumeKey = (int)System.Windows.Forms.Keys.D2,
                ResumeCtrl = true,
                ResumeAlt = false,
                ResumeShift = false
            };
            if (settings.AutobattleStopHotkeyText == account.HotkeyText)
                throw new Exception("Weight Autobattle STOP must be independent from the character ResumeHotkey.");
            settings.AutobattleStopKey = 0;
            Throws(() => settings.Validate(false));
            settings.AutobattleStopKey = (int)System.Windows.Forms.Keys.D3;
            settings.AutoCartRearmPercent = 50m;
            Throws(() => settings.Validate(false));
            settings.AutoCartRearmPercent = 40m; settings.TransferUseItems = settings.TransferEtcItems = settings.TransferEquipItems = false;
            Throws(() => settings.Validate(false));
        }

        private static void WeightCartStopHotkeyMigration()
        {
            const string legacyJson = @"{""Version"":1,""AutoCartEnabled"":true,""AutoCartThresholdPercent"":50,""AutoCartRearmPercent"":40,"
                + @"""TransferUseItems"":true,""TransferEquipItems"":false,""TransferEtcItems"":true,"
                + @"""InventoryKey"":69,""InventoryAlt"":true,""CartKey"":87,""CartAlt"":true}";
            VanillaWeightAlertSettings legacy = JsonConvert.DeserializeObject<VanillaWeightAlertSettings>(legacyJson);
            if (legacy == null) throw new Exception("Legacy Weight JSON could not be deserialized.");
            legacy.Validate(false);
            if (legacy.AutobattleStopKey != (int)System.Windows.Forms.Keys.D3
                || !legacy.AutobattleStopAlt || legacy.AutobattleStopCtrl || legacy.AutobattleStopShift
                || legacy.AutobattleStopHotkeyText != "Alt+3")
                throw new Exception("Legacy Weight settings did not inherit the dedicated Alt+3 Autobattle STOP default.");

            legacy.AutobattleStopKey = (int)System.Windows.Forms.Keys.F8;
            legacy.AutobattleStopAlt = false;
            legacy.AutobattleStopCtrl = true;
            VanillaWeightAlertSettings roundTrip = legacy.Clone();
            if (roundTrip.AutobattleStopHotkeyText != "Ctrl+F8")
                throw new Exception("Configured Weight Autobattle STOP hotkey was not preserved by clone/serialization.");
        }

        private static void WeightPolicyPerCharacter()
        {
            var first = new VanillaReconnectAccount { Label = "A", UserName = "user", CharacterName = "char", WeightEnabled = true };
            var second = first.Clone();
            second.Id = Guid.NewGuid().ToString("N");
            second.CharacterName = "char2";
            second.WeightEnabled = false;
            if (!first.WeightEnabled || second.WeightEnabled)
                throw new Exception("Weight policy must be independent for each character row.");
            VanillaReconnectAccount roundTrip = second.Clone();
            if (roundTrip.WeightEnabled)
                throw new Exception("Disabled per-character Weight policy was not preserved by serialization/clone.");
            var identity = new VanillaCharacterIdentity(123, Guid.NewGuid(), DateTimeOffset.UtcNow, "char2", "user");
            VanillaCharacterRoster.FillMissing(roundTrip, identity);
            if (roundTrip.WeightEnabled)
                throw new Exception("Identity enrichment must not re-enable Weight for a character.");
        }

        private static void InventoryVision()
        {
            using (var before = new Bitmap(800, 600))
            using (var after = new Bitmap(800, 600))
            {
                using (Graphics g = Graphics.FromImage(before)) g.Clear(Color.FromArgb(80, 70, 55));
                using (Graphics g = Graphics.FromImage(after))
                {
                    g.Clear(Color.FromArgb(80, 70, 55));
                    Rectangle panel = new Rectangle(70, 90, 360, 300);
                    g.FillRectangle(Brushes.White, panel);
                    int[] xs = { 150, 191, 232, 273, 314, 355, 396 };
                    int[] ys = { 175, 216, 257, 298, 339 };
                    using (var pale = new SolidBrush(Color.FromArgb(205, 216, 232)))
                    {
                        foreach (int y in ys) foreach (int x in xs) g.FillEllipse(pale, x - 17, y - 9, 34, 18);
                    }
                    // One occupied source slot hides most of the empty-slot oval.
                    g.FillRectangle(Brushes.OrangeRed, xs[0] - 11, ys[0] - 10, 22, 21);
                }
                Rectangle detected; bool opened;
                if (!VanillaInventoryVision.TryFindToggledPanel(before, after, out detected, out opened) || !opened)
                    throw new Exception("Opened inventory panel was not detected.");
                VanillaUiSlotGrid grid = VanillaInventoryVision.DetectSlotGrid(after, detected);
                Point? occupied = VanillaInventoryVision.FirstOccupiedSlot(after, grid);
                if (!occupied.HasValue || Math.Abs(occupied.Value.X - 150) > 8 || Math.Abs(occupied.Value.Y - 175) > 8)
                    throw new Exception("Occupied inventory slot was not resolved from the detected lattice.");
            }
        }

        private static void CartFirstSlotClassifier()
        {
            using (var frame = new Bitmap(900, 650))
            {
                Rectangle panel = new Rectangle(90, 80, 420, 330);
                int[] columns = { 180, 222, 264, 306, 348, 390, 432 };
                int[] rows = { 160, 202, 244, 286, 328 };
                using (Graphics g = Graphics.FromImage(frame))
                {
                    g.Clear(Color.FromArgb(85, 75, 60));
                    g.FillRectangle(Brushes.White, panel);
                    using (var empty = new SolidBrush(Color.FromArgb(205, 216, 232)))
                        foreach (int y in rows)
                            foreach (int x in columns)
                                g.FillEllipse(empty, x - 18, y - 10, 36, 20);
                }

                var grid = new VanillaUiSlotGrid
                {
                    Panel = panel,
                    Columns = columns,
                    Rows = rows,
                    EmptyPaleThreshold = 220
                };

                VanillaInventoryFirstSlotObservation emptyObservation = VanillaInventoryVision.ObserveFirstSlot(frame, grid);
                if (emptyObservation.State != VanillaInventorySlotState.Empty)
                    throw new Exception("Empty first inventory slot was not positively recognized.");
                if (emptyObservation.PaleRatio < 0.90 || emptyObservation.TemplateDifference > 10)
                    throw new Exception("Empty first-slot evidence is not coherent with its empty reference slot.");

                using (Graphics g = Graphics.FromImage(frame))
                    g.FillRectangle(Brushes.OrangeRed, columns[0] - 12, rows[0] - 11, 24, 22);

                VanillaInventoryFirstSlotObservation occupiedObservation = VanillaInventoryVision.ObserveFirstSlot(frame, grid);
                if (occupiedObservation.State != VanillaInventorySlotState.Occupied)
                    throw new Exception("Occupied first inventory slot was not positively recognized.");

                // Repaint the exact first slot as empty to reproduce the live transition after
                // the final item was moved out of a category.
                using (Graphics g = Graphics.FromImage(frame))
                {
                    g.FillRectangle(Brushes.White, columns[0] - 22, rows[0] - 15, 44, 30);
                    using (var empty = new SolidBrush(Color.FromArgb(205, 216, 232)))
                        g.FillEllipse(empty, columns[0] - 18, rows[0] - 10, 36, 20);
                }
                VanillaInventoryFirstSlotObservation emptiedAgain = VanillaInventoryVision.ObserveFirstSlot(frame, grid);
                if (emptiedAgain.State != VanillaInventorySlotState.Empty)
                    throw new Exception("First slot did not return to Empty after the final item disappeared.");

                Point[] destinations = Enumerable.Range(0, 8)
                    .Select(i => VanillaInventoryVision.CartDropPoint(panel, i)).ToArray();
                if (destinations.Distinct().Count() < 6)
                    throw new Exception("Cart destination rotation did not vary across detected interior points.");
                foreach (Point point in destinations)
                    if (!panel.Contains(point))
                        throw new Exception("Detected Cart destination escaped the Cart panel.");

                // Regression from the 2026-09-19 live run: after one successful transfer,
                // populated Cart graphics can hide the empty-slot lattice. A later item must
                // still have a safe destination because the Cart body itself remains valid.
                using (Graphics g = Graphics.FromImage(frame))
                    g.FillRectangle(Brushes.DarkSlateBlue, panel);
                Point populatedCartDestination = VanillaInventoryVision.CartDropPoint(panel, 9);
                if (!panel.Contains(populatedCartDestination))
                    throw new Exception("Populated Cart lost its panel-based safe destination.");
            }
        }

        private static void InventoryCategoryRail()
        {
            // Critical live regression: Use is active while Fav remains blue.
            VerifyCategoryRail(new Size(900, 650), new Rectangle(70, 55, 360, 310), 34, 54, 0);
            VerifyCategoryRail(new Size(1500, 950), new Rectangle(760, 180, 500, 430), 48, 78, 1);
            VerifyCategoryRail(new Size(1200, 800), new Rectangle(140, 120, 420, 360), 40, 66, 3);
        }

        private static void VerifyCategoryRail(Size canvas, Rectangle panel, int slotSpacing, int categoryStep, int selectedIndex)
        {
            using (var frame = new Bitmap(canvas.Width, canvas.Height))
            {
                int railWidth = Math.Max(28, (int)Math.Round(slotSpacing * 1.15));
                int firstColumn = panel.Left + railWidth + slotSpacing;
                int origin = panel.Top + Math.Max(18, slotSpacing / 2);
                int[] columns = Enumerable.Range(0, 7).Select(i => firstColumn + i * slotSpacing).ToArray();
                int[] rows = Enumerable.Range(0, 5).Select(i => origin + categoryStep / 2 + i * slotSpacing).ToArray();
                int[] boundaries = Enumerable.Range(0, 5).Select(i => origin + i * categoryStep).ToArray();
                Rectangle rail = Rectangle.FromLTRB(panel.Left, boundaries[0], firstColumn - (int)Math.Round(slotSpacing * 0.65), boundaries[4]);

                using (Graphics g = Graphics.FromImage(frame))
                {
                    g.Clear(Color.FromArgb(85, 75, 60));
                    g.FillRectangle(Brushes.White, panel);
                    using (var empty = new SolidBrush(Color.FromArgb(205, 216, 232)))
                        foreach (int y in rows)
                            foreach (int x in columns)
                                g.FillEllipse(empty, x - Math.Max(12, slotSpacing * 3 / 8), y - Math.Max(7, slotSpacing / 5),
                                    Math.Max(24, slotSpacing * 3 / 4), Math.Max(14, slotSpacing * 2 / 5));

                    // Fav is visually blue even when it is NOT the active category.
                    using (var favorite = new SolidBrush(Color.FromArgb(204, 220, 248)))
                        g.FillRectangle(favorite, Rectangle.FromLTRB(rail.Left, boundaries[3], rail.Right, boundaries[4]));

                    using (var rule = new Pen(Color.FromArgb(205, 205, 205), 2))
                    {
                        foreach (int y in boundaries)
                            g.DrawLine(rule, rail.Left, y, rail.Right - 1, y);
                        g.DrawLine(rule, rail.Left, boundaries[0], rail.Left, boundaries[4]);

                        // Inactive tabs are closed on the right. The active tab merges into the
                        // inventory body, so its right border is deliberately absent.
                        for (int tab = 0; tab < 4; tab++)
                            if (tab != selectedIndex)
                                g.DrawLine(rule, rail.Right - 1, boundaries[tab] + 2,
                                    rail.Right - 1, boundaries[tab + 1] - 2);
                    }
                }

                var grid = new VanillaUiSlotGrid
                {
                    Panel = panel,
                    Columns = columns,
                    Rows = rows,
                    EmptyPaleThreshold = 220
                };
                VanillaInventoryCategoryTabs tabs = VanillaInventoryVision.DetectCategoryTabs(frame, grid);
                if (tabs.Tabs == null || tabs.Tabs.Length != 4)
                    throw new Exception("Four category tabs were not recovered from detected UI structure.");
                if (tabs.SelectedIndex != selectedIndex)
                    throw new Exception("Active category tab was not recognized from its open right edge.");
                if (tabs.RailBounds.IsEmpty || tabs.RightBorderX <= tabs.RailBounds.Left
                    || tabs.RailBounds.Width < rail.Width * 0.60)
                    throw new Exception("Category rail borders were not recovered structurally.");
                if (selectedIndex != 3 && tabs.SelectedIndex == 3)
                    throw new Exception("Blue Favorite styling was incorrectly treated as active selection.");
                for (int i = 0; i < tabs.Tabs.Length; i++)
                {
                    Point center = new Point(tabs.Tabs[i].Left + tabs.Tabs[i].Width / 2,
                        tabs.Tabs[i].Top + tabs.Tabs[i].Height / 2);
                    if (!rail.Contains(center))
                        throw new Exception("Detected category click target escaped the visually detected tab rail.");
                }

                if (VanillaInventoryVision.FirstOccupiedSlot(frame, grid).HasValue)
                    throw new Exception("An all-empty detected slot grid must be recognized as empty.");

                using (Graphics g = Graphics.FromImage(frame))
                    g.FillRectangle(Brushes.OrangeRed, columns[0] - slotSpacing / 4, rows[0] - slotSpacing / 5,
                        Math.Max(12, slotSpacing / 2), Math.Max(12, slotSpacing * 2 / 5));
                if (!VanillaInventoryVision.FirstOccupiedSlot(frame, grid).HasValue)
                    throw new Exception("Occupied slot was not recognized after adding an item to the detected grid.");
            }
        }

        private static void InventoryCategorySelectionVerification()
        {
            var before = new VanillaInventoryCategoryTabs
            {
                SelectedIndex = 3,
                SelectionScores = new[] { 0.08, 0.10, 0.09, 0.90 }
            };
            var direct = new VanillaInventoryCategoryTabs
            {
                SelectedIndex = 0,
                SelectionScores = new[] { 0.92, 0.08, 0.10, 0.09 }
            };
            if (!VanillaWeightCartAutomation.CategorySelectionConfirmed(0, before, direct))
                throw new Exception("Direct selected-index verification must accept the requested category.");

            // If the target tab becomes structurally open while the old tab closes, that
            // transition is positive evidence even when the absolute classifier is inconclusive.
            var transition = new VanillaInventoryCategoryTabs
            {
                SelectedIndex = -1,
                SelectionScores = new[] { 0.74, 0.18, 0.16, 0.20 }
            };
            if (!VanillaWeightCartAutomation.CategorySelectionConfirmed(0, before, transition))
                throw new Exception("Strong target-rise/old-selection-fall transition was rejected.");

            var ambiguous = new VanillaInventoryCategoryTabs
            {
                SelectedIndex = -1,
                SelectionScores = new[] { 0.35, 0.31, 0.29, 0.70 }
            };
            if (VanillaWeightCartAutomation.CategorySelectionConfirmed(0, before, ambiguous))
                throw new Exception("Ambiguous/unmoved category highlight must fail closed.");

            Rectangle tab = new Rectangle(20, 30, 30, 60);
            var points = Enumerable.Range(0, 3).Select(i => VanillaWeightCartAutomation.CategoryClickPoint(tab, i)).ToArray();
            if (points.Distinct().Count() != 3)
                throw new Exception("Bounded category retries must use distinct deterministic interior points.");
            foreach (Point point in points)
                if (!tab.Contains(point) || point.X <= tab.Left || point.X >= tab.Right - 1
                    || point.Y <= tab.Top || point.Y >= tab.Bottom - 1)
                    throw new Exception("Category retry click escaped the detected safe tab interior.");
        }

        private static void QuantityPromptGuard()
        {
            using (var noPrompt = new Bitmap(800, 600))
            using (var prompt = new Bitmap(800, 600))
            {
                using (Graphics g = Graphics.FromImage(noPrompt)) g.Clear(Color.FromArgb(80, 70, 55));
                using (Graphics g = Graphics.FromImage(prompt))
                {
                    g.Clear(Color.FromArgb(80, 70, 55));
                    g.FillRectangle(Brushes.White, 290, 250, 220, 58);
                    using (var selected = new SolidBrush(Color.FromArgb(111, 158, 242))) g.FillRectangle(selected, 306, 281, 70, 17);
                    g.FillRectangle(Brushes.LightGray, 445, 278, 48, 22);
                }
                if (VanillaInventoryVision.HasQuantityPrompt(noPrompt))
                    throw new Exception("A frame without a quantity dialog must never authorize Enter.");
                if (!VanillaInventoryVision.HasQuantityPrompt(prompt))
                    throw new Exception("Positive quantity-dialog structure was not detected.");

                using (var ordinaryUi = new Bitmap(800, 600))
                {
                    using (Graphics g = Graphics.FromImage(ordinaryUi))
                    {
                        g.Clear(Color.FromArgb(80, 70, 55));
                        // Basic Info-like panel with prominent blue bars must never be mistaken
                        // for the short/wide quantity modal.
                        g.FillRectangle(Brushes.White, 30, 35, 280, 120);
                        using (var bar = new SolidBrush(Color.FromArgb(90, 145, 235)))
                        {
                            g.FillRectangle(bar, 70, 80, 180, 12);
                            g.FillRectangle(bar, 70, 104, 160, 12);
                        }
                    }
                    if (VanillaInventoryVision.HasQuantityPrompt(ordinaryUi))
                        throw new Exception("Ordinary white/blue gameplay UI must never authorize Enter.");
                }
            }
        }

        private static void SmartTeleportSettings()
        {
            var first = new VanillaReconnectAccount { Label = "A", UserName = "user", CharacterName = "char" };
            if (first.SmartTeleportEnabled || first.SmartTeleportIdleSeconds != 60 || first.SmartTeleportKey != 0)
                throw new Exception("Smart Teleport defaults changed unexpectedly.");
            first.SmartTeleportEnabled = true;
            bool rejected = false;
            try { VanillaCharacterRoster.Validate(new[] { first }); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Enabled Smart Teleport without a hotkey must be rejected.");
            first.SmartTeleportKey = (int)System.Windows.Forms.Keys.F5;
            first.SmartTeleportCtrl = true;
            VanillaCharacterRoster.Validate(new[] { first });
            var second = first.Clone();
            second.Id = Guid.NewGuid().ToString("N");
            second.CharacterName = "char2";
            second.SmartTeleportEnabled = false;
            second.SmartTeleportIdleSeconds = 135;
            if (!first.SmartTeleportEnabled || second.SmartTeleportEnabled || second.SmartTeleportIdleSeconds != 135
                || first.SmartTeleportHotkeyText != "Ctrl+F5")
                throw new Exception("Smart Teleport policy is not independent per character.");
            VanillaReconnectAccount roundTrip = second.Clone();
            if (roundTrip.SmartTeleportEnabled || roundTrip.SmartTeleportIdleSeconds != 135
                || roundTrip.SmartTeleportKey != (int)System.Windows.Forms.Keys.F5)
                throw new Exception("Smart Teleport settings were not preserved by character serialization.");
        }

        private static void SmartTeleportTracker()
        {
            var tracker = new VanillaSmartTeleportTracker();
            DateTimeOffset utc = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
            Guid session = Guid.NewGuid();
            Func<int, int, int, bool, VanillaPositionSample> sample = (seconds, x, y, verified) =>
                new VanillaPositionSample(42, session, utc.AddSeconds(seconds), x, y, "map", verified, null);
            if (tracker.Observe(sample(0, 10, 20, true), TimeSpan.Zero, utc, 60))
                throw new Exception("First X/Y sample cannot be stationary timeout.");
            if (tracker.Observe(sample(30, 10, 20, true), TimeSpan.FromSeconds(30), utc.AddSeconds(30), 60))
                throw new Exception("Stationary timeout fired early.");
            if (!tracker.Observe(sample(60, 10, 20, true), TimeSpan.FromSeconds(60), utc.AddSeconds(60), 60))
                throw new Exception("Unchanged verified X/Y did not fire at 60 seconds.");
            if (tracker.Observe(sample(61, 11, 20, true), TimeSpan.FromSeconds(61), utc.AddSeconds(61), 60))
                throw new Exception("Movement must reset the Smart Teleport timer.");
            if (tracker.Observe(sample(121, 11, 20, false), TimeSpan.FromSeconds(121), utc.AddSeconds(121), 60))
                throw new Exception("Unverified coordinates must never authorize Smart Teleport.");
            if (tracker.Observe(sample(122, 11, 20, true), TimeSpan.FromSeconds(122), utc.AddSeconds(122), 60))
                throw new Exception("Fresh coordinates after an unknown gap require a new baseline.");
            if (!tracker.Observe(sample(182, 11, 20, true), TimeSpan.FromSeconds(182), utc.AddSeconds(182), 60))
                throw new Exception("New verified stationary baseline did not fire after 60 seconds.");
        }

        private static void SmartTeleportPopupGuard()
        {
            using (var before = new Bitmap(800, 600))
            using (var after = new Bitmap(800, 600))
            using (var quantity = new Bitmap(800, 600))
            {
                using (Graphics g = Graphics.FromImage(before)) g.Clear(Color.FromArgb(95, 80, 55));
                using (Graphics g = Graphics.FromImage(after))
                {
                    g.DrawImageUnscaled(before, 0, 0);
                    Rectangle dialog = new Rectangle(270, 300, 300, 125);
                    using (var light = new SolidBrush(Color.FromArgb(245, 245, 245))) g.FillRectangle(light, dialog);
                    using (var selected = new SolidBrush(Color.FromArgb(185, 205, 245))) g.FillRectangle(selected, 282, 329, 275, 19);
                    using (var button = new SolidBrush(Color.FromArgb(225, 225, 225)))
                    {
                        g.FillRectangle(button, 465, 390, 42, 22);
                        g.FillRectangle(button, 515, 390, 42, 22);
                    }
                }
                if (!VanillaTeleportVision.HasWarpDialog(before, after) || !VanillaTeleportVision.HasWarpDialog(null, after))
                    throw new Exception("Positive warp-selection popup structure was not recognized.");

                using (Graphics g = Graphics.FromImage(quantity))
                {
                    g.Clear(Color.FromArgb(95, 80, 55));
                    using (var light = new SolidBrush(Color.FromArgb(245, 245, 245))) g.FillRectangle(light, 290, 300, 220, 58);
                    using (var selected = new SolidBrush(Color.FromArgb(185, 205, 245))) g.FillRectangle(selected, 305, 328, 70, 17);
                }
                if (VanillaTeleportVision.HasWarpDialog(before, quantity))
                    throw new Exception("Short quantity dialog must never authorize Smart Teleport Enter.");
                if (VanillaTeleportVision.HasWarpDialog(null, before))
                    throw new Exception("Ordinary gameplay background must not be mistaken for the warp popup.");
            }
        }

        private static void DebugActionSummary()
        {
            DateTimeOffset now = new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
            string t = now.ToString("O");
            string old = now.AddDays(-2).ToString("O");
            string summary = VanillaDebugLog.BuildRecentActionSummary(new[]
            {
                old + " [TELEPORT] event=teleport-start mode=automatic-idle.",
                t + " [TELEPORT] event=teleport-start mode=automatic-idle.",
                t + " [TELEPORT] event=teleport-complete mode=automatic-idle.",
                t + " [WEIGHT] event=cart-start trigger=automatic-threshold.",
                t + " [WEIGHT] event=cart-complete trigger=automatic-threshold items=7.",
                t + " [WEIGHT] event=cart-manual-hold trigger=manual-test."
            }, now.AddHours(-24));
            Contains(summary, "Smart Teleport: attempts=1, completed=1", "teleport summary");
            Contains(summary, "Weight/Cart: attempts=1, completed=1, itemsMoved=7", "cart summary");
            Contains(summary, "manualHolds=1", "cart hold summary");
        }

        private static void WeightMailValidation()
        {
            var settings = new VanillaWeightAlertSettings { Enabled = true, ThresholdPercent = 85m, RearmPercent = 80m };
            Throws(() => settings.Validate(false));
            settings.SmtpHost = "smtp.example.invalid";
            settings.FromAddress = "sender@example.invalid";
            settings.ToAddress = "receiver@example.invalid";
            settings.Validate(false);
            VanillaWeightAlertSettings clone = settings.Clone();
            if (!clone.Enabled || clone.SmtpHost != settings.SmtpHost || clone.ToAddress != settings.ToAddress)
                throw new Exception("Weight alert settings clone lost data.");
        }

        private static void Equal(long expected, long actual, string label)
        {
            if (expected != actual) throw new Exception(label + ": expected " + expected + ", got " + actual + ".");
        }

        private static void Equal(string expected, string actual, string label)
        {
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new Exception(label + ": expected '" + expected + "', got '" + actual + "'.");
        }

        private static void Contains(string text, string expected, string label)
        {
            if (text == null || !text.Contains(expected)) throw new Exception(label + ": missing '" + expected + "'.");
        }

        private static void Throws(System.Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new Exception("Expected ArgumentException.");
        }
    }
}
