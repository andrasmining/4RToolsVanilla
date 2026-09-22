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
            Test("Public fallback authenticates only the original API and never its CDN", PublicFallback);
            Test("Update success preserves unrelated user files and previous managed files", UpdateSuccess);
            Test("Partial copy failure restores old bytes and removes newly added files", UpdateCopyRollback);
            Test("Application start failure rolls back the managed installation", UpdateStartRollback);
            Test("Manifest rejects missing executable, extras, duplicates and user data", ManifestSafety);
            Test("Observed stack quantities never exceed capacity or the offered stack", QuantityBounds);
            Test("Production quantity recognition reads selected numeric fields", QuantityVision);
            Test("Temporary pending cast finishes before move and sit", TemporaryRestSequence);
            Test("Temporary sit requires movement and fails on the bounded deadline", TemporaryMoveTimeout);
            Test("Temporary pending input rejects death, session change and cancellation", TemporaryIdentity);
            Test("Observed Vanilla cards need unique selection and distinguish an empty slot", ObservedCards);
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
                if (calls == 1) { Assert(request.Headers.Authorization == null, "Not public first."); return new HttpResponseMessage(HttpStatusCode.NotFound); }
                if (calls == 2)
                {
                    Assert(request.Headers.Authorization?.Parameter == "synthetic_test_token_not_real", "Fallback API was not authenticated.");
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/test.zip?signature=synthetic");
                    return response;
                }
                Assert(request.Headers.Authorization == null && !request.Headers.Contains("X-GitHub-Api-Version"), "Credential escaped onto CDN.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
            })))
            {
                var client = new VanillaPrivateReleaseClient(http, () => { credentials++; return Task.FromResult("synthetic_test_token_not_real"); }, true);
                Assert(client.ReadAssetAsync(VanillaPrivateReleaseClient.AssetUrl(123), 1024).GetAwaiter().GetResult().Length == 1, "Fallback failed.");
                Assert(calls == 3 && credentials == 1, "Fallback is not bounded.");
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
            internal int Actions, Clicks, Moves, Sits, Releases, X = 100;
            internal decimal Sp = 70;
            internal Guid Session = Guid.NewGuid();
            internal bool Alive = true, Cancelled;
            private long frame;
            public VanillaTemporarySample Read() { return new VanillaTemporarySample { Identity = "test", Map = "test-map", Session = Session,
                At = DateTimeOffset.UtcNow.AddTicks(++frame), X = X, Y = 100, Sp = Sp, Alive = Alive }; }
            public bool Acquire() { return true; }
            public void Release() { Releases++; }
            public void CheckCancelled() { if (Cancelled) throw new OperationCanceledException(); }
            public void PrepareTarget() { }
            public void ActionHotkey() { Actions++; }
            public void TargetClick() { Clicks++; }
            public void MoveBeforeSit() { Moves++; }
            public void SitStand() { Sits++; }
        }
        private static VanillaTemporaryActionSettings TemporarySettings()
        { return new VanillaTemporaryActionSettings { ActionKey = (int)Keys.F1, ClickTargetAfterKey = true, TargetClickDelayMs = 180, RestMoveCaptured = true }; }
        private static void TemporaryRestSequence()
        {
            var io = new FakeTemporary(); var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            cycle.Tick(TimeSpan.Zero); io.Sp = 5;
            cycle.Tick(TimeSpan.FromMilliseconds(200));
            Assert(io.Actions == 1 && io.Clicks == 1 && io.Moves == 0 && io.Sits == 0, "Low SP interrupted a pending cast into Sit.");
            cycle.Tick(TimeSpan.FromMilliseconds(850)); Assert(io.Moves == 1 && io.Sits == 0, "No verified movement required.");
            io.X++; cycle.Tick(TimeSpan.FromMilliseconds(1000));
            cycle.Tick(TimeSpan.FromMilliseconds(1500)); Assert(io.Sits == 1 && cycle.Phase == VanillaTemporaryPhase.Resting, "Verified walk did not precede sit.");
            io.Sp = 85; cycle.Tick(TimeSpan.FromMilliseconds(2000)); Assert(io.Sits == 2, "Standing not requested after SP recovery.");
            cycle.Tick(TimeSpan.FromMilliseconds(2300)); Assert(io.Actions == 1, "Cast sent before standing settled.");
            cycle.Tick(TimeSpan.FromMilliseconds(2700)); Assert(io.Actions == 2, "Action did not resume.");
        }
        private static void TemporaryMoveTimeout()
        {
            var io = new FakeTemporary { Sp = 5 }; var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
            cycle.Tick(TimeSpan.Zero); cycle.Tick(TimeSpan.FromSeconds(4));
            Reject(() => cycle.Tick(TimeSpan.FromSeconds(8)));
            Assert(io.Sits == 0 && io.Moves == 2 && io.Actions == 0, "Unverified movement led to sitting/casting or unbounded retries.");
        }
        private static void TemporaryIdentity()
        {
            for (int mode = 0; mode < 3; mode++)
            {
                var io = new FakeTemporary(); var cycle = new VanillaTemporaryCycle(io, TemporarySettings(), TimeSpan.Zero);
                cycle.Tick(TimeSpan.Zero);
                if (mode == 0) io.Alive = false; else if (mode == 1) io.Session = Guid.NewGuid(); else io.Cancelled = true;
                Reject(() => cycle.Tick(TimeSpan.FromSeconds(1)));
                Assert(io.Clicks == 0 && io.Sits == 0, "Invalid identity/death/STOP allowed a pending click.");
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
