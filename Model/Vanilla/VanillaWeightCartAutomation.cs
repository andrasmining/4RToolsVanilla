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
    internal sealed class VanillaWeightCartResult
    {
        internal bool RequiresManualIntervention;
        internal bool Deferred;
        internal bool CartFull;
        internal bool StoppedForCartSafety;
        internal int ItemsMoved;
        internal string Message;
    }

    internal sealed class VanillaCartWeightSample
    {
        internal uint Current;
        internal uint Maximum;
        internal decimal Percent;
        internal uint Remaining { get { return Maximum >= Current ? Maximum - Current : 0; } }
    }

    internal sealed class VanillaCartItemRule
    {
        internal int Category;
        internal string CategoryName;
        internal string ItemName;
        internal uint UnitWeight;
    }

    internal sealed class VanillaWeightMaintenanceToken
    {
        internal string AccountId;
        internal int ProcessId;
        internal int Generation;
        internal VanillaReconnectAccount Account;
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private readonly HashSet<string> weightManualHolds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> weightCompletedHolds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int weightMaintenanceGeneration;

        internal bool IsWeightEnabledForProcess(int pid)
        {
            lock (gate)
            {
                Runtime runtime = runtimes.Values.FirstOrDefault(item => item.ProcessId == pid && item.Account.Enabled);
                return runtime != null && runtime.Account.WeightEnabled;
            }
        }

        internal bool TryBeginWeightMaintenance(int pid, out VanillaWeightMaintenanceToken token, out string reason)
        {
            token = null; reason = null;
            lock (gate)
            {
                if (disposed || !running) { reason = "reconnect supervision is not running"; return false; }
                Runtime runtime = runtimes.Values.FirstOrDefault(item => item.ProcessId == pid && item.Account.Enabled);
                if (runtime == null) { reason = "the client is not assigned to an enabled character row"; return false; }
                if (!runtime.Account.WeightEnabled) { reason = "Weight/Cart is disabled for this character"; return false; }
                if (weightManualHolds.Contains(runtime.Account.Id)) { reason = "the character is waiting for manual cart emptying"; return false; }
                if (weightCompletedHolds.Contains(runtime.Account.Id)) { reason = "farming is complete for this character; clear the Weight hold before resuming"; return false; }
                if (runtime.ScriptRunning || runtime.RecoveryOwned || runtime.ClosingForRecovery
                    || runtimes.Values.Any(item => !ReferenceEquals(item, runtime) && (item.ScriptRunning || item.RecoveryOwned || item.ClosingForRecovery)))
                { reason = "another serialized startup/recovery/UI operation owns the input lease"; return false; }
                runtime.ScriptRunning = true;
                int generation = ++weightMaintenanceGeneration;
                token = new VanillaWeightMaintenanceToken
                {
                    AccountId = runtime.Account.Id, ProcessId = pid, Generation = generation, Account = runtime.Account.Clone()
                };
                SetStage(runtime, VanillaReconnectStage.Online, "Weight/cart maintenance owns the serialized client input lease");
                return true;
            }
        }

        internal bool WeightMaintenanceCancelled(VanillaWeightMaintenanceToken token)
        {
            if (token == null) return true;
            lock (gate)
            {
                Runtime runtime;
                return disposed || !running || token.Generation != weightMaintenanceGeneration
                    || !runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId
                    || !runtime.Account.Enabled || !runtime.Account.WeightEnabled || CharacterOwnershipChanged(runtime, token.ProcessId);
            }
        }

        internal void CompleteWeightMaintenance(VanillaWeightMaintenanceToken token, bool manualHold, string detail)
        {
            if (token == null) return;
            lock (gate)
            {
                if (token.Generation != weightMaintenanceGeneration) return;
                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId) return;
                runtime.ScriptRunning = false;
                runtime.MovementWatchdog.Reset();
                runtime.MovementRecoveryPending = false;
                runtime.NonMinimizedSince = null;
                if (manualHold)
                {
                    weightManualHolds.Add(token.AccountId);
                    SetStage(runtime, VanillaReconnectStage.Error, detail ?? "Weight/cart maintenance needs manual attention");
                }
                else
                {
                    weightManualHolds.Remove(token.AccountId);
                    weightCompletedHolds.Remove(token.AccountId);
                    SetStage(runtime, VanillaReconnectStage.Online, detail ?? "Weight/cart maintenance completed");
                }
            }
            RaiseUpdated();
        }

        internal bool IsWeightManualHold(string accountId)
        { lock (gate) return !string.IsNullOrWhiteSpace(accountId)
            && (weightManualHolds.Contains(accountId) || weightCompletedHolds.Contains(accountId)); }

        internal void CompleteWeightFarmingDone(VanillaWeightMaintenanceToken token, string detail)
        {
            if (token == null) return;
            lock (gate)
            {
                if (token.Generation != weightMaintenanceGeneration) return;
                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId) return;
                runtime.ScriptRunning = false;
                runtime.RecoveryOwned = false;
                runtime.MovementWatchdog.Reset();
                runtime.MovementRecoveryPending = false;
                runtime.NonMinimizedSince = null;
                weightManualHolds.Remove(token.AccountId);
                weightCompletedHolds.Add(token.AccountId);
                SetStage(runtime, VanillaReconnectStage.Stopped,
                    detail ?? "Farming complete: Cart full and carried weight target reached; Autobattle intentionally OFF");
            }
            RaiseUpdated();
        }

        internal bool IsWeightCompletedHold(string accountId)
        { lock (gate) return !string.IsNullOrWhiteSpace(accountId) && weightCompletedHolds.Contains(accountId); }

        internal void MarkWeightMaintenanceCancelled(VanillaWeightMaintenanceToken token, bool autobattleMayBePaused, string detail)
        {
            if (token == null) return;
            lock (gate)
            {
                // Once Autobattle may have been toggled OFF, retain the hold by stable
                // character-row ID even if a settings edit removed/rebuilt its runtime.
                if (autobattleMayBePaused) weightManualHolds.Add(token.AccountId);

                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId) return;
                runtime.ScriptRunning = false;
                runtime.MovementWatchdog.Reset();
                runtime.MovementRecoveryPending = false;
                runtime.NonMinimizedSince = null;
                if (autobattleMayBePaused)
                {
                    if (running)
                        SetStage(runtime, VanillaReconnectStage.Error, detail ?? "Weight/cart maintenance was cancelled after Autobattle may have been paused");
                    else
                        SetStage(runtime, VanillaReconnectStage.Stopped, "Supervisor stopped; manual Weight/Cart hold retained");
                }
                else if (running && token.Generation == weightMaintenanceGeneration)
                    SetStage(runtime, VanillaReconnectStage.Online, detail ?? "Weight/cart maintenance cancelled before pausing Autobattle");
            }
            RaiseUpdated();
        }

        public void ClearWeightManualHolds()
        {
            lock (gate)
            {
                var held = new HashSet<string>(weightManualHolds.Concat(weightCompletedHolds), StringComparer.OrdinalIgnoreCase);
                weightManualHolds.Clear();
                weightCompletedHolds.Clear();
                foreach (Runtime runtime in runtimes.Values)
                    if (held.Contains(runtime.Account.Id) && runtime.ProcessId.HasValue && runtime.Account.Enabled
                        && (runtime.Stage == VanillaReconnectStage.Error || runtime.Stage == VanillaReconnectStage.Stopped))
                        SetStage(runtime, VanillaReconnectStage.Online, "Weight/Cart hold explicitly cleared");
            }
            VanillaDebugLog.Write("WEIGHT", "event=cart-holds-cleared source=explicit-user-action manualAndCompleted=true.");
            RaiseUpdated();
        }

        internal bool MinimizeWeightMaintenanceClient(VanillaWeightMaintenanceToken token)
        {
            if (WeightMaintenanceCancelled(token)) return false;
            return MinimizeAssignedClientCore(token.AccountId, false, true);
        }

        internal void LogWeightMaintenanceActivity(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Reuse the supervisor/session log so Weight/Cart progress is visible in the
            // Recovery & relog Log pane as it happens, not only in COPY DEBUG LOG.
            Log("[WEIGHT] " + text);
        }
    }

    internal sealed class VanillaWeightCartAutomation
    {
        private const int ToggleSettleMs = 500;
        private const int CategorySettleMs = 140;
        private const int CategoryVerifyTimeoutMs = 1800;
        private const int CategoryClickAttempts = 3;
        private const int EmptyCategoryConfirmMs = 180;
        private const int FirstSlotVerifySamples = 4;
        internal const int TransferAttemptLimit = 3;
        internal const int TransferSettleMs = 700;
        internal const int QuantityPromptTimeoutMs = 2200;
        internal const int CartProgressTimeoutMs = 3000;
        internal const int TransferRetryPauseMs = 800;
        private const int MaxTransfers = 120;
        internal const decimal PrecisionThresholdPercent = 95m;
        internal const decimal CartFullPercent = 100m;
        internal const decimal FarmingDoneCarryPercent = 50m;
        private readonly VanillaFleetMonitor fleet;
        private readonly VanillaReconnectSupervisor supervisor;

        internal VanillaWeightCartAutomation(VanillaFleetMonitor fleet, VanillaReconnectSupervisor supervisor)
        {
            this.fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        }

        internal static VanillaCartItemRule KnownItemRule(int category)
        {
            // Current farming profile has exactly two known loot families:
            // Use -> Mastela Fruit (3 weight), Etc -> Peco Feather (1 weight).
            // Equip remains unknown and therefore cannot use precision filling above 95%.
            if (category == 0) return new VanillaCartItemRule
            {
                Category = 0, CategoryName = "Use", ItemName = "Mastela Fruit", UnitWeight = 3
            };
            if (category == 2) return new VanillaCartItemRule
            {
                Category = 2, CategoryName = "Etc", ItemName = "Peco Feather", UnitWeight = 1
            };
            return null;
        }

        internal static uint? KnownItemUnitWeightForCategory(int category)
        {
            VanillaCartItemRule rule = KnownItemRule(category);
            return rule == null ? (uint?)null : rule.UnitWeight;
        }

        internal static bool RequiresPrecisionFill(decimal cartPercent)
        {
            return cartPercent >= PrecisionThresholdPercent;
        }

        internal static uint CapacitySafeQuantity(uint currentCartWeight, uint maximumCartWeight, uint unitWeight)
        {
            if (unitWeight == 0 || maximumCartWeight <= currentCartWeight) return 0;
            return (maximumCartWeight - currentCartWeight) / unitWeight;
        }

        internal VanillaWeightCartResult Run(int pid, VanillaWeightAlertSettings settings, System.Action<string> report,
            string trigger = "automatic-threshold")
        {
            report = report ?? (_ => { });
            System.Action<string> activity = message =>
            {
                report(message);
                VanillaDebugLog.Write("WEIGHT", message);
                supervisor.LogWeightMaintenanceActivity(message);
            };
            VanillaWeightMaintenanceToken token;
            string reason;
            if (!supervisor.TryBeginWeightMaintenance(pid, out token, out reason))
            {
                VanillaDebugLog.Write("WEIGHT", "event=cart-deferred trigger=" + trigger + " pid=" + pid + " reason='" + reason + "'.");
                return new VanillaWeightCartResult { Deferred = true, Message = "Cart maintenance deferred: " + reason + "." };
            }

            VanillaCartWeightSample initialCart = CurrentCartWeight(pid);
            if (initialCart == null)
            {
                string detail = "Verified Cart current/max weight is unavailable; no Cart UI input was sent.";
                supervisor.CompleteWeightMaintenance(token, false, detail);
                VanillaDebugLog.Write("WEIGHT", "event=cart-deferred trigger=" + trigger + " account='" + token.Account.Label
                    + "' pid=" + pid + " stage=cart-weight reason='verified cart weight unavailable'.");
                return new VanillaWeightCartResult { Deferred = true, Message = token.Account.Label + ": " + detail };
            }
            if (initialCart.Percent >= CartFullPercent)
            {
                string detail = token.Account.Label + ": Cart is already full at " + initialCart.Current + "/" + initialCart.Maximum
                    + "; no transfer was attempted.";
                supervisor.CompleteWeightMaintenance(token, false, detail);
                VanillaDebugLog.Write("WEIGHT", "event=cart-already-full trigger=" + trigger + " account='" + token.Account.Label
                    + "' accountId=" + token.AccountId + " pid=" + pid + " cart=" + initialCart.Current + "/" + initialCart.Maximum + ".");
                return new VanillaWeightCartResult { CartFull = true, Message = detail };
            }

            VanillaDebugLog.Write("WEIGHT", "event=cart-start trigger=" + trigger + " account='" + token.Account.Label
                + "' accountId=" + token.AccountId + " pid=" + pid + " cart=" + initialCart.Current + "/" + initialCart.Maximum + ".");
            activity(token.Account.Label + ": weight maintenance started (" + trigger + "); serialized input lease acquired; Cart "
                + initialCart.Current + "/" + initialCart.Maximum + " (" + initialCart.Percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%).");
            bool paused = false, manualHold = false, completed = false;
            bool cartFull = false, cartSafetyStop = false;
            Rectangle inventory = Rectangle.Empty, cart = Rectangle.Empty;
            int moved = 0;
            Func<bool> cancelled = () => supervisor.WeightMaintenanceCancelled(token);
            VanillaForegroundInput openedInput;
            try { openedInput = new VanillaForegroundInput(pid); }
            catch (Exception ex)
            {
                VanillaDebugLog.Write("WEIGHT", "event=cart-deferred trigger=" + trigger + " account='" + token.Account.Label
                    + "' pid=" + pid + " stage=window reason='" + ex.Message + "'.");
                supervisor.CompleteWeightMaintenance(token, false, "Weight/cart maintenance could not acquire the verified client window: " + ex.Message);
                return new VanillaWeightCartResult { Deferred = true, Message = token.Account.Label + ": cart maintenance deferred: " + ex.Message };
            }
            using (var input = openedInput)
            {
                input.CancellationRequested = cancelled;
                try
                {
                    activity(token.Account.Label + ": weight maintenance: stopping Autobattle with dedicated Weight hotkey "
                        + settings.AutobattleStopHotkeyText + ".");
                    VanillaDebugLog.Write("WEIGHT", "event=cart-autobattle-stop account='" + token.Account.Label
                        + "' accountId=" + token.AccountId + " pid=" + pid + " hotkey='" + settings.AutobattleStopHotkeyText + "'.");
                    input.Chord(settings.AutobattleStopCtrl, settings.AutobattleStopAlt,
                        settings.AutobattleStopShift, (Keys)settings.AutobattleStopKey);
                    paused = true;
                    Thread.Sleep(700);
                    ThrowIfCancelled(cancelled);

                    inventory = EnsureToggledPanel(input, settings.InventoryCtrl, settings.InventoryAlt, settings.InventoryShift,
                        (Keys)settings.InventoryKey, "Inventory", cancelled, activity);
                    cart = EnsureToggledPanel(input, settings.CartCtrl, settings.CartAlt, settings.CartShift,
                        (Keys)settings.CartKey, "Cart", cancelled, activity);
                    if (inventory.IntersectsWith(cart) && IntersectionRatio(inventory, cart) > 0.60)
                        throw new InvalidOperationException("Inventory and Cart overlap too heavily for safe drag-and-drop. Move them apart once and retry.");

                    var categories = new List<int>();
                    if (settings.TransferUseItems) categories.Add(0);
                    if (settings.TransferEquipItems) categories.Add(1);
                    if (settings.TransferEtcItems) categories.Add(2);
                    foreach (int category in categories)
                    {
                        if (cartFull) break;
                        ThrowIfCancelled(cancelled);
                        string categoryName = CategoryName(category);
                        VanillaCartItemRule itemRule = KnownItemRule(category);
                        activity(token.Account.Label + ": weight maintenance: detecting and selecting " + categoryName + " tab"
                            + (itemRule == null ? "." : "; known farming item=" + itemRule.ItemName + " unitWeight=" + itemRule.UnitWeight + "."));
                        SelectCategory(input, inventory, category, categoryName, cancelled, activity);
                        int categoryMoved = 0;
                        while (moved < MaxTransfers)
                        {
                            ThrowIfCancelled(cancelled);
                            VanillaCartWeightSample cartBefore = CurrentCartWeight(token.ProcessId);
                            if (cartBefore == null)
                            {
                                manualHold = true;
                                throw new VanillaCartManualException("Verified Cart weight disappeared while Autobattle was OFF. No further transfer is safe.");
                            }
                            if (cartBefore.Percent >= CartFullPercent)
                            {
                                cartFull = true;
                                activity(token.Account.Label + ": weight maintenance: Cart confirmed full at " + cartBefore.Current + "/"
                                    + cartBefore.Maximum + "; no further drag will be sent.");
                                break;
                            }

                            bool precision = RequiresPrecisionFill(cartBefore.Percent);
                            if (precision && itemRule == null)
                            {
                                cartSafetyStop = true;
                                activity(token.Account.Label + ": weight maintenance: Cart is "
                                    + cartBefore.Percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                                    + "% full (>=95%). " + categoryName
                                    + " has no verified unit-weight rule, so this category is skipped and no blind full-stack transfer is sent.");
                                break;
                            }
                            if (precision && cartBefore.Remaining < itemRule.UnitWeight)
                            {
                                cartSafetyStop = true;
                                activity(token.Account.Label + ": weight maintenance: only " + cartBefore.Remaining
                                    + " Cart weight remains, less than one " + itemRule.ItemName + " (" + itemRule.UnitWeight
                                    + "); this category is skipped so a lighter known category can still fill the remainder.");
                                break;
                            }

                            VanillaCartWeightSample cartAfter = null;
                            bool transferSucceeded = false;
                            bool categoryEmpty = false;
                            bool abortForCapacity = false;
                            bool quantity = false;
                            bool weightReduced = false;
                            uint? requestedQuantity = null;

                            for (int transferAttempt = 1; transferAttempt <= TransferAttemptLimit; transferAttempt++)
                            {
                                ThrowIfCancelled(cancelled);

                                if (transferAttempt > 1)
                                {
                                    // Never repeat a drag if delayed read-only evidence now proves
                                    // the preceding attempt succeeded.
                                    VanillaCartWeightSample lateProgress = CurrentCartWeight(token.ProcessId);
                                    if (lateProgress != null && lateProgress.Current > cartBefore.Current)
                                    {
                                        cartAfter = lateProgress;
                                        transferSucceeded = true;
                                        activity(token.Account.Label + ": weight maintenance: delayed Cart-weight progress appeared before retry "
                                            + transferAttempt + "/" + TransferAttemptLimit + "; previous drag succeeded, so no duplicate drag is sent.");
                                        break;
                                    }

                                    activity(token.Account.Label + ": weight maintenance: transfer attempt "
                                        + (transferAttempt - 1) + "/" + TransferAttemptLimit
                                        + " showed no Cart-weight increase; waiting " + TransferRetryPauseMs
                                        + "ms, then re-detecting the first slot before retry " + transferAttempt + "/" + TransferAttemptLimit + ".");
                                    Thread.Sleep(TransferRetryPauseMs);
                                }

                                uint? pendingWeightBefore;
                                Point sourcePoint;
                                if (!TryDragNextDetectedItem(token, input, inventory, cart, categoryName,
                                    moved * TransferAttemptLimit + transferAttempt - 1, cancelled, activity,
                                    out sourcePoint, out pendingWeightBefore))
                                {
                                    if (transferAttempt == 1)
                                    {
                                        categoryEmpty = true;
                                        break;
                                    }

                                    // The source disappeared after an earlier drag. Give Cart memory
                                    // one final bounded chance to catch up; do not blindly drag a
                                    // different compacted item when the prior outcome is uncertain.
                                    if (WaitForCartWeightIncrease(token.ProcessId, cartBefore.Current, cancelled,
                                        out cartAfter, CartProgressTimeoutMs))
                                    {
                                        transferSucceeded = true;
                                        activity(token.Account.Label + ": weight maintenance: source slot became empty after retry preparation, "
                                            + "and delayed Cart-weight progress confirmed the preceding drag succeeded.");
                                        break;
                                    }

                                    manualHold = true;
                                    throw new VanillaCartManualException(categoryName
                                        + " first slot became empty after a failed transfer attempt but Cart weight never increased. "
                                        + "No further drag is safe; Autobattle stays OFF for manual inspection.");
                                }

                                activity(token.Account.Label + ": weight maintenance: slow drag attempt " + transferAttempt + "/"
                                    + TransferAttemptLimit + " sent; allowing the client to settle before checking quantity/progress.");
                                Thread.Sleep(TransferSettleMs);

                                quantity = WaitForQuantityPrompt(input, cancelled, QuantityPromptTimeoutMs);
                                requestedQuantity = null;
                                if (quantity)
                                {
                                    if (precision)
                                    {
                                        uint fit = CapacitySafeQuantity(cartBefore.Current, cartBefore.Maximum, itemRule.UnitWeight);
                                        if (fit == 0)
                                        {
                                            activity(token.Account.Label + ": weight maintenance: quantity dialog detected but no "
                                                + itemRule.ItemName + " can fit; cancelling transfer.");
                                            input.Press(Keys.Escape);
                                            cartSafetyStop = true;
                                            abortForCapacity = true;
                                            break;
                                        }
                                        bool fullStackDefinitelyFits = pendingWeightBefore.HasValue
                                            && cartBefore.Remaining >= pendingWeightBefore.Value;
                                        if (fullStackDefinitelyFits)
                                        {
                                            activity(token.Account.Label + ": weight maintenance: Cart is at/above 95%, but the remaining "
                                                + cartBefore.Remaining + " capacity is at least the character's entire current carried weight "
                                                + pendingWeightBefore.Value + "; the full stack is provably safe.");
                                            input.Press(Keys.Enter);
                                            Thread.Sleep(TransferSettleMs);
                                        }
                                        else
                                        {
                                            requestedQuantity = fit;
                                            activity(token.Account.Label + ": weight maintenance: Cart "
                                                + cartBefore.Current + "/" + cartBefore.Maximum + " is at/above 95%; precision fill for "
                                                + itemRule.ItemName + " (" + itemRule.UnitWeight + " weight each) requests at most " + fit
                                                + " item(s) to fit the remaining " + cartBefore.Remaining + " weight.");
                                            input.ReplaceFocusedText(fit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                            input.Press(Keys.Enter);
                                            Thread.Sleep(TransferSettleMs);

                                            bool promptStillOpen;
                                            using (Bitmap quantityCheck = input.CaptureClientBitmap())
                                                promptStillOpen = VanillaInventoryVision.HasQuantityPrompt(quantityCheck);
                                            if (promptStillOpen)
                                            {
                                                uint chunk = Math.Min(fit, 20U);
                                                requestedQuantity = chunk;
                                                activity(token.Account.Label + ": weight maintenance: precision quantity was not accepted immediately; "
                                                    + "retrying a small capacity-safe chunk of " + chunk + " " + itemRule.ItemName + ".");
                                                input.ReplaceFocusedText(chunk.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                                input.Press(Keys.Enter);
                                                Thread.Sleep(TransferSettleMs);

                                                using (Bitmap chunkCheck = input.CaptureClientBitmap())
                                                    promptStillOpen = VanillaInventoryVision.HasQuantityPrompt(chunkCheck);
                                                if (promptStillOpen)
                                                {
                                                    requestedQuantity = 1;
                                                    activity(token.Account.Label + ": weight maintenance: small chunk was still not accepted; "
                                                        + "falling back to one " + itemRule.ItemName + " so the transfer remains capacity-safe.");
                                                    input.ReplaceFocusedText("1");
                                                    input.Press(Keys.Enter);
                                                    Thread.Sleep(TransferSettleMs);
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        activity(token.Account.Label + ": weight maintenance: quantity dialog positively detected; pressing Enter for the full stack.");
                                        input.Press(Keys.Enter);
                                        Thread.Sleep(TransferSettleMs);
                                    }
                                }
                                else
                                {
                                    activity(token.Account.Label + ": weight maintenance: no quantity dialog detected after "
                                        + QuantityPromptTimeoutMs + "ms; Enter NOT sent (single-item path remains possible).");
                                }

                                bool cartIncreased = WaitForCartWeightIncrease(token.ProcessId, cartBefore.Current, cancelled,
                                    out cartAfter, CartProgressTimeoutMs);
                                using (Bitmap verify = input.CaptureClientBitmap())
                                {
                                    if (VanillaInventoryVision.HasQuantityPrompt(verify))
                                    {
                                        manualHold = true;
                                        throw new VanillaCartManualException("A quantity dialog remained or appeared late. No further key was sent; Autobattle stays OFF for manual inspection.");
                                    }
                                }

                                if (!cartIncreased)
                                {
                                    if (transferAttempt < TransferAttemptLimit)
                                    {
                                        activity(token.Account.Label + ": weight maintenance: transfer attempt " + transferAttempt + "/"
                                            + TransferAttemptLimit + " produced no verified Cart-weight increase after "
                                            + CartProgressTimeoutMs + "ms; the item will be re-detected and retried.");
                                        continue;
                                    }

                                    manualHold = true;
                                    throw new VanillaCartManualException("Cart did not show a verified weight increase after "
                                        + TransferAttemptLimit + " slow drag attempts. Autobattle stays OFF for manual inspection.");
                                }

                                weightReduced = WaitForWeightReduction(token.ProcessId, pendingWeightBefore, cancelled);
                                transferSucceeded = true;
                                break;
                            }

                            if (abortForCapacity) break;
                            if (categoryEmpty)
                            {
                                activity(token.Account.Label + ": weight maintenance: " + categoryName
                                    + " confirmed empty because the first inventory slot was empty on two fresh classified captures; moved "
                                    + categoryMoved + " transfer(s) from this tab; advancing.");
                                VanillaDebugLog.Write("WEIGHT", "event=cart-category-empty account='" + token.Account.Label
                                    + "' accountId=" + token.AccountId + " pid=" + pid + " category=" + categoryName
                                    + " moved=" + categoryMoved + " samples=2.");
                                break;
                            }
                            if (!transferSucceeded || cartAfter == null)
                            {
                                manualHold = true;
                                throw new VanillaCartManualException("Transfer retry sequence ended without verified Cart progress. "
                                    + "Autobattle stays OFF for manual inspection.");
                            }

                            if (!weightReduced)
                            {
                                activity(token.Account.Label + ": weight maintenance: Cart weight increased from "
                                    + cartBefore.Current + " to " + cartAfter.Current
                                    + "; carried-weight reduction was not independently observed, so Cart weight remains the transfer proof.");
                            }

                            if (precision)
                            {
                                uint delta = cartAfter.Current - cartBefore.Current;
                                if (delta == 0 || delta % itemRule.UnitWeight != 0)
                                {
                                    manualHold = true;
                                    throw new VanillaCartManualException("Precision Cart transfer changed weight by " + delta
                                        + ", which does not match the known " + itemRule.ItemName + " unit weight " + itemRule.UnitWeight
                                        + ". Item identity is uncertain; no further transfer is safe.");
                                }
                                if (!quantity && delta != itemRule.UnitWeight)
                                {
                                    manualHold = true;
                                    throw new VanillaCartManualException("Single-item Cart transfer changed weight by " + delta
                                        + " instead of the expected " + itemRule.UnitWeight + " for " + itemRule.ItemName + ".");
                                }
                                if (requestedQuantity.HasValue
                                    && (ulong)delta > (ulong)requestedQuantity.Value * (ulong)itemRule.UnitWeight)
                                {
                                    manualHold = true;
                                    throw new VanillaCartManualException("Precision Cart transfer exceeded the requested capacity-safe quantity.");
                                }
                            }
                            moved++;
                            categoryMoved++;
                            activity(token.Account.Label + ": weight maintenance: moved " + categoryName + " transfer "
                                + categoryMoved + " (total " + moved + "); Cart " + cartAfter.Current + "/" + cartAfter.Maximum
                                + " (" + cartAfter.Percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%).");

                            if (cartAfter.Percent >= CartFullPercent)
                            {
                                cartFull = true;
                                activity(token.Account.Label + ": weight maintenance: Cart reached 100%; stopping Cart transfers.");
                                break;
                            }
                        }
                        if (moved >= MaxTransfers)
                        {
                            manualHold = true;
                            throw new VanillaCartManualException("Transfer safety limit reached. Autobattle is left OFF for manual inspection.");
                        }
                    }

                    if (cartFull)
                        activity(token.Account.Label + ": weight maintenance: Cart is full; remaining inventory stays on the character.");
                    else if (cartSafetyStop)
                        activity(token.Account.Label + ": weight maintenance: stopped Cart filling safely at/above 95%; no unverified-weight item will be transferred.");
                    else
                        activity(token.Account.Label + ": weight maintenance: every enabled inventory category is confirmed complete.");

                    ClosePanelIfOpen(input, settings.CartCtrl, settings.CartAlt, settings.CartShift, (Keys)settings.CartKey, cart, "Cart", cancelled);
                    ClosePanelIfOpen(input, settings.InventoryCtrl, settings.InventoryAlt, settings.InventoryShift, (Keys)settings.InventoryKey, inventory, "Inventory", cancelled);

                    VanillaFleetClientInfo afterUi = CurrentClient(token.ProcessId);
                    decimal? carriedPercent = afterUi == null ? null : afterUi.WeightPercent;
                    if (cartFull && carriedPercent.HasValue && carriedPercent.Value >= FarmingDoneCarryPercent)
                    {
                        string done = token.Account.Label + ": farming complete immediately after Cart maintenance: Cart 100% and carried weight "
                            + carriedPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                            + "%; Autobattle remains OFF.";
                        activity(done);
                        supervisor.CompleteWeightFarmingDone(token, done);
                        paused = false;
                        completed = true;
                        VanillaDebugLog.Write("WEIGHT", "event=farming-done trigger=" + trigger + " account='" + token.Account.Label
                            + "' accountId=" + token.AccountId + " pid=" + pid + " cartPercent=100 carriedPercent="
                            + carriedPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".");
                        return new VanillaWeightCartResult
                        {
                            ItemsMoved = moved, CartFull = true, Message = done
                        };
                    }

                    activity(token.Account.Label + ": weight maintenance: transfer complete; resuming Autobattle with the shared verified ResumeHotkey routine.");
                    VerifyResume(token, input, cancelled, activity);
                    paused = false;
                    if (!supervisor.MinimizeWeightMaintenanceClient(token))
                        throw new InvalidOperationException("Autobattle movement was verified but the client could not be minimized.");
                    completed = true;
                    string message = token.Account.Label + ": cart maintenance completed; moved " + moved
                        + " transfer(s), Cart " + (cartFull ? "100% full" : cartSafetyStop ? "stopped safely at/above 95%" : "processed")
                        + ", autobattle movement verified, client minimized.";
                    VanillaDebugLog.Write("WEIGHT", "event=cart-complete trigger=" + trigger + " account='" + token.Account.Label
                        + "' accountId=" + token.AccountId + " pid=" + pid + " items=" + moved + " cartFull=" + cartFull
                        + " safetyStop=" + cartSafetyStop + ".");
                    supervisor.CompleteWeightMaintenance(token, false, message);
                    return new VanillaWeightCartResult
                    {
                        ItemsMoved = moved, CartFull = cartFull, StoppedForCartSafety = cartSafetyStop, Message = message
                    };
                }
                catch (OperationCanceledException ex)
                {
                    string detail = "Weight/cart maintenance cancelled by supervisor/settings/client ownership change: " + ex.Message;
                    activity(token.Account.Label + ": " + detail);
                    supervisor.MarkWeightMaintenanceCancelled(token, paused, detail);
                    VanillaDebugLog.Write("WEIGHT", "event=cart-cancelled trigger=" + trigger + " account='" + token.Account.Label
                        + "' accountId=" + token.AccountId + " pid=" + pid + " items=" + moved
                        + " autobattleMayBePaused=" + paused + " manualHold=" + paused + ".");
                    return new VanillaWeightCartResult
                    {
                        ItemsMoved = moved,
                        Deferred = !paused,
                        RequiresManualIntervention = paused,
                        CartFull = cartFull,
                        StoppedForCartSafety = cartSafetyStop,
                        Message = paused
                            ? token.Account.Label + ": cart maintenance was cancelled after Autobattle may have been paused; manual hold retained."
                            : token.Account.Label + ": cart maintenance cancelled before Autobattle was paused; it may retry when supervision is stable."
                    };
                }
                catch (VanillaCartManualException ex)
                {
                    manualHold = true;
                    activity(token.Account.Label + ": weight maintenance stopped safely; manual Cart hold required: " + ex.Message);
                    bool cancelledNow = cancelled();
                    VanillaDebugLog.Write("WEIGHT", "event=cart-manual-hold trigger=" + trigger + " account='" + token.Account.Label
                        + "' accountId=" + token.AccountId + " pid=" + pid + " items=" + moved + " reason='" + ex.Message
                        + "' cancellationRace=" + cancelledNow + ".");
                    if (cancelledNow)
                        supervisor.MarkWeightMaintenanceCancelled(token, true, ex.Message);
                    else
                        supervisor.CompleteWeightMaintenance(token, true, ex.Message);
                    return new VanillaWeightCartResult
                    {
                        ItemsMoved = moved, RequiresManualIntervention = true, CartFull = cartFull,
                        StoppedForCartSafety = cartSafetyStop, Message = token.Account.Label + ": " + ex.Message
                    };
                }
                catch (Exception ex)
                {
                    activity(token.Account.Label + ": weight maintenance failed closed: " + ex.Message);
                    bool cancelledNow = cancelled();
                    if (paused) manualHold = true;
                    VanillaDebugLog.Write("WEIGHT", "event=cart-failed trigger=" + trigger + " account='" + token.Account.Label
                        + "' accountId=" + token.AccountId + " pid=" + pid + " items=" + moved
                        + " manualHold=" + manualHold + " cancellationRace=" + cancelledNow + " reason='" + ex.Message + "'.");
                    string detail = manualHold
                        ? "Weight/cart maintenance stopped in an uncertain UI state; Autobattle remains OFF for manual inspection: " + ex.Message
                        : "Weight/cart maintenance aborted safely: " + ex.Message;
                    if (cancelledNow)
                        supervisor.MarkWeightMaintenanceCancelled(token, manualHold, detail);
                    else
                        supervisor.CompleteWeightMaintenance(token, manualHold, detail);
                    return new VanillaWeightCartResult
                    {
                        ItemsMoved = moved, RequiresManualIntervention = manualHold, CartFull = cartFull,
                        StoppedForCartSafety = cartSafetyStop,
                        Message = token.Account.Label + ": automatic cart maintenance " + (manualHold ? "needs manual attention: " : "aborted safely: ") + ex.Message
                    };
                }
                finally
                {
                    if (!completed && cancelled()) VanillaDebugLog.Write("WEIGHT", token.Account.Label + ": weight maintenance cancelled by ownership/supervisor change.");
                }
            }
        }

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
                Thread.Sleep(150);
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
            try
            {
                VanillaFleetClientInfo client = CurrentClient(pid);
                if (client == null || !client.WeightVerified || !client.CartWeightVerified
                    || !client.WeightPercent.HasValue || !client.CartWeightPercent.HasValue
                    || client.CartWeightPercent.Value < CartFullPercent
                    || client.WeightPercent.Value < FarmingDoneCarryPercent)
                {
                    supervisor.CompleteWeightMaintenance(token, false,
                        "Farming-complete stop cancelled because fresh weight conditions were no longer satisfied.");
                    return false;
                }

                using (var input = new VanillaForegroundInput(pid))
                {
                    input.CancellationRequested = () => supervisor.WeightMaintenanceCancelled(token);
                    report(token.Account.Label + ": Cart 100% and carried weight "
                        + client.WeightPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        + "%; stopping Autobattle with " + settings.AutobattleStopHotkeyText + ".");
                    input.Chord(settings.AutobattleStopCtrl, settings.AutobattleStopAlt,
                        settings.AutobattleStopShift, (Keys)settings.AutobattleStopKey);
                    paused = true;
                    Thread.Sleep(500);
                }

                string detail = token.Account.Label + ": farming complete — Cart 100% and carried weight "
                    + client.WeightPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + "%; Autobattle intentionally OFF.";
                supervisor.CompleteWeightFarmingDone(token, detail);
                VanillaDebugLog.Write("WEIGHT", "event=farming-done-stop account='" + token.Account.Label
                    + "' accountId=" + token.AccountId + " pid=" + pid + " cartPercent="
                    + client.CartWeightPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " carriedPercent=" + client.WeightPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".");
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
                if (paused)
                    supervisor.MarkWeightMaintenanceCancelled(token, true,
                        "Farming-complete stop failed after Autobattle may have been paused: " + ex.Message);
                else
                    supervisor.CompleteWeightMaintenance(token, false,
                        "Farming-complete stop failed before Autobattle was changed: " + ex.Message);
                VanillaDebugLog.Write("WEIGHT", "event=farming-done-stop-failed account='" + token.Account.Label
                    + "' accountId=" + token.AccountId + " pid=" + pid + " paused=" + paused
                    + " reason='" + ex.Message + "'.");
                report(token.Account.Label + ": farming-complete stop failed safely: " + ex.Message);
                return false;
            }
        }

        private static bool WaitForQuantityPrompt(VanillaForegroundInput input, Func<bool> cancelled, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                ThrowIfCancelled(cancelled);
                using (Bitmap frame = input.CaptureClientBitmap())
                    if (VanillaInventoryVision.HasQuantityPrompt(frame)) return true;
                Thread.Sleep(100);
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
                Thread.Sleep(150);
            }
            return false;
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
                Thread.Sleep(ToggleSettleMs);
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
                    Thread.Sleep(ToggleSettleMs);
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
                Thread.Sleep(250);
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
                    Thread.Sleep(CategorySettleMs);
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
            Thread.Sleep(CategorySettleMs);
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
                            input.DragNormalizedDeliberate(NormalizeX(sourcePoint.X, frame.Width), NormalizeY(sourcePoint.Y, frame.Height),
                                NormalizeX(destination.X, frame.Width), NormalizeY(destination.Y, frame.Height));
                            return true;
                        }
                    }
                    else
                    {
                        emptyStable = 0;
                        occupiedStable = 0;
                    }
                }
                Thread.Sleep(EmptyCategoryConfirmMs);
            }

            throw new VanillaCartManualException(categoryName
                + " first-slot occupancy stayed ambiguous across " + FirstSlotVerifySamples
                + " fresh captures. No drag was sent; Autobattle stays OFF for manual inspection.");
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
        private static void ThrowIfCancelled(Func<bool> cancelled) { if (cancelled()) throw new OperationCanceledException("Weight/cart maintenance cancelled."); }

        private sealed class VanillaCartManualException : Exception { internal VanillaCartManualException(string message) : base(message) { } }
    }

    internal sealed class VanillaUiSlotGrid
    {
        internal Rectangle Panel;
        internal int[] Columns;
        internal int[] Rows;
        internal int EmptyPaleThreshold;
    }

    internal enum VanillaInventorySlotState
    {
        Unknown,
        Empty,
        Occupied
    }

    internal sealed class VanillaInventoryFirstSlotObservation
    {
        internal VanillaInventorySlotState State;
        internal Point Center;
        internal Point ReferenceEmptyCenter;
        internal double PaleRatio;
        internal double TemplateDifference;
    }

    internal sealed class VanillaInventoryCategoryTabs
    {
        internal Rectangle[] Tabs;
        internal Rectangle RailBounds;
        internal int RightBorderX;
        internal int SelectedIndex;
        // Higher means the tab is structurally more "open" into the inventory body.
        internal double[] SelectionScores;
    }

    internal static class VanillaInventoryVision
    {
        private sealed class Component
        {
            internal Rectangle Bounds;
            internal int Area;
            internal Point Center;
        }

        private sealed class CategoryRuleLine
        {
            internal int Y;
            internal int Left;
            internal int Right;
            internal int Length { get { return Math.Max(0, Right - Left + 1); } }
        }

        internal static bool TryFindToggledPanel(Bitmap before, Bitmap after, out Rectangle panel, out bool opened)
        {
            panel = Rectangle.Empty; opened = false;
            Rectangle added = FindChangedLightPanel(after, before);
            Rectangle removed = FindChangedLightPanel(before, after);
            int addedScore = ScorePanel(after, added), removedScore = ScorePanel(before, removed);
            if (addedScore <= 0 && removedScore <= 0) return false;
            opened = addedScore >= removedScore;
            panel = opened ? added : removed;
            return !panel.IsEmpty;
        }

        internal static bool PanelStillPresent(Bitmap frame, Rectangle panel)
        {
            if (frame == null || panel.IsEmpty) return false;
            Rectangle clipped = Rectangle.Intersect(new Rectangle(Point.Empty, frame.Size), panel);
            if (clipped.Width < 100 || clipped.Height < 50) return false;
            PixelBuffer pixels = PixelBuffer.Read(frame);
            int light = 0, total = 0;
            int step = Math.Max(1, Math.Min(clipped.Width, clipped.Height) / 80);
            for (int y = clipped.Top; y < clipped.Bottom; y += step)
                for (int x = clipped.Left; x < clipped.Right; x += step) { total++; if (pixels.IsLight(x, y)) light++; }
            return total > 0 && light / (double)total > 0.35;
        }

        internal static VanillaUiSlotGrid DetectSlotGrid(Bitmap frame, Rectangle panel)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            Rectangle clipped = Rectangle.Intersect(new Rectangle(Point.Empty, frame.Size), panel);
            if (clipped.Width < 120 || clipped.Height < 60) throw new InvalidOperationException("Detected item panel is too small for slot recognition.");
            PixelBuffer pixels = PixelBuffer.Read(frame);
            List<Component> components = ConnectedComponents(pixels, clipped, p => p.Pale, 18, 55, 8, 30, 120, 1000);
            var rows = new List<List<Component>>();
            foreach (Component item in components.OrderBy(c => c.Center.Y))
            {
                List<Component> row = rows.FirstOrDefault(r => Math.Abs(r.Average(c => c.Center.Y) - item.Center.Y) <= 5);
                if (row == null) { row = new List<Component>(); rows.Add(row); }
                row.Add(item);
            }
            var useful = rows.Select(row => row.OrderBy(c => c.Center.X).ToList())
                .Where(row => LongestRegularRun(row.Select(c => c.Center.X).ToArray()).Length >= 4).ToList();
            if (useful.Count == 0) throw new InvalidOperationException("No regular Vanilla item-slot grid was detected inside the toggled panel.");

            int[] columns = useful.Select(row => LongestRegularRun(row.Select(c => c.Center.X).ToArray()))
                .OrderByDescending(run => run.Length).First();
            if (columns.Length < 4) throw new InvalidOperationException("Item-slot column lattice is incomplete.");
            int columnSpacing = MedianSpacing(columns);
            var rowCenters = useful.Select(row => (int)Math.Round(row.Average(c => c.Center.Y))).OrderBy(v => v).ToList();
            int rowSpacing = rowCenters.Count > 1 ? MedianSpacing(rowCenters.ToArray()) : columnSpacing;
            rowSpacing = Math.Max(24, Math.Min(60, rowSpacing));
            int first = rowCenters.First();
            while (first - rowSpacing - clipped.Top > rowSpacing * 2) first -= rowSpacing;
            int last = rowCenters.Last();
            while (clipped.Bottom - (last + rowSpacing) > rowSpacing) last += rowSpacing;
            var allRows = new List<int>();
            for (int y = first; y <= last; y += rowSpacing) allRows.Add(y);

            int emptyMedian = components.OrderBy(c => Math.Abs(c.Center.Y - rowCenters[0])).Take(Math.Max(1, columns.Length))
                .Select(c => pixels.PaleCount(c.Center.X, c.Center.Y, 18, 10)).OrderBy(v => v).ElementAt(Math.Max(0, Math.Min(columns.Length - 1, columns.Length / 2)));
            int threshold = Math.Max(240, (int)Math.Round(emptyMedian * 0.66));
            return new VanillaUiSlotGrid { Panel = clipped, Columns = columns, Rows = allRows.ToArray(), EmptyPaleThreshold = threshold };
        }

        internal static VanillaInventoryCategoryTabs DetectCategoryTabs(Bitmap frame, VanillaUiSlotGrid grid)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (grid == null || grid.Columns == null || grid.Columns.Length < 4)
                throw new InvalidOperationException("A detected item-slot grid is required before category-tab detection.");

            PixelBuffer pixels = PixelBuffer.Read(frame);
            int columnSpacing = MedianSpacing(grid.Columns);
            int rowSpacing = grid.Rows != null && grid.Rows.Length > 1 ? MedianSpacing(grid.Rows) : columnSpacing;
            int firstColumn = grid.Columns.Min();

            // The tab rail is not addressed by a percentage or screen coordinate. Its horizontal
            // search area is derived from the detected panel edge and detected first slot column.
            int railRight = firstColumn - Math.Max(6, (int)Math.Round(columnSpacing * 0.65));
            Rectangle railSearch = Rectangle.Intersect(grid.Panel,
                new Rectangle(grid.Panel.Left, grid.Panel.Top, Math.Max(0, railRight - grid.Panel.Left), grid.Panel.Height));
            railSearch.Intersect(new Rectangle(Point.Empty, frame.Size));
            if (railSearch.Width < 12 || railSearch.Height < 80)
                throw new InvalidOperationException("The inventory category rail could not be separated from the detected slot grid.");

            int minimumRun = Math.Max(8, (int)Math.Round(railSearch.Width * 0.45));
            var raw = new List<CategoryRuleLine>();
            for (int y = railSearch.Top; y < railSearch.Bottom; y++)
            {
                int left, right;
                if (LongestCategoryRuleRun(pixels, railSearch.Left, railSearch.Right, y, out left, out right) >= minimumRun)
                    raw.Add(new CategoryRuleLine { Y = y, Left = left, Right = right });
            }

            var rules = new List<CategoryRuleLine>();
            CategoryRuleLine best = null;
            int previousY = int.MinValue;
            foreach (CategoryRuleLine line in raw.OrderBy(item => item.Y))
            {
                if (best == null || line.Y > previousY + 1)
                {
                    if (best != null) rules.Add(best);
                    best = line;
                }
                else if (line.Length > best.Length) best = line;
                previousY = line.Y;
            }
            if (best != null) rules.Add(best);

            int lattice = Math.Max(1, Math.Min(columnSpacing, rowSpacing));
            double minStep = Math.Max(20.0, lattice * 1.15);
            double maxStep = Math.Max(minStep + 4.0, Math.Max(columnSpacing, rowSpacing) * 2.40);
            int bestHits = 0;
            double bestError = double.MaxValue;
            double bestOrigin = 0, bestStep = 0;
            int bestTolerance = 0;

            for (int i = 0; i < rules.Count; i++)
            for (int j = i + 1; j < rules.Count; j++)
            for (int divisor = 1; divisor <= 4; divisor++)
            {
                double step = (rules[j].Y - rules[i].Y) / (double)divisor;
                if (step < minStep || step > maxStep) continue;
                int tolerance = Math.Max(3, (int)Math.Round(step * 0.13));
                for (int indexAtFirst = 0; indexAtFirst + divisor <= 4; indexAtFirst++)
                {
                    double origin = rules[i].Y - indexAtFirst * step;
                    double end = origin + 4 * step;
                    if (origin < grid.Panel.Top - tolerance || end > grid.Panel.Bottom + tolerance) continue;

                    int hits = 0;
                    double error = 0;
                    for (int k = 0; k <= 4; k++)
                    {
                        double predicted = origin + k * step;
                        double nearest = rules.Count == 0 ? double.MaxValue : rules.Min(rule => Math.Abs(rule.Y - predicted));
                        if (nearest <= tolerance) { hits++; error += nearest; }
                    }
                    if (hits > bestHits || (hits == bestHits && error < bestError))
                    {
                        bestHits = hits;
                        bestError = error;
                        bestOrigin = origin;
                        bestStep = step;
                        bestTolerance = tolerance;
                    }
                }
            }

            if (bestHits < 3 || bestStep <= 0)
                throw new InvalidOperationException("The four-tab inventory rail was not positively detected from repeated separator structure.");

            int[] boundaries = Enumerable.Range(0, 5).Select(k => (int)Math.Round(bestOrigin + k * bestStep)).ToArray();
            for (int k = 1; k < boundaries.Length; k++)
                if (boundaries[k] - boundaries[k - 1] < Math.Max(12, (int)Math.Round(lattice * 0.75)))
                    throw new InvalidOperationException("Detected category-tab boundaries are incoherent.");

            // Vanilla's Fav tab is blue even while another category is active, so color is
            // not a selected-tab signal. The actual active tab is the one whose right edge is
            // open/merged into the inventory body; inactive tabs keep a vertical right border.
            // Recover both vertical rail borders from repeated grayscale rule pixels.
            var vertical = new List<Tuple<int, double>>();
            for (int x = railSearch.Left; x < railSearch.Right; x++)
            {
                double coverage = CategoryVerticalRuleCoverage(pixels, x, boundaries);
                if (coverage >= 0.38) vertical.Add(Tuple.Create(x, coverage));
            }
            if (vertical.Count < 2)
                throw new InvalidOperationException("Inventory category rail vertical borders were not positively detected.");

            int leftBorder = vertical.Min(item => item.Item1);
            int rightBorder = vertical.Max(item => item.Item1);
            if (rightBorder - leftBorder < 8)
                throw new InvalidOperationException("Detected inventory category rail is too narrow.");

            // Pick the strongest pixel column inside the rightmost border cluster. This stabilizes
            // anti-aliasing/DPI differences while preserving the structural open-edge signal.
            int clusterStart = rightBorder;
            while (clusterStart > leftBorder && vertical.Any(item => item.Item1 == clusterStart - 1))
                clusterStart--;
            int rightRuleX = vertical.Where(item => item.Item1 >= clusterStart)
                .OrderByDescending(item => item.Item2).ThenByDescending(item => item.Item1).First().Item1;

            int clickLeft = leftBorder + 1;
            int clickRight = rightRuleX - 1;
            if (clickRight - clickLeft < 6)
                throw new InvalidOperationException("Detected category rail has no safe interior click band.");

            var tabs = new Rectangle[4];
            var scores = new double[4];
            for (int k = 0; k < 4; k++)
            {
                int top = Math.Max(grid.Panel.Top, boundaries[k] + 1);
                int bottom = Math.Min(grid.Panel.Bottom, boundaries[k + 1] - 1);
                if (bottom <= top) throw new InvalidOperationException("Detected category tab has no usable area.");
                tabs[k] = Rectangle.FromLTRB(clickLeft, top, clickRight + 1, bottom);
                double closedFraction = CategoryRightBorderFraction(pixels, rightRuleX, top, bottom);
                scores[k] = Math.Max(0, Math.Min(1, 1.0 - closedFraction));
            }

            int selected = -1;
            int maxIndex = 0;
            for (int k = 1; k < scores.Length; k++) if (scores[k] > scores[maxIndex]) maxIndex = k;
            double second = scores.Where((value, index) => index != maxIndex).DefaultIfEmpty(0).Max();
            if (scores[maxIndex] >= 0.52 && scores[maxIndex] - second >= 0.16)
                selected = maxIndex;

            return new VanillaInventoryCategoryTabs
            {
                Tabs = tabs,
                RailBounds = Rectangle.FromLTRB(leftBorder, boundaries[0], rightRuleX + 1, boundaries[4]),
                RightBorderX = rightRuleX,
                SelectedIndex = selected,
                SelectionScores = scores
            };
        }

        internal static VanillaInventoryFirstSlotObservation ObserveFirstSlot(Bitmap frame, VanillaUiSlotGrid grid)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (grid == null || grid.Columns == null || grid.Columns.Length == 0 || grid.Rows == null || grid.Rows.Length == 0)
                throw new InvalidOperationException("Detected inventory slot grid is empty.");

            PixelBuffer pixels = PixelBuffer.Read(frame);
            int columnSpacing = grid.Columns.Length > 1 ? MedianSpacing(grid.Columns) : 41;
            int rowSpacing = grid.Rows.Length > 1 ? MedianSpacing(grid.Rows) : columnSpacing;
            int rx = Math.Max(10, Math.Min(28, (int)Math.Round(columnSpacing * 0.42)));
            int ry = Math.Max(6, Math.Min(18, (int)Math.Round(rowSpacing * 0.24)));

            Point first = new Point(grid.Columns[0], grid.Rows[0]);
            var candidates = new List<Tuple<Point, int>>();
            foreach (int y in grid.Rows)
            foreach (int x in grid.Columns)
            {
                if (x == first.X && y == first.Y) continue;
                if (!grid.Panel.Contains(x, y)) continue;
                int pale = pixels.PaleCount(x, y, rx, ry);
                candidates.Add(Tuple.Create(new Point(x, y), pale));
            }
            if (candidates.Count == 0)
                throw new InvalidOperationException("No reference slot is available for first-slot classification.");

            Tuple<Point, int> reference = candidates.OrderByDescending(item => item.Item2).First();
            int firstPale = pixels.PaleCount(first.X, first.Y, rx, ry);
            double paleRatio = firstPale / (double)Math.Max(1, reference.Item2);
            double difference = pixels.MeanPatchColorDistance(first, reference.Item1, rx, ry);

            VanillaInventorySlotState state = VanillaInventorySlotState.Unknown;
            if (paleRatio >= 0.80 && difference <= 24.0)
                state = VanillaInventorySlotState.Empty;
            else if (paleRatio <= 0.68 || difference >= 34.0)
                state = VanillaInventorySlotState.Occupied;

            return new VanillaInventoryFirstSlotObservation
            {
                State = state,
                Center = first,
                ReferenceEmptyCenter = reference.Item1,
                PaleRatio = paleRatio,
                TemplateDifference = difference
            };
        }

        internal static Point CartDropPoint(Rectangle cartPanel, int sequence)
        {
            if (cartPanel.Width < 40 || cartPanel.Height < 30)
                throw new InvalidOperationException("Detected Cart panel is too small for a safe interior drop.");

            // Vanilla accepts the dragged item anywhere in the Cart item body. Use only the
            // positively detected Cart rectangle and keep a generous inset from borders/tabs.
            // A deterministic rotation avoids depending on any particular Cart slot being empty
            // or even visually detectable once the Cart contains items.
            double[] xs = { 0.50, 0.35, 0.65, 0.25, 0.75, 0.50, 0.40, 0.60 };
            double[] ys = { 0.55, 0.55, 0.55, 0.55, 0.55, 0.35, 0.72, 0.72 };
            int index = Math.Abs(sequence) % xs.Length;
            int insetX = Math.Max(10, Math.Min(28, cartPanel.Width / 10));
            int insetY = Math.Max(8, Math.Min(20, cartPanel.Height / 7));
            int left = cartPanel.Left + insetX;
            int right = cartPanel.Right - insetX - 1;
            int top = cartPanel.Top + insetY;
            int bottom = cartPanel.Bottom - insetY - 1;
            if (right <= left || bottom <= top)
                throw new InvalidOperationException("Detected Cart panel has no safe interior drop area.");
            int x = left + (int)Math.Round((right - left) * xs[index]);
            int y = top + (int)Math.Round((bottom - top) * ys[index]);
            Point point = new Point(Math.Max(left, Math.Min(right, x)), Math.Max(top, Math.Min(bottom, y)));
            if (!cartPanel.Contains(point))
                throw new InvalidOperationException("Detected Cart drop point escaped the detected Cart panel.");
            return point;
        }

        internal static Point CartDropPoint(VanillaUiSlotGrid grid, int sequence)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            return CartDropPoint(grid.Panel, sequence);
        }

        internal static Point? FirstOccupiedSlot(Bitmap frame, VanillaUiSlotGrid grid)
        {
            PixelBuffer pixels = PixelBuffer.Read(frame);
            foreach (int y in grid.Rows)
                foreach (int x in grid.Columns)
                    if (grid.Panel.Contains(x, y) && pixels.PaleCount(x, y, 18, 10) < grid.EmptyPaleThreshold) return new Point(x, y);
            return null;
        }

        internal static Point? FirstOccupiedSlotNear(Bitmap frame, VanillaUiSlotGrid grid, Point previous)
        {
            PixelBuffer pixels = PixelBuffer.Read(frame);
            Point? nearest = null; double best = double.MaxValue;
            foreach (int y in grid.Rows)
                foreach (int x in grid.Columns)
                {
                    if (!grid.Panel.Contains(x, y) || pixels.PaleCount(x, y, 18, 10) >= grid.EmptyPaleThreshold) continue;
                    double distance = Math.Abs(x - previous.X) + Math.Abs(y - previous.Y);
                    if (distance < best) { best = distance; nearest = new Point(x, y); }
                }
            return best <= 14 ? nearest : null;
        }

        internal static bool HasQuantityPrompt(Bitmap frame)
        {
            if (frame == null) return false;
            PixelBuffer pixels = PixelBuffer.Read(frame);
            Rectangle whole = new Rectangle(Point.Empty, frame.Size);
            // The Vanilla quantity prompt is a short, wide white modal containing a focused
            // blue-selected numeric edit field. Basic Info, inventory/cart panes and chat also
            // contain white/blue pixels, so dimensions/aspect/fill are mandatory before Enter
            // can ever be authorized. Missing/ambiguous evidence always means NO Enter.
            List<Component> white = ConnectedComponents(pixels, whole, p => p.Light, 100, 440, 30, 95, 900, 60000);
            foreach (Component component in white)
            {
                Rectangle box = component.Bounds;
                double aspect = box.Width / (double)Math.Max(1, box.Height);
                double fill = component.Area / (double)Math.Max(1, box.Width * box.Height);
                if (aspect < 2.4 || aspect > 7.0 || fill < 0.45) continue;
                Rectangle leftBody = new Rectangle(box.Left, box.Top + box.Height / 4,
                    Math.Max(1, (int)Math.Round(box.Width * 0.68)), Math.Max(1, box.Height * 3 / 4));
                if (pixels.BlueSelectionCount(leftBody) >= 18) return true;
            }
            return false;
        }

        private static Rectangle FindChangedLightPanel(Bitmap primary, Bitmap secondary)
        {
            if (primary == null || secondary == null || primary.Size != secondary.Size) return Rectangle.Empty;
            PixelBuffer a = PixelBuffer.Read(primary), b = PixelBuffer.Read(secondary);
            int width = a.Width, height = a.Height;
            bool[] mask = new bool[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    mask[y * width + x] = a.IsLight(x, y) && a.ColorDistance(b, x, y) >= 32;
            List<Component> components = ConnectedComponents(a, new Rectangle(0, 0, width, height), mask, 80, 700, 40, 760, 1200, width * height);
            if (components.Count == 0) return Rectangle.Empty;
            Component best = components.OrderByDescending(c => c.Area).First();
            Rectangle r = best.Bounds;
            r.Inflate(8, 8);
            r.Intersect(new Rectangle(0, 0, width, height));
            return r;
        }

        private static int ScorePanel(Bitmap frame, Rectangle panel)
        {
            if (panel.IsEmpty || panel.Width < 120 || panel.Height < 50) return 0;
            try
            {
                VanillaUiSlotGrid grid = DetectSlotGrid(frame, panel);
                return panel.Width * panel.Height + grid.Columns.Length * grid.Rows.Length * 100;
            }
            catch { return 0; }
        }

        private static int[] LongestRegularRun(int[] values)
        {
            values = values.Distinct().OrderBy(v => v).ToArray();
            int[] best = new int[0];
            for (int i = 0; i < values.Length; i++)
            {
                var run = new List<int> { values[i] };
                int spacing = 0;
                for (int j = i + 1; j < values.Length; j++)
                {
                    int delta = values[j] - run[run.Count - 1];
                    if (run.Count == 1)
                    {
                        if (delta < 28 || delta > 58) continue;
                        spacing = delta; run.Add(values[j]);
                    }
                    else if (Math.Abs(delta - spacing) <= 5) run.Add(values[j]);
                    else if (delta > spacing + 6) break;
                }
                if (run.Count > best.Length) best = run.ToArray();
            }
            return best;
        }

        private static int MedianSpacing(int[] values)
        {
            if (values == null || values.Length < 2) return 41;
            int[] d = values.Skip(1).Select((value, index) => value - values[index]).Where(v => v > 0).OrderBy(v => v).ToArray();
            return d.Length == 0 ? 41 : d[d.Length / 2];
        }

        private static int LongestCategoryRuleRun(PixelBuffer pixels, int left, int right, int y, out int bestLeft, out int bestRight)
        {
            bestLeft = left; bestRight = left - 1;
            int currentLeft = left;
            bool inRun = false;
            for (int x = left; x < right; x++)
            {
                bool rule = pixels.At(x, y).CategoryRule;
                if (rule && !inRun) { currentLeft = x; inRun = true; }
                bool end = inRun && (!rule || x == right - 1);
                if (!end) continue;
                int currentRight = rule && x == right - 1 ? x : x - 1;
                if (currentRight - currentLeft > bestRight - bestLeft)
                {
                    bestLeft = currentLeft;
                    bestRight = currentRight;
                }
                inRun = false;
            }
            return Math.Max(0, bestRight - bestLeft + 1);
        }

        private static double CategoryVerticalRuleCoverage(PixelBuffer pixels, int x, int[] boundaries)
        {
            int rule = 0, total = 0;
            for (int k = 0; k < 4; k++)
            {
                int top = boundaries[k] + 4;
                int bottom = boundaries[k + 1] - 4;
                for (int y = top; y <= bottom; y++)
                {
                    if (x < 0 || x >= pixels.Width || y < 0 || y >= pixels.Height) continue;
                    total++;
                    if (pixels.At(x, y).CategoryRule) rule++;
                }
            }
            return total == 0 ? 0 : rule / (double)total;
        }

        private static double CategoryRightBorderFraction(PixelBuffer pixels, int x, int top, int bottom)
        {
            int rule = 0, total = 0;
            int innerTop = top + Math.Max(2, (bottom - top) / 12);
            int innerBottom = bottom - Math.Max(2, (bottom - top) / 12);
            for (int y = innerTop; y <= innerBottom; y++)
            {
                bool hit = false;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sx = x + dx;
                    if (sx >= 0 && sx < pixels.Width && y >= 0 && y < pixels.Height
                        && pixels.At(sx, y).CategoryRule)
                    {
                        hit = true;
                        break;
                    }
                }
                total++;
                if (hit) rule++;
            }
            return total == 0 ? 1 : rule / (double)total;
        }

        private static List<Component> ConnectedComponents(PixelBuffer pixels, Rectangle area, Func<PixelInfo, bool> predicate,
            int minWidth, int maxWidth, int minHeight, int maxHeight, int minArea, int maxArea)
        {
            bool[] mask = new bool[pixels.Width * pixels.Height];
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++) mask[y * pixels.Width + x] = predicate(pixels.At(x, y));
            return ConnectedComponents(pixels, area, mask, minWidth, maxWidth, minHeight, maxHeight, minArea, maxArea);
        }

        private static List<Component> ConnectedComponents(PixelBuffer pixels, Rectangle area, bool[] mask,
            int minWidth, int maxWidth, int minHeight, int maxHeight, int minArea, int maxArea)
        {
            var result = new List<Component>();
            int width = pixels.Width;
            int[] queue = new int[Math.Max(1, area.Width * area.Height)];
            for (int y0 = area.Top; y0 < area.Bottom; y0++)
            for (int x0 = area.Left; x0 < area.Right; x0++)
            {
                int start = y0 * width + x0;
                if (!mask[start]) continue;
                int head = 0, tail = 0; queue[tail++] = start; mask[start] = false;
                int minX = x0, maxX = x0, minY = y0, maxY = y0, count = 0;
                while (head < tail)
                {
                    int index = queue[head++], y = index / width, x = index - y * width; count++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < area.Left || nx >= area.Right || ny < area.Top || ny >= area.Bottom) continue;
                        int ni = ny * width + nx;
                        if (!mask[ni]) continue;
                        mask[ni] = false; queue[tail++] = ni;
                    }
                }
                int w = maxX - minX + 1, h = maxY - minY + 1;
                if (w >= minWidth && w <= maxWidth && h >= minHeight && h <= maxHeight && count >= minArea && count <= maxArea)
                    result.Add(new Component { Bounds = new Rectangle(minX, minY, w, h), Area = count, Center = new Point((minX + maxX) / 2, (minY + maxY) / 2) });
            }
            return result;
        }

        internal struct PixelInfo
        {
            internal byte R, G, B;
            internal bool Light { get { return R >= 235 && G >= 235 && B >= 235; } }
            internal bool CategoryRule
            {
                get
                {
                    int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                    int mean = (R + G + B) / 3;
                    return max - min <= 18 && mean >= 115 && mean <= 245;
                }
            }
            internal bool Pale
            {
                get
                {
                    int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                    return R > 180 && G > 185 && B > 190 && B - R >= 5 && max - min <= 48;
                }
            }
        }

        internal sealed class PixelBuffer
        {
            private readonly byte[] data;
            internal int Width { get; private set; }
            internal int Height { get; private set; }
            private PixelBuffer(int width, int height, byte[] data) { Width = width; Height = height; this.data = data; }
            internal static PixelBuffer Read(Bitmap source)
            {
                using (var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(copy)) g.DrawImageUnscaled(source, 0, 0);
                    Rectangle rect = new Rectangle(0, 0, copy.Width, copy.Height);
                    BitmapData bits = copy.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        int stride = bits.Stride, row = copy.Width * 3;
                        byte[] raw = new byte[row * copy.Height];
                        byte[] scan = new byte[Math.Abs(stride) * copy.Height];
                        Marshal.Copy(bits.Scan0, scan, 0, scan.Length);
                        for (int y = 0; y < copy.Height; y++) Buffer.BlockCopy(scan, y * Math.Abs(stride), raw, y * row, row);
                        return new PixelBuffer(copy.Width, copy.Height, raw);
                    }
                    finally { copy.UnlockBits(bits); }
                }
            }
            internal PixelInfo At(int x, int y)
            {
                int index = (y * Width + x) * 3;
                return new PixelInfo { B = data[index], G = data[index + 1], R = data[index + 2] };
            }
            internal bool IsLight(int x, int y) { return At(x, y).Light; }
            internal int ColorDistance(PixelBuffer other, int x, int y)
            {
                PixelInfo a = At(x, y), b = other.At(x, y);
                return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            }
            internal int PaleCount(int cx, int cy, int rx, int ry)
            {
                int count = 0;
                for (int y = Math.Max(0, cy - ry); y <= Math.Min(Height - 1, cy + ry); y++)
                    for (int x = Math.Max(0, cx - rx); x <= Math.Min(Width - 1, cx + rx); x++) if (At(x, y).Pale) count++;
                return count;
            }

            internal double MeanPatchColorDistance(Point a, Point b, int rx, int ry)
            {
                long total = 0;
                int count = 0;
                for (int dy = -ry; dy <= ry; dy++)
                for (int dx = -rx; dx <= rx; dx++)
                {
                    int ax = a.X + dx, ay = a.Y + dy, bx = b.X + dx, by = b.Y + dy;
                    if (ax < 0 || ax >= Width || ay < 0 || ay >= Height
                        || bx < 0 || bx >= Width || by < 0 || by >= Height) continue;
                    PixelInfo pa = At(ax, ay), pb = At(bx, by);
                    total += Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                    count++;
                }
                return count == 0 ? double.MaxValue : total / (double)(count * 3);
            }
            internal int BlueSelectionCount(Rectangle box)
            {
                int count = 0;
                Rectangle clipped = Rectangle.Intersect(new Rectangle(0, 0, Width, Height), box);
                for (int y = clipped.Top; y < clipped.Bottom; y++)
                    for (int x = clipped.Left; x < clipped.Right; x++)
                    {
                        PixelInfo p = At(x, y);
                        if (p.B >= 180 && p.B - p.R >= 60 && p.B - p.G >= 30) count++;
                    }
                return count;
            }
        }
    }
}
