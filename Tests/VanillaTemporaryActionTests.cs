using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    public static class VanillaTemporaryActionTests
    {
        public static int Run()
        {
            int failed = 0;
            var tests = new Dictionary<string, System.Action>
            {
                { "Temporary actions use safe SP hysteresis defaults", Defaults },
                { "Temporary action settings reject unsafe thresholds", ThresholdValidation },
                { "Temporary target positions remain client-relative percentages", RelativeTarget },
                { "Temporary settings clone isolates mutable state", Clone },
                { "Temporary scene permits a small spell glow but blocks a central modal", InputScene }
            };
            foreach (var test in tests)
            {
                try { test.Value(); Console.WriteLine("PASS " + test.Key); }
                catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Key + ": " + ex); }
            }
            Console.WriteLine("Temporary actions: {0} passed; {1} failed. No process or input was opened.", tests.Count - failed, failed);
            failed += VanillaMemoryAndWeightTests.Run();
            return failed;
        }

        private static void Defaults()
        {
            var value = new VanillaTemporaryActionSettings();
            value.Validate();
            Assert(value.SpRestEnabled && value.RestBelowPercent == 10m && value.ResumeAbovePercent == 80m,
                "Default rest hysteresis should be 10/80.");
            Assert(value.SitStandKey == (int)Keys.Insert, "Default sit/stand key should be Insert.");
        }

        private static void ThresholdValidation()
        {
            foreach (var pair in new[] { Tuple.Create(10m, 10m), Tuple.Create(50m, 20m), Tuple.Create(0m, 80m), Tuple.Create(10m, 101m) })
            {
                var value = new VanillaTemporaryActionSettings { RestBelowPercent = pair.Item1, ResumeAbovePercent = pair.Item2 };
                Throws(value.Validate);
            }
        }

        private static void RelativeTarget()
        {
            var value = new VanillaTemporaryActionSettings { TargetXPercent = 12.5m, TargetYPercent = 87.5m };
            value.Validate();
            Assert(value.TargetXPercent == 12.5m && value.TargetYPercent == 87.5m, "Target coordinates are normalized percentages.");
            value.TargetXPercent = -1; Throws(value.Validate);
            value.TargetXPercent = 50; value.TargetYPercent = 100.1m; Throws(value.Validate);
        }

        private static void Clone()
        {
            var original = new VanillaTemporaryActionSettings { ActionKey = (int)Keys.F5, RestBelowPercent = 12, ResumeAbovePercent = 88 };
            var clone = original.Clone();
            clone.ActionKey = (int)Keys.F6;
            Assert(original.ActionKey == (int)Keys.F5 && clone.ActionKey == (int)Keys.F6, "Clone should be independent.");
        }

        private static void InputScene()
        {
            using (var scene = new Bitmap(1024, 768))
            using (Graphics graphics = Graphics.FromImage(scene))
            {
                graphics.Clear(Color.DarkOliveGreen);
                graphics.FillEllipse(Brushes.White, 520, 325, 80, 100);
                VanillaTemporaryActionRunner.RequireInputScene(scene);
                graphics.FillRectangle(Brushes.White, 310, 300, 400, 210);
                try { VanillaTemporaryActionRunner.RequireInputScene(scene); }
                catch (InvalidOperationException) { return; }
                throw new Exception("Central modal allowed temporary input over unchanged surrounding terrain.");
            }
        }

        private static void Throws(System.Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new Exception("Expected ArgumentException.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
