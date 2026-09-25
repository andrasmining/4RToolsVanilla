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
        private const int ToggleSettleMs = 500;
        private const int CategorySettleMs = 140;
        private const int CategoryVerifyTimeoutMs = 1800;
        private const int CategoryClickAttempts = 3;
        private const int EmptyCategoryConfirmMs = 180;
        private const int FirstSlotVerifySamples = 4;
        internal const int TransferAttemptLimit = 3;
        internal const int TransferSettleMs = 700;
        internal const int QuantityPromptTimeoutMs = 3000;
        internal const int CartProgressTimeoutMs = 4000;
        internal const int TransferRetryPauseMs = 1000;
        private const int MaxTransfers = 120;
        internal const decimal PrecisionThresholdPercent = 75m;
        internal const decimal CartFullPercent = 99m; // Maintenance-ready threshold, not physical capacity.
        internal const decimal FarmingDoneCartPercent = 99m;
        internal const decimal FarmingDoneCarryPercent = 50m;
        internal const decimal HpDamageAbortPercent = 10m;
        internal const int TransientCartRetrySeconds = 60;
        private readonly VanillaFleetMonitor fleet;
        private readonly VanillaReconnectSupervisor supervisor;

        internal VanillaWeightCartAutomation(VanillaFleetMonitor fleet, VanillaReconnectSupervisor supervisor)
        {
            this.fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        }

        internal static uint? KnownItemUnitWeightForCategory(int category)
        {
            // A tab is not an item identity. No production transfer may infer unit
            // weight from Use/Equip/Etc; quantity safety uses the observed stack.
            return null;
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

        internal static bool IsFarmingComplete(decimal cartPercent, decimal carriedPercent)
        {
            return cartPercent >= FarmingDoneCartPercent && carriedPercent >= FarmingDoneCarryPercent;
        }

        internal static bool HpDamageExceeded(decimal stoppedBaselinePercent, decimal currentPercent)
        {
            return stoppedBaselinePercent - currentPercent > HpDamageAbortPercent;
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
                string detail = token.Account.Label + ": Cart is at the 99% maintenance threshold: " + initialCart.Current + "/" + initialCart.Maximum
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
            bool cartFull = false, cartSafetyStop = false, retryLater = false;
            string retryLaterReason = null;
            Rectangle inventory = Rectangle.Empty, cart = Rectangle.Empty;
            Rectangle[] quantityBeforeDrag = new Rectangle[0];
            Rectangle knownQuantityDialog = Rectangle.Empty;
            int moved = 0;
            decimal? stoppedHpBaselinePercent = null;
            bool hpDanger = false;
            string hpDangerReason = null;
            Func<bool> supervisorCancelled = () => supervisor.WeightMaintenanceCancelled(token);
            Func<bool> cancelled = () =>
            {
                if (supervisorCancelled()) return true;
                if (hpDanger) return true;
                if (!stoppedHpBaselinePercent.HasValue) return false;

                VanillaFleetClientInfo hpClient = CurrentClient(token.ProcessId);
                if (supervisorCancelled()) return true;
                if (hpClient == null || !hpClient.HpVerified || !hpClient.HpPercent.HasValue
                    || hpClient.Snapshot == null || hpClient.Snapshot.SampledAtUtc > DateTimeOffset.UtcNow
                    || DateTimeOffset.UtcNow - hpClient.Snapshot.SampledAtUtc > TimeSpan.FromSeconds(3))
                {
                    hpDanger = true;
                    hpDangerReason = "Fresh verified HP became unavailable after Autobattle STOP; Cart work is aborted.";
                    return true;
                }

                decimal currentHp = hpClient.HpPercent.Value;
                if (HpDamageExceeded(stoppedHpBaselinePercent.Value, currentHp))
                {
                    hpDanger = true;
                    hpDangerReason = "HP fell from "
                        + stoppedHpBaselinePercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        + "% to " + currentHp.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        + "% after Autobattle STOP (> " + HpDamageAbortPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                        + " percentage-point drop).";
                    return true;
                }
                return false;
            };
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
                        + settings.AutobattleStopHotkeyText + " and verifying continuous X/Y stillness before any panel input.");
                    bool stopHpDamage;
                    decimal? stopHpBaseline;
                    bool stopVerified = VerifyAutobattleStopped(token, input, settings, supervisorCancelled, activity,
                        () => paused = true, "cart-start", out stopHpDamage, out stopHpBaseline);
                    if (!stopVerified)
                    {
                        if (stopHpDamage)
                        {
                            hpDanger = true;
                            hpDangerReason = "HP dropped by more than "
                                + HpDamageAbortPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                                + " percentage points while verifying Autobattle STOP.";
                            throw new OperationCanceledException(hpDangerReason);
                        }

                        string detail = token.Account.Label + ": Autobattle STOP was not verified after "
                            + VanillaAutobattleStopVerifier.MaximumAttempts
                            + " attempts; Inventory/Cart were NOT opened. Recovering paused state before retrying Cart maintenance in about "
                            + TransientCartRetrySeconds + "s.";
                        activity(detail);
                        VanillaDebugLog.Write("WEIGHT", "event=cart-stop-unverified trigger=" + trigger
                            + " account='" + token.Account.Label + "' accountId=" + token.AccountId + " pid=" + pid
                            + " attempts=" + VanillaAutobattleStopVerifier.MaximumAttempts + ".");
                        completed = true;
                        return RecoverPaused(token, input, settings, inventory, cart, paused, moved, cartFull,
                            cartSafetyStop, supervisorCancelled, activity, detail);
                    }
                    paused = true;

                    VanillaFleetClientInfo stoppedHp = CurrentClient(token.ProcessId);
                    if (stoppedHp == null || !stoppedHp.HpVerified || !stoppedHp.HpPercent.HasValue)
                    {
                        hpDanger = true;
                        hpDangerReason = "Fresh verified HP is unavailable immediately after the verified Autobattle STOP.";
                        throw new OperationCanceledException(hpDangerReason);
                    }
                    stoppedHpBaselinePercent = stopHpBaseline ?? stoppedHp.HpPercent.Value;
                    activity(token.Account.Label + ": weight maintenance: HP damage guard armed at "
                        + stoppedHpBaselinePercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        + "%; any drop greater than " + HpDamageAbortPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                        + " percentage points aborts Cart work and resumes Autobattle.");
                    input.CancellationRequested = cancelled;
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
                        activity(token.Account.Label + ": weight maintenance: detecting and selecting " + categoryName + " tab; no item identity inferred from the tab.");
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
                                activity(token.Account.Label + ": weight maintenance: Cart reached the 99% maintenance threshold at " + cartBefore.Current + "/"
                                    + cartBefore.Maximum + "; no further drag will be sent.");
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
                                        + "ms, then re-checking Cart weight and re-detecting the first slot before retry "
                                        + transferAttempt + "/" + TransferAttemptLimit + ".");
                                    WaitWithCancellation(TransferRetryPauseMs, cancelled);

                                    // Progress can arrive during the retry pause on a laggy client.
                                    // Re-check after the pause as well as before it, otherwise a
                                    // delayed success could make us drag the next compacted stack.
                                    lateProgress = CurrentCartWeight(token.ProcessId);
                                    if (lateProgress != null && lateProgress.Current > cartBefore.Current)
                                    {
                                        cartAfter = lateProgress;
                                        transferSucceeded = true;
                                        activity(token.Account.Label + ": weight maintenance: delayed Cart-weight progress appeared during the retry pause; "
                                            + "previous drag succeeded, so retry " + transferAttempt + "/" + TransferAttemptLimit + " is suppressed.");
                                        break;
                                    }
                                }

                                uint? pendingWeightBefore;
                                Point sourcePoint;
                                knownQuantityDialog = Rectangle.Empty;
                                if (!TryDragNextDetectedItem(token, input, inventory, cart, categoryName,
                                    moved * TransferAttemptLimit + transferAttempt - 1, cancelled, activity,
                                    out sourcePoint, out pendingWeightBefore, out quantityBeforeDrag))
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

                                    retryLater = true;
                                    retryLaterReason = categoryName
                                        + " first slot changed after a drag but Cart weight has not caught up. "
                                        + "Treating this as a transient client/RDP delay; no more Cart input this pass.";
                                    activity(token.Account.Label + ": weight maintenance: " + retryLaterReason
                                        + " Autobattle will resume and Cart maintenance will retry in "
                                        + TransientCartRetrySeconds + "s.");
                                    break;
                                }

                                activity(token.Account.Label + ": weight maintenance: slow drag attempt " + transferAttempt + "/"
                                    + TransferAttemptLimit + " sent; allowing the client to settle before checking quantity/progress.");
                                WaitWithCancellation(TransferSettleMs, cancelled);

                                quantity = WaitForQuantityPrompt(input, cancelled, QuantityPromptTimeoutMs, out knownQuantityDialog);
                                requestedQuantity = null;
                                if (quantity)
                                {
                                    VanillaFleetClientInfo capacity = QuantityWeights(token, cartBefore.Maximum);
                                    if (VanillaCartQuantity.CanAcceptWholeInventory(capacity.CurrentWeight.Value,
                                        capacity.CurrentCartWeight.Value, capacity.MaxCartWeight.Value))
                                    {
                                        activity(token.Account.Label + ": quantity dialog detected; all carried weight "
                                            + capacity.CurrentWeight + " fits in free Cart capacity "
                                            + (capacity.MaxCartWeight.Value - capacity.CurrentCartWeight.Value)
                                            + "; confirming the untouched quantity once, then verifying Cart-weight progress.");
                                        VanillaCartQuantity.ConfirmDefault(input, () =>
                                        {
                                            VanillaFleetClientInfo fresh = QuantityWeights(token, cartBefore.Maximum);
                                            return VanillaCartQuantity.CanAcceptWholeInventory(fresh.CurrentWeight.Value,
                                                fresh.CurrentCartWeight.Value, fresh.MaxCartWeight.Value);
                                        }, quantityBeforeDrag);
                                    }
                                    else
                                    {
                                        VanillaQuantityObservation offered = VanillaCartQuantity.ReadStable(input, quantityBeforeDrag);
                                        capacity = QuantityWeights(token, cartBefore.Maximum);
                                        uint fit = VanillaCartQuantity.ConservativeAmount(capacity.CurrentWeight.Value, offered.Amount,
                                            capacity.CurrentCartWeight.Value, capacity.MaxCartWeight.Value);
                                        if (fit == 0)
                                        {
                                            if (!VanillaCartQuantity.CancelKnownPrompt(input, quantityBeforeDrag, knownQuantityDialog))
                                                throw new InvalidOperationException("Capacity-limited quantity dialog could not be cancelled safely.");
                                            cartSafetyStop = true; abortForCapacity = true;
                                            activity(token.Account.Label + ": remaining Cart capacity cannot safely accept this observed stack; category skipped without submitting a quantity.");
                                            break;
                                        }
                                        requestedQuantity = fit;
                                        activity(token.Account.Label + ": verified offered stack=" + offered.Amount
                                            + "; conservative capacity-safe quantity=" + fit + "; verifying field focus and typed readback before Enter.");
                                        VanillaCartQuantity.Submit(input, offered, fit, () =>
                                        {
                                            VanillaFleetClientInfo fresh = QuantityWeights(token, cartBefore.Maximum);
                                            return VanillaCartQuantity.ConservativeAmount(fresh.CurrentWeight.Value, offered.Amount,
                                                fresh.CurrentCartWeight.Value, fresh.MaxCartWeight.Value) >= fit;
                                        });
                                    }
                                    WaitWithCancellation(TransferSettleMs, cancelled);
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
                                        throw new VanillaCartManualException("A quantity dialog remained or appeared late. No unverified confirmation was sent; clearing the known prompt before paused-state recovery.");
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

                                    retryLater = true;
                                    retryLaterReason = "Cart did not show a verified weight increase after "
                                        + TransferAttemptLimit + " slow drag attempts. This is treated as transient lag, not a terminal failure.";
                                    activity(token.Account.Label + ": weight maintenance: " + retryLaterReason
                                        + " Autobattle will resume and Cart maintenance will retry in "
                                        + TransientCartRetrySeconds + "s.");
                                    break;
                                }

                                weightReduced = WaitForWeightReduction(token.ProcessId, pendingWeightBefore, cancelled);
                                transferSucceeded = true;
                                break;
                            }

                            if (abortForCapacity || retryLater) break;
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
                                retryLater = true;
                                retryLaterReason = retryLaterReason
                                    ?? "Transfer retry sequence ended without verified Cart progress.";
                                activity(token.Account.Label + ": weight maintenance: " + retryLaterReason
                                    + " Autobattle will resume and Cart maintenance will retry in "
                                    + TransientCartRetrySeconds + "s.");
                                break;
                            }

                            if (!weightReduced)
                            {
                                activity(token.Account.Label + ": weight maintenance: Cart weight increased from "
                                    + cartBefore.Current + " to " + cartAfter.Current
                                    + "; carried-weight reduction was not independently observed, so Cart weight remains the transfer proof.");
                            }

                            if (cartAfter.Maximum != cartBefore.Maximum || cartAfter.Current > cartBefore.Maximum)
                                throw new InvalidOperationException("Cart progress contradicts its verified capacity; no further transfer is safe.");
                            moved++;
                            categoryMoved++;
                            activity(token.Account.Label + ": weight maintenance: moved " + categoryName + " transfer "
                                + categoryMoved + " (total " + moved + "); Cart " + cartAfter.Current + "/" + cartAfter.Maximum
                                + " (" + cartAfter.Percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%).");

                            if (cartAfter.Percent >= CartFullPercent)
                            {
                                cartFull = true;
                                activity(token.Account.Label + ": weight maintenance: Cart reached the 99% maintenance threshold; stopping transfers and leaving remaining inventory on the character.");
                                break;
                            }
                        }
                        if (moved >= MaxTransfers)
                        {
                            manualHold = true;
                            throw new VanillaCartManualException("Transfer safety limit reached; aborting this pass and recovering paused state.");
                        }
                        if (retryLater) break;
                    }

                    if (cartFull)
                        activity(token.Account.Label + ": weight maintenance: Cart is at or above 99%; remaining inventory stays on the character.");
                    else if (retryLater)
                        activity(token.Account.Label + ": weight maintenance: temporary Cart transfer deferral; closing panels and resuming Autobattle before the scheduled retry.");
                    else if (cartSafetyStop)
                        activity(token.Account.Label + ": weight maintenance: stopped Cart filling at the verified capacity limit; no unverified quantity will be submitted.");
                    else
                        activity(token.Account.Label + ": weight maintenance: every enabled inventory category is confirmed complete.");

                    ClosePanelIfOpen(input, settings.CartCtrl, settings.CartAlt, settings.CartShift, (Keys)settings.CartKey, cart, "Cart", cancelled);
                    ClosePanelIfOpen(input, settings.InventoryCtrl, settings.InventoryAlt, settings.InventoryShift, (Keys)settings.InventoryKey, inventory, "Inventory", cancelled);

                    VanillaFleetClientInfo afterUi = CurrentClient(token.ProcessId);
                    decimal? carriedPercent = afterUi == null ? null : afterUi.WeightPercent;
                    decimal? finalCartPercent = afterUi == null ? null : afterUi.CartWeightPercent;
                    if (carriedPercent.HasValue && finalCartPercent.HasValue
                        && IsFarmingComplete(finalCartPercent.Value, carriedPercent.Value))
                    {
                        string done = token.Account.Label + ": farming complete immediately after Cart maintenance: Cart "
                            + finalCartPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                            + "% (>= " + FarmingDoneCartPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                            + "%) and carried weight "
                            + carriedPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                            + "% (>= " + FarmingDoneCarryPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                            + "%); Autobattle remains OFF.";
                        activity(done);
                        supervisor.CompleteWeightFarmingDone(token, done);
                        paused = false;
                        completed = true;
                        VanillaDebugLog.Write("WEIGHT", "event=farming-done trigger=" + trigger + " account='" + token.Account.Label
                            + "' accountId=" + token.AccountId + " pid=" + pid + " cartPercent="
                            + finalCartPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                            + " carriedPercent=" + carriedPercent.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".");
                        return new VanillaWeightCartResult
                        {
                            ItemsMoved = moved,
                            CartFull = finalCartPercent.Value >= CartFullPercent,
                            Message = done
                        };
                    }

                    activity(token.Account.Label + ": weight maintenance: transfer complete; resuming Autobattle with the shared verified ResumeHotkey routine.");
                    VerifyResume(token, input, cancelled, activity);
                    paused = false;
                    if (!supervisor.MinimizeWeightMaintenanceClient(token))
                        throw new InvalidOperationException("Autobattle movement was verified but the client could not be minimized.");
                    completed = true;
                    string message = token.Account.Label + ": cart maintenance completed; moved " + moved
                        + " transfer(s), Cart " + (cartFull ? "at/above 99%" : retryLater ? "temporarily deferred"
                            : cartSafetyStop ? "stopped at verified capacity limit" : "processed")
                        + ", autobattle movement verified, client minimized."
                        + (retryLater ? " Retry scheduled in about " + TransientCartRetrySeconds + "s." : "");
                    VanillaDebugLog.Write("WEIGHT", "event=cart-complete trigger=" + trigger + " account='" + token.Account.Label
                        + "' accountId=" + token.AccountId + " pid=" + pid + " items=" + moved + " cartFull=" + cartFull
                        + " safetyStop=" + cartSafetyStop + " retryLater=" + retryLater + ".");
                    supervisor.CompleteWeightMaintenance(token, false, message);
                    return new VanillaWeightCartResult
                    {
                        ItemsMoved = moved,
                        CartFull = cartFull,
                        StoppedForCartSafety = cartSafetyStop,
                        RetryLater = retryLater,
                        RetryAfterSeconds = retryLater ? TransientCartRetrySeconds : 0,
                        Message = message
                    };
                }
                catch (OperationCanceledException ex)
                {
                    if (hpDanger)
                    {
                        string danger = hpDangerReason ?? "HP safety guard triggered after Autobattle STOP.";
                        activity(token.Account.Label + ": weight maintenance: " + danger
                            + " Resuming Autobattle immediately; Cart maintenance will retry in about "
                            + TransientCartRetrySeconds + "s.");
                        VanillaDebugLog.Write("WEIGHT", "event=cart-hp-danger trigger=" + trigger
                            + " account='" + token.Account.Label + "' accountId=" + token.AccountId + " pid=" + pid
                            + " items=" + moved + " reason='" + danger + "'.");

                        completed = true;
                        return RecoverPaused(token, input, settings, inventory, cart, paused, moved, cartFull,
                            cartSafetyStop, supervisorCancelled, activity, danger, quantityBeforeDrag, knownQuantityDialog);
                    }

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
                catch (Exception ex)
                {
                    string detail = "Cart maintenance aborted: " + ex.Message;
                    activity(token.Account.Label + ": " + detail);
                    if (supervisorCancelled())
                    {
                        supervisor.MarkWeightMaintenanceCancelled(token, paused, detail);
                        return new VanillaWeightCartResult { ItemsMoved = moved, RequiresManualIntervention = paused,
                            Deferred = !paused, Message = detail + "; ownership changed, no resume input sent." };
                    }
                    completed = true;
                    return RecoverPaused(token, input, settings, inventory, cart, paused, moved, cartFull,
                        cartSafetyStop, supervisorCancelled, activity, detail, quantityBeforeDrag, knownQuantityDialog);
                }
                finally
                {
                    if (!completed && cancelled()) VanillaDebugLog.Write("WEIGHT", token.Account.Label + ": weight maintenance cancelled by ownership/supervisor change.");
                }
            }
        }

        private VanillaWeightCartResult RecoverPaused(VanillaWeightMaintenanceToken token, VanillaForegroundInput input,
            VanillaWeightAlertSettings settings, Rectangle inventory, Rectangle cart, bool paused, int moved,
            bool cartFull, bool safetyStop, Func<bool> cancelled, System.Action<string> report, string reason,
            Rectangle[] quantityBeforeDrag = null, Rectangle knownQuantityDialog = default(Rectangle))
        {
            try
            {
                // Refresh the emergency evidence before the ordinary resume/retry path.
                CurrentClient(token.ProcessId);
                input.CancellationRequested = cancelled;
                ThrowIfCancelled(cancelled);
                // Never send resume into a surviving modal edit field. Escape is only
                // allowed after positive modal recognition; unknown focus escalates to
                // the supervisor's identity-bound restart instead of blind input.
                bool neededResume = paused;
                if (paused)
                {
                    if (!VanillaCartQuantity.CancelKnownPrompt(input, quantityBeforeDrag, knownQuantityDialog))
                        throw new InvalidOperationException("Quantity dialog could not be safely cleared before resume.");
                    VerifyResume(token, input, cancelled, report);
                    paused = false;
                }
                try
                {
                    if (!cart.IsEmpty) ClosePanelIfOpen(input, settings.CartCtrl, settings.CartAlt, settings.CartShift,
                        (Keys)settings.CartKey, cart, "Cart", cancelled);
                    if (!inventory.IsEmpty) ClosePanelIfOpen(input, settings.InventoryCtrl, settings.InventoryAlt, settings.InventoryShift,
                        (Keys)settings.InventoryKey, inventory, "Inventory", cancelled);
                }
                catch (Exception cleanup) { VanillaDebugLog.Write("WEIGHT", "Post-resume cleanup: " + cleanup.Message); }
                ThrowIfCancelled(cancelled);
                supervisor.MinimizeWeightMaintenanceClient(token);
                string detail = reason + (neededResume ? "; Autobattle movement verified" : "; no STOP was sent")
                    + "; Cart retry in about " + TransientCartRetrySeconds + "s.";
                supervisor.CompleteWeightMaintenance(token, false, detail);
                report(token.Account.Label + ": " + detail);
                return new VanillaWeightCartResult { ItemsMoved = moved, CartFull = cartFull, StoppedForCartSafety = safetyStop,
                    RetryLater = true, RetryAfterSeconds = TransientCartRetrySeconds, Message = detail };
            }
            catch (Exception failure)
            {
                string detail = reason + "; paused-state recovery failed: " + failure.Message;
                bool changed = cancelled();
                bool restarted = false;
                if (!changed)
                {
                    try { restarted = supervisor.TryRestartAfterCartFailure(token, detail); }
                    catch (Exception restartFailure) { detail += "; supervised restart could not be queued: " + restartFailure.Message; }
                }
                if (restarted)
                {
                    report(token.Account.Label + ": " + detail + "; owned client restart queued, healthy siblings unchanged.");
                    return new VanillaWeightCartResult { ItemsMoved = moved, RetryLater = true,
                        RetryAfterSeconds = TransientCartRetrySeconds, Message = detail + "; supervised restart queued." };
                }
                if (changed) supervisor.MarkWeightMaintenanceCancelled(token, paused, detail);
                else supervisor.CompleteWeightMaintenance(token, paused, detail);
                return new VanillaWeightCartResult { ItemsMoved = moved, RequiresManualIntervention = paused,
                    Deferred = !paused, Message = detail + "; no unverified input or client close sent." };
            }
        }

    }
}
