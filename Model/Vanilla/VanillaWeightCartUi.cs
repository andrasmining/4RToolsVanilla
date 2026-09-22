using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed partial class VanillaWeightCartAutomation
    {
        private VanillaFleetClientInfo CurrentClient(int pid)
        {
            return fleet.Poll().FirstOrDefault(item => item.ProcessId == pid);
        }

        private uint? CurrentWeight(int pid)
        {
            VanillaFleetClientInfo client = CurrentClient(pid);
            return client != null && client.WeightVerified ? client.CurrentWeight : null;
        }

        private VanillaCartWeightSample CurrentCartWeight(int pid)
        {
            VanillaFleetClientInfo client = CurrentClient(pid);
            if (client == null || !client.CartWeightVerified
                || !client.CurrentCartWeight.HasValue || !client.MaxCartWeight.HasValue
                || client.MaxCartWeight.Value == 0)
                return null;
            return new VanillaCartWeightSample
            {
                Current = client.CurrentCartWeight.Value,
                Maximum = client.MaxCartWeight.Value,
                Percent = client.CurrentCartWeight.Value * 100m / client.MaxCartWeight.Value
            };
        }

        private bool WaitForCartWeightIncrease(int pid, uint before, Func<bool> cancelled,
            out VanillaCartWeightSample after, int timeoutMs = CartProgressTimeoutMs)
        {
            after = null;
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                ThrowIfCancelled(cancelled);
                VanillaCartWeightSample current = CurrentCartWeight(pid);
                if (current != null)
                {
                    after = current;
                    if (current.Current > before) return true;
                }
                WaitWithCancellation(150, cancelled);
            }
            return false;
        }

        internal bool StopForFarmingCompletion(int pid, VanillaWeightAlertSettings settings, System.Action<string> report)
        {
            report = report ?? (_ => { });
            VanillaWeightMaintenanceToken token;
            string reason;
            if (!supervisor.TryBeginWeightMaintenance(pid, out token, out reason))
            {
                if (supervisor.IsWeightCompletedHold(supervisor.ManagedAccountIdForProcess(pid))) return true;
                report("Farming-complete stop deferred: " + reason + ".");
                return false;
            }

            bool paused = false;
            VanillaForegroundInput input = null;
            Func<bool> cancelled = () => supervisor.WeightMaintenanceCancelled(token);
            try
            {
                VanillaFleetClientInfo client = CurrentClient(pid);
                if (!HasCompleteWeights(client))
                {
                    supervisor.CompleteWeightMaintenance(token, false,
                        "Farming-complete stop cancelled because fresh weight conditions were no longer satisfied.");
                    return false;
                }
                input = new VanillaForegroundInput(pid) { CancellationRequested = cancelled };
                report(token.Account.Label + ": both farming-complete weight thresholds are verified; checking Autobattle STOP with "
                    + settings.AutobattleStopHotkeyText + ".");
                bool hpDamage; decimal? baseline;
                bool verified = VerifyAutobattleStopped(token, input, settings, cancelled, report,
                    () => paused = true, "farming-done", out hpDamage, out baseline);
                if (!verified)
                {
                    string failure = hpDamage ? "HP safety guard interrupted farming-complete STOP."
                        : "Farming-complete STOP was not verified after the bounded attempts.";
                    RecoverPaused(token, input, settings, Rectangle.Empty, Rectangle.Empty, paused, 0,
                        false, false, cancelled, report, failure + " No DONE hold/email authorized.");
                    return false;
                }
                paused = true;
                client = CurrentClient(pid);
                if (!HasCompleteWeights(client))
                {
                    RecoverPaused(token, input, settings, Rectangle.Empty, Rectangle.Empty, paused, 0,
                        false, false, cancelled, report, "Weights changed during STOP verification; no DONE hold/email authorized.");
                    return false;
                }
                ThrowIfCancelled(cancelled);
                if (!supervisor.MinimizeWeightMaintenanceClient(token))
                    report(token.Account.Label + ": verified STOP retained, but client minimization was not confirmed.");
                string detail = token.Account.Label + ": farming complete — Cart "
                    + client.CartWeightPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + "% and carried weight "
                    + client.WeightPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + "%; Autobattle STOP verified.";
                supervisor.CompleteWeightFarmingDone(token, detail);
                VanillaDebugLog.Write("WEIGHT", "event=farming-done-stop account='" + token.Account.Label
                    + "' accountId=" + token.AccountId + " pid=" + pid + ".");
                report(detail);
                return true;
            }
            catch (OperationCanceledException)
            {
                supervisor.MarkWeightMaintenanceCancelled(token, paused,
                    "Farming-complete stop cancelled by supervisor/settings/client ownership change.");
                return false;
            }
            catch (Exception ex)
            {
                string detail = "Farming-complete stop failed: " + ex.Message;
                if (input != null && !cancelled())
                    RecoverPaused(token, input, settings, Rectangle.Empty, Rectangle.Empty, paused, 0,
                        false, false, cancelled, report, detail);
                else supervisor.MarkWeightMaintenanceCancelled(token, paused, detail);
                return false;
            }
            finally { input?.Dispose(); }
        }

        private static bool HasCompleteWeights(VanillaFleetClientInfo client)
        {
            return client != null && client.WeightVerified && client.CartWeightVerified
                && client.WeightPercent.HasValue && client.CartWeightPercent.HasValue
                && client.Snapshot != null && client.Snapshot.SampledAtUtc <= DateTimeOffset.UtcNow
                && DateTimeOffset.UtcNow - client.Snapshot.SampledAtUtc <= TimeSpan.FromSeconds(3)
                && IsFarmingComplete(client.CartWeightPercent.Value, client.WeightPercent.Value);
        }

        private static bool WaitForQuantityPrompt(VanillaForegroundInput input, Func<bool> cancelled, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                ThrowIfCancelled(cancelled);
                using (Bitmap frame = input.CaptureClientBitmap())
                    {
                        VanillaQuantityObservation quantity;
                        if (VanillaCartQuantity.TryObserve(frame, out quantity) || VanillaInventoryVision.HasQuantityPrompt(frame)) return true;
                    }
                WaitWithCancellation(100, cancelled);
            }
            return false;
        }

        private bool WaitForWeightReduction(int pid, uint? before, Func<bool> cancelled)
        {
            if (!before.HasValue) return false;
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1800)
            {
                ThrowIfCancelled(cancelled);
                uint? current = CurrentWeight(pid);
                if (current.HasValue && current.Value < before.Value) return true;
                WaitWithCancellation(150, cancelled);
            }
            return false;
        }

        private bool VerifyAutobattleStopped(VanillaWeightMaintenanceToken token, VanillaForegroundInput input,
            VanillaWeightAlertSettings settings, Func<bool> cancelled, System.Action<string> report,
            System.Action onStopSent, string context, out bool hpDamageDetected, out decimal? hpBaselinePercent)
        {
            var clock = Stopwatch.StartNew();
            var verifier = new VanillaAutobattleStopVerifier();
            Func<VanillaClientState> read = () =>
            {
                VanillaFleetClientInfo client = fleet.Poll().FirstOrDefault(item => item.ProcessId == token.ProcessId);
                if (client == null || client.Snapshot == null)
                    throw new InvalidOperationException("Fresh fleet state is unavailable for Autobattle STOP verification.");
                return client.Snapshot;
            };

            bool verified = verifier.VerifyAsync(token.ProcessId, read, input.Activate,
                () =>
                {
                    // Mark potential STOP before native input, including a partially failed send.
                    onStopSent?.Invoke();
                    input.ChordInVerifiedForeground(settings.AutobattleStopCtrl, settings.AutobattleStopAlt,
                        settings.AutobattleStopShift, (Keys)settings.AutobattleStopKey);
                    VanillaDebugLog.Write("WEIGHT", "event=autobattle-stop-attempt context=" + context
                        + " account='" + token.Account.Label + "' accountId=" + token.AccountId
                        + " pid=" + token.ProcessId + " attempt=" + verifier.Attempts + "/"
                        + VanillaAutobattleStopVerifier.MaximumAttempts + " hotkey='"
                        + settings.AutobattleStopHotkeyText + "'.");
                },
                cancelled, () => clock.Elapsed, () => DateTimeOffset.UtcNow,
                milliseconds => Task.Delay(milliseconds),
                text => report(token.Account.Label + ": weight maintenance: " + text))
                .GetAwaiter().GetResult();

            hpDamageDetected = verifier.HpDamageDetected;
            hpBaselinePercent = verifier.HpBaselinePercent;
            VanillaDebugLog.Write("WEIGHT", "event=autobattle-stop-verification context=" + context
                + " account='" + token.Account.Label + "' accountId=" + token.AccountId
                + " pid=" + token.ProcessId + " verified=" + verified + " hpDamage=" + hpDamageDetected
                + " attempts=" + verifier.Attempts + ".");
            return verified;
        }

        private void VerifyResume(VanillaWeightMaintenanceToken token, VanillaForegroundInput input, Func<bool> cancelled, System.Action<string> report)
        {
            var clock = Stopwatch.StartNew();
            var verifier = new VanillaAutobattleResumeVerifier();
            Func<VanillaClientState> read = () =>
            {
                VanillaFleetClientInfo client = fleet.Poll().FirstOrDefault(item => item.ProcessId == token.ProcessId);
                if (client == null || client.Snapshot == null) throw new InvalidOperationException("Fresh fleet state is unavailable for autobattle verification.");
                return client.Snapshot;
            };
            verifier.VerifyAsync(token.ProcessId, read, input.Activate,
                () => input.ChordInVerifiedForeground(token.Account.ResumeCtrl, token.Account.ResumeAlt, token.Account.ResumeShift, (Keys)token.Account.ResumeKey),
                attempt =>
                {
                    string detail;
                    bool confirmed = VanillaVerifiedTeleportAction.TryExecute(token.ProcessId, token.Account, cancelled,
                        "weight-resume-" + attempt, out detail);
                    report(token.Account.Label + ": weight maintenance: teleport recovery " + attempt + "/"
                        + VanillaAutobattleResumeVerifier.MaximumAttempts + ": " + detail);
                    return confirmed;
                },
                cancelled, () => clock.Elapsed, () => DateTimeOffset.UtcNow,
                milliseconds => Task.Delay(milliseconds), text => report(token.Account.Label + ": weight maintenance: " + text))
                .GetAwaiter().GetResult();
        }

        private static Rectangle EnsureToggledPanel(VanillaForegroundInput input, bool ctrl, bool alt, bool shift, Keys key,
            string caption, Func<bool> cancelled, System.Action<string> report)
        {
            ThrowIfCancelled(cancelled);
            using (Bitmap before = input.CaptureClientBitmap())
            {
                input.Chord(ctrl, alt, shift, key);
                WaitWithCancellation(ToggleSettleMs, cancelled);
                using (Bitmap after = input.CaptureClientBitmap())
                {
                    bool opened;
                    Rectangle panel;
                    if (!VanillaInventoryVision.TryFindToggledPanel(before, after, out panel, out opened))
                        throw new InvalidOperationException(caption + " hotkey changed no confidently detectable slot panel; no drag input sent.");
                    if (opened)
                    {
                        report(caption + " detected at " + panel + ".");
                        return panel;
                    }
                    input.Chord(ctrl, alt, shift, key);
                    WaitWithCancellation(ToggleSettleMs, cancelled);
                    using (Bitmap reopened = input.CaptureClientBitmap())
                    {
                        Rectangle reopenedPanel; bool reopenedOpened;
                        if (!VanillaInventoryVision.TryFindToggledPanel(after, reopened, out reopenedPanel, out reopenedOpened) || !reopenedOpened)
                            throw new InvalidOperationException(caption + " was initially open but could not be reopened and verified; no drag input sent.");
                        report(caption + " was already open; toggled closed/reopened and detected at " + reopenedPanel + ".");
                        return reopenedPanel;
                    }
                }
            }
        }

        private static void ClosePanelIfOpen(VanillaForegroundInput input, bool ctrl, bool alt, bool shift, Keys key,
            Rectangle panel, string caption, Func<bool> cancelled)
        {
            ThrowIfCancelled(cancelled);
            using (Bitmap before = input.CaptureClientBitmap())
            {
                if (!VanillaInventoryVision.PanelStillPresent(before, panel)) return;
                input.Chord(ctrl, alt, shift, key);
                WaitWithCancellation(250, cancelled);
                VanillaDebugLog.Write("WEIGHT", caption + " close hotkey sent after verified panel presence.");
            }
        }

        private static void SelectCategory(VanillaForegroundInput input, Rectangle inventory, int category, string categoryName,
            Func<bool> cancelled, System.Action<string> report)
        {
            ThrowIfCancelled(cancelled);
            VanillaInventoryCategoryTabs original;
            using (Bitmap first = input.CaptureClientBitmap())
            {
                VanillaUiSlotGrid grid = VanillaInventoryVision.DetectSlotGrid(first, inventory);
                original = VanillaInventoryVision.DetectCategoryTabs(first, grid);
                if (category < 0 || category >= original.Tabs.Length)
                    throw new InvalidOperationException("Detected category rail does not contain the requested " + categoryName + " tab.");
                report("Detected four-tab inventory rail; current selection=" + CategoryName(original.SelectedIndex)
                    + ", target=" + categoryName + ", rail=" + original.RailBounds + ".");
                if (original.SelectedIndex == category)
                {
                    report(categoryName + " tab appears selected; confirming on a second fresh capture before any drag.");
                    if (ConfirmAlreadySelected(input, inventory, category, cancelled))
                    {
                        report(categoryName + " tab selection confirmed on two fresh captures.");
                        return;
                    }
                    report(categoryName + " second selection capture was inconclusive; continuing with detected-bound click attempts.");
                }
            }

            for (int attempt = 0; attempt < CategoryClickAttempts; attempt++)
            {
                ThrowIfCancelled(cancelled);
                VanillaInventoryCategoryTabs beforeTabs;
                Point target;
                using (Bitmap before = input.CaptureClientBitmap())
                {
                    VanillaUiSlotGrid grid = VanillaInventoryVision.DetectSlotGrid(before, inventory);
                    beforeTabs = VanillaInventoryVision.DetectCategoryTabs(before, grid);
                    if (beforeTabs.SelectedIndex == category)
                    {
                        report(categoryName + " tab appears selected before click attempt " + (attempt + 1)
                            + "; confirming on a second fresh capture.");
                        if (ConfirmAlreadySelected(input, inventory, category, cancelled))
                        {
                            report(categoryName + " tab selection confirmed before click attempt " + (attempt + 1) + ".");
                            return;
                        }
                        report(categoryName + " second capture was inconclusive; click attempt " + (attempt + 1) + " will proceed.");
                    }
                    Rectangle tab = beforeTabs.Tabs[category];
                    target = CategoryClickPoint(tab, attempt);
                    report(categoryName + " click attempt " + (attempt + 1) + "/" + CategoryClickAttempts
                        + " using a deterministic safe interior point from detected tab bounds " + tab + ".");
                    input.ClickNormalizedWithDiagnostics(NormalizeX(target.X, before.Width), NormalizeY(target.Y, before.Height));
                    report(categoryName + " click attempt " + (attempt + 1)
                        + " was accepted by Windows for the verified Vanilla client; waiting for visual state confirmation.");
                }

                Stopwatch watch = Stopwatch.StartNew();
                int stable = 0;
                while (watch.ElapsedMilliseconds < CategoryVerifyTimeoutMs)
                {
                    ThrowIfCancelled(cancelled);
                    WaitWithCancellation(CategorySettleMs, cancelled);
                    using (Bitmap after = input.CaptureClientBitmap())
                    {
                        VanillaUiSlotGrid grid = VanillaInventoryVision.DetectSlotGrid(after, inventory);
                        VanillaInventoryCategoryTabs afterTabs = VanillaInventoryVision.DetectCategoryTabs(after, grid);
                        bool confirmed = CategorySelectionConfirmed(category, beforeTabs, afterTabs);
                        if (confirmed)
                        {
                            stable++;
                            if (stable >= 2)
                            {
                                report(categoryName + " tab selection positively verified on two fresh captures after click attempt "
                                    + (attempt + 1) + ".");
                                return;
                            }
                        }
                        else
                        {
                            stable = 0;
                            if (watch.ElapsedMilliseconds >= CategoryVerifyTimeoutMs / 2)
                            {
                                report(categoryName + " click attempt " + (attempt + 1)
                                    + " still unverified; observed selection=" + CategoryName(afterTabs.SelectedIndex)
                                    + ", targetScore=" + FormatScore(afterTabs.SelectionScores, category) + ".");
                            }
                        }
                    }
                }

                report(categoryName + " click attempt " + (attempt + 1)
                    + " was not verified; re-detecting the rail before any retry. No item drag sent.");
            }

            throw new InvalidOperationException(categoryName
                + " tab remained unverified after " + CategoryClickAttempts
                + " detected-bound click attempts; no item drag sent.");
        }

        private static bool ConfirmAlreadySelected(VanillaForegroundInput input, Rectangle inventory, int category,
            Func<bool> cancelled)
        {
            ThrowIfCancelled(cancelled);
            WaitWithCancellation(CategorySettleMs, cancelled);
            using (Bitmap confirm = input.CaptureClientBitmap())
            {
                VanillaUiSlotGrid grid = VanillaInventoryVision.DetectSlotGrid(confirm, inventory);
                VanillaInventoryCategoryTabs tabs = VanillaInventoryVision.DetectCategoryTabs(confirm, grid);
                return tabs.SelectedIndex == category;
            }
        }

        internal static Point CategoryClickPoint(Rectangle tab, int attempt)
        {
            if (tab.Width < 3 || tab.Height < 3)
                throw new ArgumentException("Detected category tab is too small for a safe click.");
            double[] xFractions = { 0.50, 0.68, 0.32 };
            double[] yFractions = { 0.50, 0.43, 0.57 };
            int index = Math.Max(0, Math.Min(xFractions.Length - 1, attempt));
            int insetX = Math.Max(1, tab.Width / 6);
            int insetY = Math.Max(1, tab.Height / 6);
            int left = tab.Left + insetX, right = tab.Right - insetX - 1;
            int top = tab.Top + insetY, bottom = tab.Bottom - insetY - 1;
            if (right < left) { left = tab.Left + 1; right = tab.Right - 2; }
            if (bottom < top) { top = tab.Top + 1; bottom = tab.Bottom - 2; }
            int x = left + (int)Math.Round((right - left) * xFractions[index]);
            int y = top + (int)Math.Round((bottom - top) * yFractions[index]);
            return new Point(Math.Max(tab.Left + 1, Math.Min(tab.Right - 2, x)),
                Math.Max(tab.Top + 1, Math.Min(tab.Bottom - 2, y)));
        }

        internal static bool CategorySelectionConfirmed(int category, VanillaInventoryCategoryTabs before,
            VanillaInventoryCategoryTabs after)
        {
            if (after == null || after.SelectionScores == null || category < 0 || category >= after.SelectionScores.Length)
                return false;
            if (after.SelectedIndex == category) return true;

            double targetAfter = after.SelectionScores[category];
            double targetBefore = before != null && before.SelectionScores != null && category < before.SelectionScores.Length
                ? before.SelectionScores[category] : 0;
            double strongestOther = after.SelectionScores.Where((value, index) => index != category).DefaultIfEmpty(0).Max();
            bool targetDominates = targetAfter >= 0.52 && targetAfter - strongestOther >= 0.16;
            bool targetRose = targetAfter - targetBefore >= 0.18;

            bool oldSelectionClosed = true;
            if (before != null && before.SelectedIndex >= 0 && before.SelectedIndex != category
                && before.SelectionScores != null && before.SelectedIndex < before.SelectionScores.Length
                && before.SelectedIndex < after.SelectionScores.Length)
            {
                oldSelectionClosed = before.SelectionScores[before.SelectedIndex] - after.SelectionScores[before.SelectedIndex] >= 0.15;
            }
            return targetDominates && targetRose && oldSelectionClosed;
        }

        private static string FormatScore(double[] scores, int index)
        {
            return scores != null && index >= 0 && index < scores.Length
                ? scores[index].ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                : "n/a";
        }

        private bool TryDragNextDetectedItem(VanillaWeightMaintenanceToken token, VanillaForegroundInput input,
            Rectangle inventory, Rectangle cart, string categoryName, int transferSequence, Func<bool> cancelled, System.Action<string> report,
            out Point sourcePoint, out uint? weightBefore)
        {
            sourcePoint = Point.Empty;
            weightBefore = null;
            int emptyStable = 0, occupiedStable = 0;

            for (int sample = 1; sample <= FirstSlotVerifySamples; sample++)
            {
                ThrowIfCancelled(cancelled);
                using (Bitmap frame = input.CaptureClientBitmap())
                {
                    VanillaUiSlotGrid inventoryGrid = VanillaInventoryVision.DetectSlotGrid(frame, inventory);
                    VanillaInventoryFirstSlotObservation first = VanillaInventoryVision.ObserveFirstSlot(frame, inventoryGrid);
                    report(categoryName + " first-slot scan " + sample + "/" + FirstSlotVerifySamples
                        + ": " + first.State + " paleRatio=" + first.PaleRatio.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                        + " diff=" + first.TemplateDifference.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".");

                    if (first.State == VanillaInventorySlotState.Empty)
                    {
                        emptyStable++;
                        occupiedStable = 0;
                        if (emptyStable >= 2)
                            return false;
                    }
                    else if (first.State == VanillaInventorySlotState.Occupied)
                    {
                        occupiedStable++;
                        emptyStable = 0;
                        if (occupiedStable >= 2)
                        {
                            // The Cart accepts drops anywhere inside its item body. Do not require
                            // the Cart's empty-slot lattice to remain visually detectable after items
                            // have already been transferred: the live 2026-09-19 run proved that this
                            // can disappear while the Cart remains fully usable.
                            Point destination = VanillaInventoryVision.CartDropPoint(cart, transferSequence);
                            sourcePoint = first.Center;
                            weightBefore = CurrentWeight(token.ProcessId);
                            report(categoryName + " first slot confirmed occupied on two fresh captures; dragging it to a safe detected Cart interior point.");
                            input.DragFromProof(new Rectangle(sourcePoint.X - 1, sourcePoint.Y - 1, 3, 3),
                                new Rectangle(destination.X - 1, destination.Y - 1, 3, 3), input.LastCaptureProof);
                            return true;
                        }
                    }
                    else
                    {
                        emptyStable = 0;
                        occupiedStable = 0;
                    }
                }
                WaitWithCancellation(EmptyCategoryConfirmMs, cancelled);
            }

            throw new VanillaCartManualException(categoryName
                + " first-slot occupancy stayed ambiguous across " + FirstSlotVerifySamples
                + " fresh captures. No drag was sent; aborting this pass before paused-state recovery.");
        }

        private static string CategoryName(int category)
        {
            if (category == 0) return "Use";
            if (category == 1) return "Equip";
            if (category == 2) return "Etc";
            if (category == 3) return "Favorite";
            return "unknown";
        }

        private static double IntersectionRatio(Rectangle a, Rectangle b)
        {
            Rectangle intersection = Rectangle.Intersect(a, b);
            if (intersection.IsEmpty) return 0;
            return intersection.Width * intersection.Height / (double)Math.Min(a.Width * a.Height, b.Width * b.Height);
        }
        private static double NormalizeX(double x, int width) { return Math.Max(0, Math.Min(1, x / Math.Max(1.0, width - 1.0))); }
        private static double NormalizeY(double y, int height) { return Math.Max(0, Math.Min(1, y / Math.Max(1.0, height - 1.0))); }
        private static void WaitWithCancellation(int milliseconds, Func<bool> cancelled)
        {
            int remaining = Math.Max(0, milliseconds);
            while (remaining > 0)
            {
                ThrowIfCancelled(cancelled);
                int slice = Math.Min(100, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
            ThrowIfCancelled(cancelled);
        }

        private static void ThrowIfCancelled(Func<bool> cancelled) { if (cancelled()) throw new OperationCanceledException("Weight/cart maintenance cancelled."); }

        private sealed class VanillaCartManualException : Exception { internal VanillaCartManualException(string message) : base(message) { } }
    }

}
