using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaStabilityTests
    {
        private static int passed, failed;
        internal static int Run()
        {
            Test("Public releases never resolve stale or absent credentials", PublicAccess);
            Test("Public web latest and direct assets require no credentials", PublicWebRelease);
            Test("Public API asset fallback remains anonymous through its CDN", PublicFallback);
            Test("Update success preserves unrelated user files and previous managed files", UpdateSuccess);
            Test("Partial copy failure restores old bytes and removes newly added files", UpdateCopyRollback);
            Test("Application start failure rolls back the managed installation", UpdateStartRollback);
            Test("Manifest rejects missing executable, extras, duplicates and user data", ManifestSafety);
            Test("Observed stack quantities never exceed capacity or the offered stack", QuantityBounds);
            Test("Production quantity recognition reads selected numeric fields", QuantityVision);
            Test("Temporary pending cast finishes before move and sit", TemporaryRestSequence);
            Test("Temporary sit requires movement and fails on the bounded deadline", TemporaryMoveTimeout);
            Test("Temporary pending input rejects death, session change and cancellation", TemporaryIdentity);
            Test("Temporary rest requires new movement on every SP cycle", TemporaryRepeatedRest);
            Test("Temporary walking settles only over fresh observation time", TemporaryFreshMovementSettle);
            Test("Temporary continuous movement resets the sit settle window", TemporaryContinuousMovement);
            Test("Temporary movement baseline follows foreground preparation", TemporaryMoveBaseline);
            Test("Temporary STOP during SP rest suppresses standing and casting", TemporaryRestCancellation);
            Test("Temporary delays begin after synchronous input completes", TemporaryInputCompletionTiming);
            Test("Temporary target geometry supports legacy settings and rejects invalid captures", TemporaryCaptureGeometry);
            Test("Observed Vanilla cards need unique selection and distinguish an empty slot", ObservedCards);
            Test("Post-click cursor parking clears controls across client sizes and edges", CursorParkingGeometry);
            Test("Post-click cursor parking clears the central form and rejects unavailable space", CursorParkingExclusion);
            Test("Mouse release and cursor parking precede the next visual check", ClickParkingSequence);
            Test("Cancelled or rejected clicks release owned buttons without cursor parking", ClickParkingFailure);
            Test("Native navigation scan codes retain E0 and differ from keypad keys", ExtendedNavigationKeys);
            Test("Ordinary keys and left/right modifiers retain correct scan and release flags", OrdinaryScanCodeKeys);
            Test("Missing and unsupported E1 scan mappings never produce key input", UnsupportedScanCodeKeys);
            Console.WriteLine("Stability integration: {0} passed; {1} failed. Isolated synthetic state/files/HTTP; no live game input.", passed, failed);
            return failed;
        }
        private static void PublicAccess()
        {
            int calls = 0;
            using (var http = new HttpClient(new Handler(request =>
            {
                calls++; Assert(request.Headers.Authorization == null, "Public HTTP carried credentials.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("public") };
            })))
            {
                var client = new VanillaPrivateReleaseClient(http, () => { throw new Exception("Corrupt saved credentials must not be read."); }, true);
                Assert(client.ReadLatestAsync().GetAwaiter().GetResult() == "public", "Metadata failed.");
                Assert(client.ReadAssetAsync(VanillaPrivateReleaseClient.AssetUrl(123), 1024).GetAwaiter().GetResult().Length == 6, "Asset failed.");
                Assert(calls == 2, "Unexpected public retries.");
            }
        }
        private static void PublicWebRelease()
        {
            int calls = 0, credentials = 0;
            using (var http = new HttpClient(new Handler(request =>
            {
                calls++;
                Assert(request.Headers.Authorization == null, "Anonymous public web path carried credentials.");
                string absolute = request.RequestUri.AbsoluteUri;
                if (absolute == "https://github.com/andrasmining/4RToolsVanilla/releases/latest")
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri("/andrasmining/4RToolsVanilla/releases/tag/v0.6.71", UriKind.Relative);
                    return response;
                }
                if (absolute == "https://github.com/andrasmining/4RToolsVanilla/releases/download/v0.6.71/test.zip")
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/test.zip?signature=synthetic");
                    return response;
                }
                if (absolute.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 4, 7, 1 }) };
                throw new Exception("Unexpected public updater request: " + absolute);
            })))
            {
                var client = new VanillaPrivateReleaseClient(http,
                    () => { credentials++; return Task.FromResult("stale_token_must_not_be_used"); }, true);
                Assert(client.ReadLatestPublicTagAsync().GetAwaiter().GetResult() == "v0.6.71", "Public latest tag was not resolved.");
                string direct = VanillaPrivateReleaseClient.PublicAssetUrl("v0.6.71", "test.zip");
                Assert(VanillaPrivateReleaseClient.IsPublicReleaseAsset(new Uri(direct)), "Canonical public asset URL was rejected.");
                Assert(client.ReadAssetAsync(direct, 1024).GetAwaiter().GetResult().SequenceEqual(new byte[] { 4, 7, 1 }),
                    "Public direct asset did not follow its credential-free CDN redirect.");
                Assert(credentials == 0 && calls == 3, "Public web update path touched credentials or retried unexpectedly.");
                Assert(!VanillaPrivateReleaseClient.IsPublicReleaseAsset(new Uri("https://github.com/other/repo/releases/download/v0.6.71/test.zip")),
                    "Foreign public asset was accepted.");
            }
        }

        private static void PublicFallback()
        {
            int calls = 0, credentials = 0;
            using (var http = new HttpClient(new Handler(request =>
            {
                calls++;
                Assert(request.Headers.Authorization == null && !request.Headers.Contains("X-GitHub-Api-Version"),
                    "Public API/CDN fallback carried credentials.");
                if (calls == 1)
                {
                    Assert(request.RequestUri.AbsoluteUri == VanillaPrivateReleaseClient.AssetUrl(123),
                        "Unexpected public API asset URL.");
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/test.zip?signature=synthetic");
                    return response;
                }
                Assert(request.RequestUri.Host == "release-assets.githubusercontent.com", "Public asset redirect did not reach the release CDN.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
            })))
            {
                var client = new VanillaPrivateReleaseClient(http, () => { credentials++; return Task.FromResult("synthetic_test_token_not_real"); }, true);
                Assert(client.ReadAssetAsync(VanillaPrivateReleaseClient.AssetUrl(123), 1024).GetAwaiter().GetResult().Length == 1, "Fallback failed.");
                Assert(calls == 2 && credentials == 0, "Current public fallback resolved credentials or retried unexpectedly.");
            }
        }
        private static void UpdateSuccess()
        {
            WithInstall((payload, install) =>
            {
                Directory.CreateDirectory(Path.Combine(install, "Profiles"));
                File.WriteAllText(Path.Combine(install, "Profiles", "user.json"), "unchanged");
                int starts = 0;
                string backup = VanillaUpdateTransaction.Apply(payload, install, path =>
                { starts++; Assert(File.ReadAllText(path) == "new executable", "Start saw unverified bytes."); });
                Assert(starts == 1 && File.ReadAllText(Path.Combine(backup, "4RTools-Vanilla.exe")) == "old executable", "Old version not backed up.");
                Assert(File.ReadAllText(Path.Combine(install, "Profiles", "user.json")) == "unchanged", "User data changed.");
                VanillaUpdater.VerifyPayloadManifest(payload);
            });
        }
        private static void UpdateCopyRollback()
        {
            WithInstall((payload, install) =>
            {
                int starts = 0;
                Reject(() => VanillaUpdateTransaction.Apply(payload, install, _ => starts++, (source, destination) =>
                {
                    File.Copy(source, destination, true);
                    if (destination.EndsWith("model.dat")) { File.WriteAllText(destination, "partial"); throw new IOException("Injected partial copy failure."); }
                }));
                Assert(starts == 0 && File.ReadAllText(Path.Combine(install, "4RTools-Vanilla.exe")) == "old executable", "Rollback lost old app.");
                Assert(!File.Exists(Path.Combine(install, "SHA256SUMS.txt")) && !Directory.Exists(Path.Combine(install, "ocr")), "Rollback left added payload files/directories.");
            });
        }
        private static void UpdateStartRollback()
        {
            WithInstall((payload, install) =>
            {
                Reject(() => VanillaUpdateTransaction.Apply(payload, install, _ => { throw new IOException("Injected start failure."); }));
                Assert(File.ReadAllText(Path.Combine(install, "4RTools-Vanilla.exe")) == "old executable", "Failed start was not rolled back.");
                Assert(!File.Exists(Path.Combine(install, "ocr", "model.dat")), "Failed start left new model.");
            });
        }
        private static void ManifestSafety()
        {
            WithInstall((payload, install) =>
            {
                string manifest = Path.Combine(payload, "SHA256SUMS.txt"), original = File.ReadAllText(manifest);
                File.WriteAllText(manifest, ""); Reject(() => VanillaUpdater.VerifyPayloadManifest(payload));
                File.WriteAllText(manifest, original + original); Reject(() => VanillaUpdater.VerifyPayloadManifest(payload));
                File.WriteAllText(manifest, original);
                File.WriteAllText(Path.Combine(payload, "extra.dll"), "unlisted"); Reject(() => VanillaUpdater.VerifyPayloadManifest(payload));
                File.Delete(Path.Combine(payload, "extra.dll"));
                foreach (string path in new[] { "../escape", "Profiles/user.json", "file:stream", "CON.txt", "a/../b", "UpdateAccess/token.bin" })
                {
                    File.WriteAllText(manifest, original + new string('0', 64) + "  " + path + Environment.NewLine);
                    Reject(() => VanillaUpdater.VerifyPayloadManifest(payload));
                }
            });
        }
        private static void WithInstall(Action<string, string> action)
        {
            string root = Path.Combine(Path.GetTempPath(), "4RTools-Stability-" + Guid.NewGuid().ToString("N"));
            string payload = Path.Combine(root, "payload"), install = Path.Combine(root, "install");
            Directory.CreateDirectory(Path.Combine(payload, "ocr")); Directory.CreateDirectory(install);
            try
            {
                File.WriteAllText(Path.Combine(payload, "4RTools-Vanilla.exe"), "new executable");
                File.WriteAllText(Path.Combine(payload, "ocr", "model.dat"), "new model");
                File.WriteAllText(Path.Combine(install, "4RTools-Vanilla.exe"), "old executable");
                File.WriteAllLines(Path.Combine(payload, "SHA256SUMS.txt"), new[] { "4RTools-Vanilla.exe", "ocr/model.dat" }
                    .Select(name => Hash(Path.Combine(payload, name)) + "  " + name), new UTF8Encoding(false));
                action(payload, install);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static string Hash(string path)
        { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(File.ReadAllBytes(path)).Select(b => b.ToString("x2"))); }
        private static void QuantityBounds()
        {
            for (uint offered = 1; offered <= 120; offered += 7)
            for (uint unit = 1; unit <= 100; unit += 9)
            for (uint remaining = 0; remaining <= 999; remaining += 13)
            {
                uint carried = offered * unit + 137;
                uint amount = VanillaCartQuantity.ConservativeAmount(carried, offered, 10000 - remaining, 10000);
                Assert(amount <= offered && (ulong)amount * unit <= remaining, "Conservative quantity overflowed its stack/capacity.");
            }
            Assert(VanillaCartQuantity.ConservativeAmount(0, 10, 9000, 10000) == 0, "Contradictory zero carry accepted.");
            Assert(VanillaCartQuantity.ConservativeAmount(uint.MaxValue, uint.MaxValue, uint.MaxValue - 7, uint.MaxValue) == 7, "Unsigned arithmetic overflow.");
            Assert(VanillaWeightCartAutomation.CartFullPercent == 99m, "9993/10000 would trigger futile transfers.");
        }
        private static void QuantityVision()
        {
            foreach (string text in new[] { "7", "123", "1000", "9999", "abc", "0" })
            foreach (Color selection in new[] { Color.Blue, Color.FromArgb(111, 158, 242) })
            using (var image = new Bitmap(640, 480))
            using (Graphics graphics = Graphics.FromImage(image))
            using (var font = new Font("Tahoma", 16, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var background = new SolidBrush(selection))
            {
                graphics.Clear(Color.FromArgb(90, 95, 90));
                graphics.FillRectangle(Brushes.White, 180, 170, 270, 80);
                graphics.FillRectangle(background, 200, 210, 90, 24);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(text, font, Brushes.White, 204, 212, StringFormat.GenericTypographic);
                VanillaQuantityObservation observed;
                string recognitionEvidence;
                bool found = VanillaCartQuantity.TryObserve(image, out observed, out recognitionEvidence);
                uint expected;
                bool numeric = uint.TryParse(text, out expected) && expected > 0;
                if (found != numeric || (found && observed.Amount != expected))
                {
                    string directory = Path.Combine(Environment.CurrentDirectory, "dist", "validation", "quantity-fixtures");
                    Directory.CreateDirectory(directory);
                    image.Save(Path.Combine(directory, "synthetic-" + text + "-" + selection.ToArgb() + ".png"));
                    throw new Exception("Selected numeric quantity recognition mismatch: synthetic='" + text
                        + "' selection=" + selection + " found=" + found + " read=" + (found ? observed.Amount.ToString() : "none")
                        + " evidence=[" + recognitionEvidence + "]");
                }
            }
        }
        private sealed class FakeTemporary : IVanillaTemporaryIo
        {
            private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            internal int Actions, Clicks, Moves, Sits, Releases, X = 100, Y = 100;
            internal int PrepareDelay, HotkeyDelay, TargetDelay, MoveDelay, SitDelay, BeforeMoveXDelta;
            internal TimeSpan Now, SampleTime;
            internal decimal Sp = 70;
            internal Guid Session = Guid.NewGuid();
            internal bool Alive = true, Cancelled;
            public TimeSpan Time { get { return Now; } }
            public VanillaTemporarySample Read() { return new VanillaTemporarySample { Identity = "test", Map = "test-map", Session = Session,
                At = Epoch + SampleTime, X = X, Y = Y, Sp = Sp, Alive = Alive }; }
            public bool Acquire() { return true; }
            public void Release() { Releases++; }
            public void CheckCancelled() { if (Cancelled) throw new OperationCanceledException(); }
            public void PrepareTarget() { Now += TimeSpan.FromMilliseconds(PrepareDelay); }
            public void ActionHotkey() { Actions++; Now += TimeSpan.FromMilliseconds(HotkeyDelay); }
            public void TargetClick() { Clicks++; Now += TimeSpan.FromMilliseconds(TargetDelay); }
            public VanillaTemporarySample MoveBeforeSit()
            {
                Moves++; X += BeforeMoveXDelta; BeforeMoveXDelta = 0;
                var baseline = Read(); Now += TimeSpan.FromMilliseconds(MoveDelay); return baseline;
            }
            public void SitStand() { Sits++; Now += TimeSpan.FromMilliseconds(SitDelay); }
        }

        private static void ExtendedNavigationKeys()
        {
            Keys[] navigation = { Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home, Keys.End,
                Keys.Insert, Keys.Delete, Keys.PageUp, Keys.PageDown };
            Keys[] keypad = { Keys.NumPad4, Keys.NumPad6, Keys.NumPad8, Keys.NumPad2, Keys.NumPad7,
                Keys.NumPad1, Keys.NumPad0, Keys.Decimal, Keys.NumPad9, Keys.NumPad3 };
            for (int index = 0; index < navigation.Length; index++)
            {
                // Calls only Windows' read-only key mapping, never SendInput.
                var down = VanillaForegroundInput.EncodeScanCodeKey(navigation[index], false);
                var up = VanillaForegroundInput.EncodeScanCodeKey(navigation[index], true);
                var pad = VanillaForegroundInput.EncodeScanCodeKey(keypad[index], false);
                Assert(down.ScanCode == pad.ScanCode && down.ScanCode > 0 && down.ScanCode <= 0xff,
                    "Expected a shared base scan code for " + navigation[index] + " and its keypad counterpart.");
                Assert(down.Flags == 0x0009 && pad.Flags == 0x0008,
                    "Navigation/keypad distinction was lost for " + navigation[index] + ".");
                Assert(up.ScanCode == down.ScanCode && up.Flags == 0x000b,
                    "Navigation key release lost its E0/scan flags for " + navigation[index] + ".");
                var bareDown = VanillaForegroundInput.EncodeScanCodeKey(navigation[index], false, (key, mode) => pad.ScanCode);
                var bareUp = VanillaForegroundInput.EncodeScanCodeKey(navigation[index], true, (key, mode) => pad.ScanCode);
                var barePad = VanillaForegroundInput.EncodeScanCodeKey(keypad[index], false, (key, mode) => pad.ScanCode);
                Assert(bareDown.ScanCode == pad.ScanCode && bareDown.Flags == 0x0009
                    && bareUp.Flags == 0x000b && barePad.Flags == 0x0008,
                    "A layout that omits E0 lost navigation/keypad identity for " + navigation[index] + ".");
            }
        }

        private static void OrdinaryScanCodeKeys()
        {
            var expected = new Dictionary<Keys, ushort> {
                { Keys.Enter, 0x1c }, { Keys.F1, 0x3b }, { Keys.A, 0x1e }, { Keys.Tab, 0x0f },
                { Keys.Back, 0x0e }, { Keys.Escape, 0x01 }, { Keys.ShiftKey, 0x2a },
                { Keys.ControlKey, 0x1d }, { Keys.Menu, 0x38 }, { Keys.RShiftKey, 0x36 }
            };
            foreach (var pair in expected)
            {
                var down = VanillaForegroundInput.EncodeScanCodeKey(pair.Key, false);
                var up = VanillaForegroundInput.EncodeScanCodeKey(pair.Key, true);
                Assert(down.ScanCode == pair.Value && down.Flags == 0x0008,
                    "Ordinary key encoding changed for " + pair.Key + ".");
                Assert(up.ScanCode == pair.Value && up.Flags == 0x000a,
                    "Ordinary key release changed for " + pair.Key + ".");
            }
            foreach (var pair in new Dictionary<Keys, ushort> { { Keys.RControlKey, 0x1d }, { Keys.RMenu, 0x38 } })
            {
                var down = VanillaForegroundInput.EncodeScanCodeKey(pair.Key, false);
                var up = VanillaForegroundInput.EncodeScanCodeKey(pair.Key, true);
                Assert(down.ScanCode == pair.Value && down.Flags == 0x0009 && up.Flags == 0x000b,
                    "Right modifier lost its extended identity on press/release.");
            }
        }

        private static void UnsupportedScanCodeKeys()
        {
            int calls = 0;
            var expected = VanillaForegroundInput.EncodeScanCodeKey(Keys.Left, false, (key, mode) =>
            {
                calls++;
                Assert(key == (uint)Keys.Left && mode == 4, "Extended key mapping mode was not requested.");
                return 0xe04b;
            });
            Assert(calls == 1 && expected.ScanCode == 0x4b && expected.Flags == 0x0009,
                "Extended scan mapping did not retain its prefix separately.");
            foreach (uint unsupported in new uint[] { 0, 0xe000, 0xe11d, 0x10001, 0x0101 })
            foreach (bool up in new[] { false, true })
                Reject(() => VanillaForegroundInput.EncodeScanCodeKey(Keys.Pause, up, (key, mode) => unsupported));
            foreach (Keys invalid in new[] { Keys.None, Keys.Control | Keys.A })
            {
                bool rejected = false;
                try { VanillaForegroundInput.EncodeScanCodeKey(invalid, false, (key, mode) => { throw new Exception("Invalid key reached native mapping."); }); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected, "Missing/combined virtual key was accepted as one physical key.");
            }
        }
        private static void TemporaryTick(FakeTemporary io, VanillaTemporaryCycle cycle, int milliseconds, int? sampleMilliseconds = null)
        {
            var next = TimeSpan.FromMilliseconds(milliseconds);
            Assert(next >= io.Now, "Synthetic monotonic input clock moved backwards.");
            io.Now = next; io.SampleTime = TimeSpan.FromMilliseconds(sampleMilliseconds ?? milliseconds);
            cycle.Tick(io.Now);
        }
        private static VanillaTemporaryActionSettings TemporarySettings()
        { return new VanillaTemporaryActionSettings { ActionKey = (int)Keys.F1, ClickTargetAfterKey = true, TargetClickDelayMs = 180, RestMoveCaptured = true }; }
        private static void TemporaryRestSequence()
        {
            var io = new FakeTemporary(); var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); io.Sp = 5;
            TemporaryTick(io, cycle, 200);
            Assert(io.Actions == 1 && io.Clicks == 1 && io.Moves == 0 && io.Sits == 0, "Low SP interrupted a pending cast into Sit.");
            TemporaryTick(io, cycle, 850); Assert(io.Moves == 1 && io.Sits == 0, "No verified movement required.");
            io.X++; TemporaryTick(io, cycle, 1000);
            TemporaryTick(io, cycle, 1500); Assert(io.Sits == 1 && cycle.Phase == VanillaTemporaryPhase.Resting, "Verified walk did not precede sit.");
            io.Sp = 85; TemporaryTick(io, cycle, 2000); Assert(io.Sits == 2, "Standing not requested after SP recovery.");
            TemporaryTick(io, cycle, 2300); Assert(io.Actions == 1, "Cast sent before standing settled.");
            TemporaryTick(io, cycle, 2700); Assert(io.Actions == 2, "Action did not resume.");
        }
        private static void TemporaryMoveTimeout()
        {
            var io = new FakeTemporary { Sp = 5 }; var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); TemporaryTick(io, cycle, 4000);
            Reject(() => TemporaryTick(io, cycle, 8000));
            Assert(io.Sits == 0 && io.Moves == 2 && io.Actions == 0, "Unverified movement led to sitting/casting or unbounded retries.");
        }
        private static void TemporaryIdentity()
        {
            for (int mode = 0; mode < 3; mode++)
            {
                var io = new FakeTemporary(); var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
                TemporaryTick(io, cycle, 0);
                if (mode == 0) io.Alive = false; else if (mode == 1) io.Session = Guid.NewGuid(); else io.Cancelled = true;
                Reject(() => TemporaryTick(io, cycle, 1000));
                Assert(io.Clicks == 0 && io.Sits == 0, "Invalid identity/death/STOP allowed a pending click.");
            }
        }
        private static void TemporaryRepeatedRest()
        {
            var io = new FakeTemporary(); var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); io.Sp = 5; TemporaryTick(io, cycle, 200);
            TemporaryTick(io, cycle, 850); io.X++; TemporaryTick(io, cycle, 1000); TemporaryTick(io, cycle, 1500);
            io.Sp = 85; TemporaryTick(io, cycle, 2000); TemporaryTick(io, cycle, 2700); TemporaryTick(io, cycle, 3000);
            io.Sp = 5; TemporaryTick(io, cycle, 3500);
            Assert(io.Moves == 1, "Second rest ignored the completed-cast settle interval.");
            TemporaryTick(io, cycle, 3650); TemporaryTick(io, cycle, 4150);
            Assert(io.Moves == 2 && io.Sits == 2, "First rest's movement authorized a second sit.");
            io.Y++; TemporaryTick(io, cycle, 4300); TemporaryTick(io, cycle, 4790);
            Assert(io.Sits == 3 && io.Clicks == 2 && cycle.Phase == VanillaTemporaryPhase.Resting,
                "Second SP cycle did not require and accept a new one-tile Y movement.");
        }
        private static void TemporaryFreshMovementSettle()
        {
            var io = new FakeTemporary { Sp = 5 }; var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); io.X++; TemporaryTick(io, cycle, 100);
            TemporaryTick(io, cycle, 300); TemporaryTick(io, cycle, 800, 300); TemporaryTick(io, cycle, 1000, 500);
            Assert(io.Sits == 0, "Repeated or early coordinate observations authorized sitting through wall-clock time alone.");
            TemporaryTick(io, cycle, 1100, 550);
            Assert(io.Sits == 1, "A fresh 450 ms stillness observation did not authorize sit after movement.");

            io = new FakeTemporary { Sp = 5 }; cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); io.X++; TemporaryTick(io, cycle, 100);
            TemporaryTick(io, cycle, 200, 1200);
            Assert(io.Sits == 0, "A forward observation-clock adjustment bypassed the monotonic walking settle interval.");
            TemporaryTick(io, cycle, 550, 1550);
            Assert(io.Sits == 1, "Movement did not settle after both monotonic and observation intervals elapsed.");
        }
        private static void TemporaryContinuousMovement()
        {
            var io = new FakeTemporary { Sp = 5 }; var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); io.X++; TemporaryTick(io, cycle, 100);
            io.X++; TemporaryTick(io, cycle, 400); TemporaryTick(io, cycle, 800);
            Assert(io.Sits == 0, "Sit was sent while the latest tile movement had not settled.");
            TemporaryTick(io, cycle, 850); Assert(io.Sits == 1, "Walking did not settle from the final tile change.");

            io = new FakeTemporary { Sp = 5 }; cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0);
            for (int at = 100; at < 10000; at += 300) { io.X++; TemporaryTick(io, cycle, at); }
            Reject(() => TemporaryTick(io, cycle, 10000));
            Assert(io.Sits == 0, "Continuous walking exceeded the hard move deadline or led to sitting.");
        }
        private static void TemporaryMoveBaseline()
        {
            var io = new FakeTemporary { Sp = 5, BeforeMoveXDelta = 1 };
            var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); TemporaryTick(io, cycle, 100); TemporaryTick(io, cycle, 1000);
            Assert(io.Sits == 0, "Movement during foreground preparation was mistaken for movement after the ground click.");
            io.X++; TemporaryTick(io, cycle, 1200); TemporaryTick(io, cycle, 1700);
            Assert(io.Sits == 1, "Post-click movement was not accepted against the actual click baseline.");
        }
        private static void TemporaryRestCancellation()
        {
            var io = new FakeTemporary { Sp = 5 }; var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); io.X++; TemporaryTick(io, cycle, 100); TemporaryTick(io, cycle, 600);
            io.Cancelled = true; io.Sp = 85; Reject(() => TemporaryTick(io, cycle, 1000));
            Assert(io.Sits == 1 && io.Actions == 0 && io.Clicks == 0, "STOP during SP rest sent an automatic stand or cast.");
        }
        private static void TemporaryInputCompletionTiming()
        {
            var io = new FakeTemporary { PrepareDelay = 600, HotkeyDelay = 120, TargetDelay = 310, MoveDelay = 310, SitDelay = 100 };
            var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); TemporaryTick(io, cycle, 899);
            Assert(io.Clicks == 0, "Target click delay began before foreground preparation/hotkey delivery completed.");
            TemporaryTick(io, cycle, 900); io.Sp = 5; TemporaryTick(io, cycle, 1809);
            Assert(io.Moves == 0, "Move-before-sit settle began before the target click completed.");
            TemporaryTick(io, cycle, 1810); io.X++; TemporaryTick(io, cycle, 2200); TemporaryTick(io, cycle, 2650);
            io.Sp = 85; TemporaryTick(io, cycle, 2800); TemporaryTick(io, cycle, 3499);
            Assert(io.Actions == 1, "Casting resumed before stand input and its full settle delay completed.");
            TemporaryTick(io, cycle, 3500); Assert(io.Actions == 2, "Casting did not resume after completed standing settled.");

            io = new FakeTemporary { PrepareDelay = 600, HotkeyDelay = 120, TargetDelay = 310 };
            cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); TemporaryTick(io, cycle, 900); TemporaryTick(io, cycle, 2709);
            Assert(io.Actions == 1, "Repeat interval began before the targeted input cycle completed.");
            TemporaryTick(io, cycle, 2710); Assert(io.Actions == 2, "Targeted repeat did not resume after its full interval.");

            var untargeted = TemporarySettings(); untargeted.ClickTargetAfterKey = false;
            io = new FakeTemporary { HotkeyDelay = 400 }; cycle = new VanillaTemporaryCycle(io, untargeted, TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); TemporaryTick(io, cycle, 1899);
            Assert(io.Actions == 1, "Untargeted repeat interval began before hotkey delivery completed.");
            TemporaryTick(io, cycle, 1900); Assert(io.Actions == 2, "Untargeted repeat did not resume after its full interval.");

            io = new FakeTemporary { Sp = 5, MoveDelay = 500 }; cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            TemporaryTick(io, cycle, 0); TemporaryTick(io, cycle, 4499);
            Assert(io.Moves == 1, "Ground click retried before the completed click's full movement window.");
            TemporaryTick(io, cycle, 4500); TemporaryTick(io, cycle, 8999);
            Assert(io.Moves == 2 && io.Sits == 0, "Movement retry timing changed or unverified movement allowed sit.");
            Reject(() => TemporaryTick(io, cycle, 9000));
        }
        private static void TemporaryCaptureGeometry()
        {
            var legacy = TemporarySettings(); legacy.Validate();
            Assert(legacy.TargetClientWidth == 0 && legacy.TargetClientHeight == 0, "Legacy captured-point geometry was invented.");
            var captured = TemporarySettings(); captured.TargetClientWidth = 1024; captured.TargetClientHeight = 768;
            var clone = captured.Clone();
            Assert(clone.TargetClientWidth == 1024 && clone.TargetClientHeight == 768, "Captured geometry did not survive serialization.");
            foreach (var size in new[] { new Size(1024, 0), new Size(0, 768), new Size(319, 240), new Size(320, 239), new Size(16001, 1000), new Size(-1, -1) })
            {
                captured.TargetClientWidth = size.Width; captured.TargetClientHeight = size.Height;
                try { captured.Validate(); }
                catch (ArgumentException) { continue; }
                throw new Exception("Invalid captured target client geometry was accepted: " + size);
            }
        }
        private static void ObservedCards()
        {
            foreach (int columns in new[] { 3, 5 })
            foreach (double scale in new[] { 1.0, 1.5 })
            using (var original = new Bitmap(700, 620))
            {
                using (var graphics = Graphics.FromImage(original))
                using (var font = new Font("Tahoma", 16, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    graphics.Clear(Color.FromArgb(90, 95, 90));
                    for (int i = 0; i < 15; i++)
                    {
                        int x = 30 + i % columns * 90, y = 30 + i / columns * 100;
                        graphics.FillRectangle(Brushes.White, x, y, 80, 90);
                        if (i != 14) graphics.FillRectangle(Brushes.DarkSlateGray, x + 32, y + 25, 16, 36);
                        if (i == 7) using (var pen = new Pen(Color.FromArgb(0, 220, 220), 4)) graphics.DrawRectangle(pen, x - 2, y - 2, 84, 94);
                    }
                    int left = 30 + columns * 90 + 8, top = 30 + (15 / columns) * 100 - 40;
                    graphics.FillRectangle(Brushes.White, left, top, 145, 30);
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    graphics.DrawString("Character List", font, Brushes.Black, left + 6, top + 6, StringFormat.GenericTypographic);
                }
                using (var image = new Bitmap((int)(700 * scale), (int)(620 * scale)))
                {
                    using (var graphics = Graphics.FromImage(image)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(original, 0, 0, image.Width, image.Height); }
                    VanillaCharacterSelectionObservation observed; string evidence;
                    Assert(VanillaObservedCharacterGrid.TryDetect(image, out observed, out evidence), "Observed-skin synthetic fixture rejected: " + evidence);
                    Assert(observed.Columns == columns && observed.Selected == 7 && observed.Occupied[7] && !observed.Occupied[14], "Wrong selected/empty observed slot.");
                }
            }
        }
        private static void CursorParkingGeometry()
        {
            foreach (Size size in new[] { new Size(200, 120), new Size(320, 240), new Size(640, 480),
                new Size(1024, 768), new Size(1920, 1080), new Size(3840, 2160) })
            {
                var bounds = new Rectangle(Point.Empty, size);
                foreach (Rectangle control in new[]
                {
                    new Rectangle(size.Width / 2 - 30, size.Height / 2 - 8, 60, 16),
                    new Rectangle(0, 0, 40, 20), new Rectangle(size.Width - 40, 0, 40, 20),
                    new Rectangle(0, size.Height - 20, 40, 20), new Rectangle(size.Width - 40, size.Height - 20, 40, 20),
                    new Rectangle(size.Width / 2, size.Height / 2, 1, 1)
                })
                {
                    Point point = VanillaForegroundInput.SelectCursorParkingPoint(size, control);
                    Assert(bounds.Contains(point), "Cursor parking escaped the client at " + size + ".");
                    Assert(!Rectangle.Inflate(control, VanillaForegroundInput.CursorParkingPadding,
                        VanillaForegroundInput.CursorParkingPadding).Contains(point), "Cursor still obscures the checked control.");
                    Assert(point == VanillaForegroundInput.SelectCursorParkingPoint(size, control), "Cursor parking was nondeterministic.");
                }
            }
        }

        private static void CursorParkingExclusion()
        {
            foreach (Size size in new[] { new Size(320, 240), new Size(1024, 768), new Size(1920, 1080) })
            {
                Point point = VanillaForegroundInput.SelectCursorParkingPoint(size, Rectangle.Empty);
                Rectangle center = new Rectangle(size.Width / 4, size.Height / 4, size.Width / 2, size.Height / 2);
                Assert(!Rectangle.Inflate(center, VanillaForegroundInput.CursorParkingPadding,
                    VanillaForegroundInput.CursorParkingPadding).Contains(point), "Cursor still covers the central form.");
            }
            Reject(() => VanillaForegroundInput.SelectCursorParkingPoint(Size.Empty, Rectangle.Empty));
            Reject(() => VanillaForegroundInput.SelectCursorParkingPoint(new Size(1024, 768), new Rectangle(-1, 0, 20, 20)));
            Reject(() => VanillaForegroundInput.SelectCursorParkingPoint(new Size(1024, 768), new Rectangle(1020, 700, 20, 20)));
            Reject(() => VanillaForegroundInput.SelectCursorParkingPoint(new Size(100, 100), new Rectangle(0, 0, 100, 100)));
        }

        private static void ClickParkingSequence()
        {
            var events = new List<string>();
            VanillaForegroundInput.DispatchGuardedClick(() => events.Add("verify"),
                up => events.Add(up ? "up" : "down"), ms => events.Add("wait" + ms), () => events.Add("park"));
            events.Add("capture");
            Assert(string.Join(",", events) == "verify,down,wait110,up,park,wait130,capture",
                "A visual check or cursor move happened before releasing the mouse: " + string.Join(",", events));
        }

        private static void ClickParkingFailure()
        {
            var events = new List<string>();
            Reject(() => VanillaForegroundInput.DispatchGuardedClick(() => { throw new OperationCanceledException(); },
                up => events.Add(up ? "up" : "down"), _ => { }, () => events.Add("park")));
            Assert(events.Count == 0, "Cancellation before the click still sent mouse input.");
            Reject(() => VanillaForegroundInput.DispatchGuardedClick(() => { },
                up => events.Add(up ? "up" : "down"), _ => { throw new OperationCanceledException(); }, () => events.Add("park")));
            Assert(string.Join(",", events) == "down,up", "Cancellation while pressed did not release only the owned button.");
            events.Clear();
            Reject(() => VanillaForegroundInput.DispatchGuardedClick(() => { },
                up => { events.Add(up ? "up" : "down"); throw new InvalidOperationException("Mouse input rejected."); },
                _ => { }, () => events.Add("park")));
            Assert(string.Join(",", events) == "down", "A rejected press still moved the cursor or released an unowned button.");
            events.Clear();
            Reject(() => VanillaForegroundInput.DispatchGuardedClick(() => { }, up => events.Add(up ? "up" : "down"),
                ms => events.Add("wait" + ms), () => { throw new InvalidOperationException("Foreground ownership lost before parking."); }));
            Assert(string.Join(",", events) == "down,wait110,up", "Lost foreground continued into a post-click visual check.");
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> callback;
            internal Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) { this.callback = callback; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { return Task.FromResult(callback(request)); }
        }
        private static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; } catch (IOException) { return; } catch (InvalidOperationException) { return; } catch (OperationCanceledException) { return; }
            throw new Exception("Unsafe operation was accepted.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Test(string name, Action action)
        { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); } }
    }
}
