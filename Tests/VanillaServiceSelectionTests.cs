using System;
using System.Drawing;
using Newtonsoft.Json;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaServiceSelectionTests
    {
        internal static int Run()
        {
            int failed = 0;
            Action<string, Action> test = (name, action) =>
            {
                try { action(); Console.WriteLine("PASS " + name); }
                catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
            };
            test("Legacy numeric proxy choices retain their service names", () =>
            {
                string[] names = { "Global", "Singapore", "Tokyo", "Los Angeles" };
                for (int i = 0; i < names.Length; i++)
                    Assert(VanillaProxyPattern.NameForRoute(JsonConvert.DeserializeObject<VanillaProxyRoute>(i.ToString())) == names[i]);
                Assert(Enum.GetValues(typeof(VanillaProxyRoute)).Length == 8);
                foreach (VanillaProxyRoute route in Enum.GetValues(typeof(VanillaProxyRoute)))
                    Assert(JsonConvert.DeserializeObject<VanillaProxyRoute>(JsonConvert.SerializeObject(route)) == route);
            });
            test("Service submit requires a click and two subsequent highlight captures", () =>
            {
                int samples = 0, clicks = 0, submits = 0;
                VanillaServiceSelection.Select("Tokyo", () => { samples++; return Row(samples >= 3); },
                    row => { Assert(samples == 2); clicks++; }, () => { Assert(samples == 4); submits++; },
                    ms => { }, () => samples > 8);
                Assert(clicks == 1 && submits == 1);
            });
            test("An already highlighted service still needs selection and fresh proof", () =>
            {
                int samples = 0, clicks = 0;
                VanillaServiceSelection.Select("Tokyo", () => { samples++; return Row(true); }, row => clicks++,
                    () => Assert(samples == 4 && clicks == 1), ms => { }, () => samples > 8);
            });
            test("Wrong service or absent highlight never submits", () =>
            {
                foreach (bool wrongName in new[] { true, false })
                {
                    int samples = 0, submits = 0;
                    try
                    {
                        VanillaServiceSelection.Select("Tokyo", () => { samples++; var row = Row(false); if (wrongName) row.Name = "Global"; return row; },
                            row => { }, () => submits++, ms => { }, () => samples > 12);
                        throw new Exception("Expected cancellation");
                    }
                    catch (OperationCanceledException) { }
                    Assert(submits == 0);
                }
            });
            test("Moving service controls invalidate selection before Enter", () =>
            {
                int samples = 0, submits = 0;
                try
                {
                    VanillaServiceSelection.Select("Tokyo", () => { samples++; var row = Row(true); if (samples > 2) row.Bounds.Offset(0, 20); return row; },
                        row => { }, () => submits++, ms => { }, () => samples > 8);
                    throw new Exception("Expected rejected layout");
                }
                catch (InvalidOperationException) { }
                Assert(submits == 0);
            });
            test("Missing captures break consecutive highlight evidence", () =>
            {
                int samples = 0;
                VanillaServiceSelection.Select("Tokyo", () => { samples++; return samples == 4 ? null : Row(samples > 2); },
                    row => { }, () => Assert(samples == 6), ms => { }, () => samples > 8);
            });
            test("STOP during final service capture blocks Enter", () =>
            {
                int samples = 0, submits = 0;
                try
                {
                    VanillaServiceSelection.Select("Tokyo", () => { samples++; return Row(true); }, row => { },
                        () => submits++, ms => { }, () => samples >= 4);
                    throw new Exception("Expected cancellation");
                }
                catch (OperationCanceledException) { }
                Assert(submits == 0);
            });
            return failed;
        }

        private static VanillaServiceObservation Row(bool highlighted) => new VanillaServiceObservation
        { Name = "Tokyo", Bounds = new Rectangle(170, 240, 150, 18), ImageSize = new Size(800, 600), Highlighted = highlighted };
        private static void Assert(bool value) { if (!value) throw new Exception("Service selection assertion failed."); }
    }
}
