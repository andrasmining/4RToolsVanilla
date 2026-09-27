using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaSmartTeleportTests
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        private static int passed, failed;
        internal static int Run()
        {
            passed = failed = 0;
            Test("Teleport activates before every foreground-only observation/input", VerifiedSequence);
            Test("Existing warp dialog sends no hotkey or Enter", ExistingPopup);
            Test("Unknown popup never authorizes Enter", UnknownPopup);
            Test("Changing confirmation never authorizes Enter", ChangedConfirmation);
            Test("One matching frame is insufficient", UnstablePopup);
            Test("Movement before focus creates no native input session", MovementBeforeFocus);
            Test("Movement during focus suppresses capture and hotkey", MovementDuringFocus);
            Test("Movement while waiting for popup suppresses Enter", MovementBeforeEnter);
            Test("Movement after confirmation sends no further input or capture", MovementAfterEnter);
            Test("STOP during popup wait cancels and skips minimization", StopDuringWait);
            Test("Lost foreground aborts before hotkey and disposes transport", LostForeground);
            Test("Missing popup is bounded and minimizes only the owned client", MissingPopup);
            Test("Outer recovery keeps ownership of minimization", OuterRecoveryCleanup);
            Test("A missing hotkey never activates or captures", NoHotkey);
            Test("Movement guard detects X, Y and movement-return history", MovementGuard);
            Test("Movement guard rejects stale, missing and replaced identity", InvalidMovement);
            Test("Loading after Enter never claims dialog disappearance", LoadingAfterEnter);
            Test("Cleanup failure still disposes the input transport", CleanupFailure);
            Test("Resized capture cannot authorize warp confirmation", ResizedCapture);
            Console.WriteLine("Smart Teleport tests: " + passed + " passed; " + failed + " failed.");
            return failed;
        }

        private sealed class Harness : IVanillaTeleportInput
        {
            internal int Ms, Factories, Activations, Captures, Chords, Enters, Minimizes, Disposals;
            internal bool Cancelled, Moved, Active, Cleanup = true, FailCapture, FailCleanup;
            internal readonly List<string> Events = new List<string>();
            internal Func<int, Bitmap> Frame;
            internal Action OnActivate, OnEnter, OnPause;
            internal Func<bool> MovementOverride;
            internal string Detail;
            internal int Key = (int)Keys.F5;
            private Func<bool> cancel;
            internal bool Run()
            {
                return VanillaVerifiedTeleportAction.TryExecute(42,
                    new VanillaReconnectAccount { SmartTeleportKey = Key }, () => Cancelled, "synthetic",
                    out Detail, () => MovementOverride == null ? Moved : MovementOverride(),
                    (pid, check) => { Assert(pid == 42, "Wrong target PID."); Factories++; cancel = check; return this; },
                    () => TimeSpan.FromMilliseconds(Ms), ms => { Ms += ms; OnPause?.Invoke(); },
                    Cleanup ? (Action<Action>)(step => { if (!Cancelled) step(); }) : null);
            }
            private void Check()
            {
                if (cancel()) throw new OperationCanceledException();
                Assert(Active, "Capture/input did not own the foreground.");
            }
            public void Activate()
            { if (cancel()) throw new OperationCanceledException(); Activations++; Events.Add("activate"); Active = true; OnActivate?.Invoke(); }
            public Bitmap Capture()
            {
                Check(); Captures++; Events.Add("capture");
                if (FailCapture) throw new InvalidOperationException("Foreground lost.");
                return Frame == null ? Image(Enters == 0 && Chords > 0 ? 1 : 0) : Frame(Captures);
            }
            public void Chord(VanillaReconnectAccount account)
            { Check(); Chords++; Events.Add("hotkey"); }
            public void ConfirmWarp() { Check(); Enters++; Events.Add("enter"); OnEnter?.Invoke(); }
            public void Minimize(Action<Action> ownedStep)
            { ownedStep(() => { if (FailCleanup) throw new Exception("minimize failed"); Minimizes++; Events.Add("minimize"); Active = false; }); }
            public void Dispose() { Disposals++; Events.Add("dispose"); }
        }

        private static Bitmap Image(int type)
        {
            var image = new Bitmap(800, 600);
            using (Graphics g = Graphics.FromImage(image))
            {
                g.Clear(Color.FromArgb(95, 80, 55));
                if (type == 1)
                {
                    using (var light = new SolidBrush(Color.FromArgb(245, 245, 245))) g.FillRectangle(light, 270, 300, 300, 125);
                    using (var selected = new SolidBrush(Color.FromArgb(185, 205, 245))) g.FillRectangle(selected, 282, 329, 275, 19);
                }
                else if (type == 2)
                {
                    g.FillRectangle(Brushes.WhiteSmoke, 290, 300, 220, 58);
                    g.FillRectangle(Brushes.LightSteelBlue, 305, 328, 70, 17);
                }
            }
            return image;
        }
        private static void VerifiedSequence()
        {
            var h = new Harness(); Assert(h.Run(), h.Detail);
            Assert(h.Activations == 1 && h.Chords == 1 && h.Enters == 1 && h.Captures == 6
                && h.Minimizes == 1 && h.Disposals == 1, "Verified sequence or cleanup was duplicated/missing.");
            Assert(h.Events.First() == "activate" && h.Events.Last() == "dispose"
                && h.Events.IndexOf("enter") > h.Events.IndexOf("hotkey"), "Native operation order changed.");
        }
        private static void ExistingPopup()
        {
            var h = new Harness { Frame = _ => Image(1) };
            Assert(!h.Run() && h.Chords == 0 && h.Enters == 0 && h.Minimizes == 1, "Existing popup received input.");
        }
        private static void UnknownPopup()
        {
            var h = new Harness { Frame = n => Image(n > 1 ? 2 : 0) };
            Assert(!h.Run() && h.Chords == 1 && h.Enters == 0, "Unknown popup received Enter.");
        }
        private static void ChangedConfirmation()
        {
            var h = new Harness { Frame = n => Image(n == 2 || n == 3 ? 1 : 0) };
            Assert(!h.Run() && h.Enters == 0 && h.Captures == 4, "Changed confirmation authorized Enter.");
        }
        private static void UnstablePopup()
        {
            var h = new Harness { Frame = n => Image(n > 1 && n % 2 == 0 ? 1 : 0) };
            Assert(!h.Run() && h.Enters == 0 && h.Ms == 3000, "Unstable popup accepted or wait exceeded its bound.");
        }
        private static void MovementBeforeFocus()
        {
            var h = new Harness { Moved = true };
            Assert(!h.Run() && h.Factories == 0 && h.Activations == 0, "Moving client acquired native input.");
        }
        private static void MovementDuringFocus()
        {
            var h = new Harness(); h.OnActivate = () => h.Moved = true;
            Assert(!h.Run() && h.Captures == 0 && h.Chords == 0 && h.Minimizes == 1, "Focus-time movement did not suppress input.");
        }
        private static void MovementBeforeEnter()
        {
            var h = new Harness(); h.OnPause = () => h.Moved = true;
            Assert(!h.Run() && h.Chords == 1 && h.Enters == 0 && h.Minimizes == 1, "Movement during popup wait sent Enter.");
        }
        private static void MovementAfterEnter()
        {
            var h = new Harness(); h.OnEnter = () => h.Moved = true;
            Assert(!h.Run() && h.Enters == 1 && h.Captures == 4 && h.Minimizes == 1, "Recovery continued after movement.");
        }
        private static void StopDuringWait()
        {
            var h = new Harness(); h.OnPause = () => h.Cancelled = true;
            Expect<OperationCanceledException>(() => h.Run());
            Assert(h.Enters == 0 && h.Minimizes == 0 && h.Disposals == 1, "STOP did not cancel input/cleanup.");
        }
        private static void LostForeground()
        {
            var h = new Harness { FailCapture = true };
            Expect<InvalidOperationException>(() => h.Run());
            Assert(h.Chords == 0 && h.Enters == 0 && h.Disposals == 1, "Capture failure reached input.");
        }
        private static void MissingPopup()
        {
            var h = new Harness { Frame = _ => Image(0) };
            Assert(!h.Run() && h.Enters == 0 && h.Ms == 3000 && h.Minimizes == 1, "Missing popup retry was unbounded.");
        }
        private static void OuterRecoveryCleanup()
        {
            var h = new Harness { Cleanup = false };
            Assert(h.Run() && h.Minimizes == 0 && h.Disposals == 1, "Shared action minimized during its outer verifier.");
        }
        private static void NoHotkey()
        {
            var h = new Harness { Key = 0 };
            Assert(!h.Run() && h.Factories == 0, "Missing hotkey touched the client.");
        }
        private static VanillaClientState Sample(Guid session, DateTimeOffset at, int x = 10, int y = 20)
        {
            var state = VanillaClientState.Create(session, at, null, new Dictionary<VanillaField, object>
            {
                { VanillaField.X, x }, { VanillaField.Y, y }, { VanillaField.Map, "map" },
                { VanillaField.CharacterName, "test character" }, { VanillaField.CurrentHP, 100u }, { VanillaField.MaxHP, 100u }
            }, null, null);
            state.ProcessId = 42;
            foreach (StateValue field in state.Fields.Values.Where(value => value.IsAvailable)) field.Validation = StateValidation.Valid;
            return state;
        }
        private static void MovementGuard()
        {
            Guid session = Guid.NewGuid();
            foreach (int variant in new[] { 0, 1, 2 })
            {
                var first = Sample(session, Epoch);
                var current = Sample(session, Epoch.AddMilliseconds(100), variant == 0 ? 11 : 10, variant == 1 ? 21 : 20);
                if (variant == 2) current.LastMovementAtUtc = current.SampledAtUtc;
                var guard = new VanillaTeleportMovementGuard(first, () => current, () => Epoch.AddMilliseconds(100));
                Assert(guard.MovementResumed(), "Fresh X/Y/history movement was not detected.");
                current = first;
                Assert(guard.MovementResumed(), "Verified movement was forgotten after returning to the original tile.");
            }
        }
        private static void InvalidMovement()
        {
            Guid session = Guid.NewGuid();
            foreach (int variant in new[] { 0, 1, 2, 3 })
            {
                var first = Sample(session, Epoch);
                var current = variant == 0 ? null : Sample(variant == 2 ? Guid.NewGuid() : session,
                    variant == 1 ? Epoch.AddSeconds(-5) : Epoch);
                if (variant == 3) current.ProcessId = 43;
                var guard = new VanillaTeleportMovementGuard(first, () => current, () => Epoch);
                Expect<InvalidOperationException>(() => guard.MovementResumed());
            }
        }
        private static void LoadingAfterEnter()
        {
            var h = new Harness();
            h.MovementOverride = () =>
            {
                if (h.Enters > 0) throw new InvalidOperationException("The client is loading or no longer ready for autobattle.");
                return false;
            };
            Assert(!h.Run() && h.Enters == 1 && h.Captures == 4
                && h.Detail.Contains("pending"), "Loading falsely confirmed dismissal or sent more input.");
        }
        private static void CleanupFailure()
        {
            var h = new Harness { FailCleanup = true };
            Expect<Exception>(() => h.Run());
            Assert(h.Disposals == 1, "Failed cleanup leaked the transport.");
        }
        private static void ResizedCapture()
        {
            var h = new Harness { Frame = n => n == 1 ? Image(0) : new Bitmap(640, 480) };
            Expect<InvalidOperationException>(() => h.Run());
            Assert(h.Enters == 0 && h.Disposals == 1, "Changed client geometry received Enter.");
        }
        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Expect<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + "."); }
    }
}
