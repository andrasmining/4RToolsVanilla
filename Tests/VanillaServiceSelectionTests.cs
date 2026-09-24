using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaServiceSelectionTests
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            Action<string, Action> test = (name, action) =>
            {
                try { action(); passed++; Console.WriteLine("PASS " + name); }
                catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
            };
            test("Legacy numeric proxy choices retain their service names", LegacyProxyValues);
            test("Default service confirmation does not require a saved proxy preference", MissingProxyPreference);
            test("Default confirmation activates, settles and sends Enter exactly once", DefaultConfirmation);
            test("Activation failure prevents Enter without retry", ActivationFailure);
            test("STOP before activation performs no window action", CancelBeforeActivation);
            test("STOP during activation prevents both settling and Enter", CancelDuringActivation);
            test("STOP during settling prevents further wait and Enter", CancelDuringSettle);
            test("STOP at the final settle boundary prevents Enter", CancelAtSubmitBoundary);
            test("STOP after activation blocks Enter even with zero settling", CancelZeroSettle);
            test("Enter failure propagates without an automatic retry", EnterFailure);
            test("Settle failure prevents Enter", SettleFailure);
            test("Custom settling uses bounded slices and preserves its full duration", CustomSettle);
            test("Zero and maximum supported settling each send one Enter", SettleBounds);
            test("Invalid settling is rejected before any window action", InvalidSettle);
            test("Known outage is checked after settling and before Enter", CheckOutageBeforeEnter);
            test("Confirmed server outage prevents Enter and is not retried", ConfirmedOutage);
            test("Cancellation during outage observation prevents Enter", CancelDuringOutageObservation);
            Console.WriteLine("Service confirmation: {0} passed; {1} failed. Fake window/input callbacks only.", passed, failed);
            return failed;
        }

        private static void LegacyProxyValues()
        {
            string[] names = { "Global", "Singapore", "Tokyo", "Los Angeles" };
            for (int i = 0; i < names.Length; i++)
                Assert(VanillaProxyPattern.NameForRoute(JsonConvert.DeserializeObject<VanillaProxyRoute>(i.ToString())) == names[i]);
            Assert(Enum.GetValues(typeof(VanillaProxyRoute)).Length == 8);
            foreach (VanillaProxyRoute route in Enum.GetValues(typeof(VanillaProxyRoute)))
                Assert(JsonConvert.DeserializeObject<VanillaProxyRoute>(JsonConvert.SerializeObject(route)) == route);
        }

        private static void DefaultConfirmation()
        {
            var order = new List<string>();
            var pauses = new List<int>();
            VanillaServiceSelection.ConfirmDefault(() => order.Add("activate"), () => order.Add("Enter"),
                ms => { pauses.Add(ms); order.Add("settle"); }, () => false);
            Assert(order.First() == "activate" && order.Last() == "Enter", "Enter must follow activation and settling.");
            Assert(order.Count(value => value == "activate") == 1 && order.Count(value => value == "Enter") == 1);
            Assert(pauses.Sum() == 1000 && pauses.All(ms => ms > 0 && ms <= 50));
        }

        private static void MissingProxyPreference()
        {
            var account = new VanillaReconnectAccount
            {
                UserName = "synthetic-login", ProtectedPassword = "synthetic-protected-value",
                CharacterSlot = 1, ProxyNeedsConfiguration = true
            };
            Assert(VanillaReconnectSupervisor.MissingCharacterConfiguration(account) == null,
                "An unused saved proxy preference must not block default confirmation.");
            Assert(account.ProxyNeedsConfiguration, "Validation must preserve legacy saved metadata.");
            account.ProtectedPassword = string.Empty;
            Assert(VanillaReconnectSupervisor.MissingCharacterConfiguration(account) == "Password is not set",
                "Removing the proxy requirement must preserve credential requirements.");
        }

        private static void ActivationFailure()
        {
            var failure = new InvalidOperationException("Intended window does not own focus.");
            int activations = 0, keys = 0, waits = 0;
            Exception actual = Throws<InvalidOperationException>(() => VanillaServiceSelection.ConfirmDefault(
                () => { activations++; throw failure; }, () => keys++, ms => waits++, () => false));
            Assert(ReferenceEquals(actual, failure) && activations == 1 && keys == 0 && waits == 0);
        }

        private static void CancelBeforeActivation()
        {
            int activations = 0, keys = 0, waits = 0;
            Throws<OperationCanceledException>(() => VanillaServiceSelection.ConfirmDefault(
                () => activations++, () => keys++, ms => waits++, () => true));
            Assert(activations == 0 && keys == 0 && waits == 0);
        }

        private static void CancelDuringActivation()
        {
            bool stopped = false;
            int activations = 0, keys = 0, waits = 0;
            Throws<OperationCanceledException>(() => VanillaServiceSelection.ConfirmDefault(
                () => { activations++; stopped = true; }, () => keys++, ms => waits++, () => stopped));
            Assert(activations == 1 && keys == 0 && waits == 0);
        }

        private static void CancelDuringSettle()
        {
            bool stopped = false;
            int keys = 0, waits = 0;
            Throws<OperationCanceledException>(() => VanillaServiceSelection.ConfirmDefault(
                () => { }, () => keys++, ms => { waits++; stopped = true; }, () => stopped));
            Assert(keys == 0 && waits == 1, "Cancellation must not wait through the remaining settle period.");
        }

        private static void CancelAtSubmitBoundary()
        {
            bool stopped = false;
            int elapsed = 0, keys = 0;
            Throws<OperationCanceledException>(() => VanillaServiceSelection.ConfirmDefault(
                () => { }, () => keys++, ms => { elapsed += ms; stopped = elapsed == 123; }, () => stopped, 123));
            Assert(elapsed == 123 && keys == 0, "STOP after the last wait must still suppress Enter.");
        }

        private static void CancelZeroSettle()
        {
            bool stopped = false;
            int keys = 0, waits = 0;
            Throws<OperationCanceledException>(() => VanillaServiceSelection.ConfirmDefault(
                () => stopped = true, () => keys++, ms => waits++, () => stopped, 0));
            Assert(keys == 0 && waits == 0);
        }

        private static void EnterFailure()
        {
            var failure = new InvalidOperationException("Foreground ownership lost before Enter.");
            int activations = 0, keys = 0, elapsed = 0;
            Exception actual = Throws<InvalidOperationException>(() => VanillaServiceSelection.ConfirmDefault(
                () => activations++, () => { keys++; throw failure; }, ms => elapsed += ms, () => false));
            Assert(ReferenceEquals(actual, failure) && activations == 1 && keys == 1 && elapsed == 1000,
                "Failed submission must not retry or restart activation.");
        }

        private static void SettleFailure()
        {
            int keys = 0, waits = 0;
            Throws<InvalidOperationException>(() => VanillaServiceSelection.ConfirmDefault(
                () => { }, () => keys++, ms => { waits++; throw new InvalidOperationException("Wait interrupted"); }, () => false));
            Assert(keys == 0 && waits == 1);
        }

        private static void CustomSettle()
        {
            var pauses = new List<int>();
            int keys = 0;
            VanillaServiceSelection.ConfirmDefault(() => { }, () => { Assert(pauses.Sum() == 123); keys++; },
                pauses.Add, () => false, 123);
            Assert(keys == 1 && pauses.SequenceEqual(new[] { 50, 50, 23 }));
        }

        private static void SettleBounds()
        {
            foreach (int duration in new[] { 0, 120000 })
            {
                int elapsed = 0, keys = 0, activations = 0;
                VanillaServiceSelection.ConfirmDefault(() => activations++, () => keys++,
                    ms => { Assert(ms > 0 && ms <= 50); elapsed += ms; }, () => false, duration);
                Assert(activations == 1 && keys == 1 && elapsed == duration);
            }
        }

        private static void InvalidSettle()
        {
            foreach (int duration in new[] { -1, 120001 })
            {
                int actions = 0;
                Throws<ArgumentOutOfRangeException>(() => VanillaServiceSelection.ConfirmDefault(
                    () => actions++, () => actions++, ms => actions++, () => false, duration));
                Assert(actions == 0, "Invalid settling must fail before activation.");
            }
        }

        private static void CheckOutageBeforeEnter()
        {
            var order = new List<string>();
            VanillaServiceSelection.ConfirmDefault(() => order.Add("activate"), () => order.Add("Enter"),
                ms => order.Add("settle"), () => false, 50, () => order.Add("outage check"));
            Assert(string.Join(",", order) == "activate,settle,outage check,Enter");
        }

        private static void ConfirmedOutage()
        {
            int keys = 0, observations = 0;
            Throws<VanillaServerClosedException>(() => VanillaServiceSelection.ConfirmDefault(() => { }, () => keys++,
                ms => { }, () => false, 50, () => { observations++; throw new VanillaServerClosedException(); }));
            Assert(keys == 0 && observations == 1, "Confirmed downtime must propagate without Enter or retry.");
        }

        private static void CancelDuringOutageObservation()
        {
            bool cancelled = false;
            int keys = 0;
            Throws<OperationCanceledException>(() => VanillaServiceSelection.ConfirmDefault(() => { }, () => keys++,
                ms => { }, () => cancelled, 50, () => cancelled = true));
            Assert(keys == 0, "STOP during the outage check must suppress Enter.");
        }

        private static Exception Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T exception) { return exception; }
            throw new Exception("Expected " + typeof(T).Name + ".");
        }

        private static void Assert(bool value, string detail = "Service confirmation assertion failed.")
        { if (!value) throw new Exception(detail); }
    }
}
