using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaServiceObservation
    {
        internal string Name;
        internal Rectangle Bounds;
        internal Size ImageSize;
        internal bool Highlighted;

        internal bool Matches(VanillaServiceObservation other)
        {
            return other != null && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && ImageSize == other.ImageSize && Math.Abs(Bounds.X - other.Bounds.X) <= 3
                && Math.Abs(Bounds.Y - other.Bounds.Y) <= 3 && Math.Abs(Bounds.Width - other.Bounds.Width) <= 3
                && Math.Abs(Bounds.Height - other.Bounds.Height) <= 3;
        }

        internal bool Valid => !string.IsNullOrEmpty(Name) && Bounds.Width > 0 && Bounds.Height > 0
            && new Rectangle(Point.Empty, ImageSize).Contains(Bounds);
    }

    // Both production and TESTS use the same select -> observe highlight -> submit transaction.
    internal static class VanillaServiceSelection
    {
        internal static void Select(string expectedName, Func<VanillaServiceObservation> observe,
            Action<VanillaServiceObservation> click, Action submit, Action<int> pause,
            Func<bool> cancelled, int timeoutMs = 45000)
        {
            var watch = Stopwatch.StartNew();
            VanillaServiceObservation previous = null, clickedRow = null;
            int stable = 0;
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                CheckCancelled(cancelled);
                var current = observe();
                CheckCancelled(cancelled);
                bool valid = current != null && current.Valid && current.Name == expectedName;
                if (clickedRow != null && valid && !current.Matches(clickedRow))
                    throw new InvalidOperationException("Service layout changed after selection; submission cancelled.");
                bool acceptable = valid && (clickedRow == null || current.Highlighted);
                stable = acceptable ? (current.Matches(previous) ? stable + 1 : 1) : 0;
                previous = acceptable ? current : null;
                if (stable >= 2)
                {
                    CheckCancelled(cancelled);
                    if (clickedRow == null)
                    {
                        click(current);
                        clickedRow = current;
                        stable = 0;
                        previous = null;
                    }
                    else
                    {
                        submit();
                        return;
                    }
                }
                pause(150);
            }
            throw new InvalidOperationException(clickedRow == null
                ? "Service '" + expectedName + "' was not uniquely recognized in two stable captures; no selection was sent."
                : "Service '" + expectedName + "' highlight was not verified in two fresh captures; Enter was withheld.");
        }

        private static void CheckCancelled(Func<bool> cancelled)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException("Service selection cancelled.");
        }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private void SelectNamedService(VanillaForegroundInput input, VanillaProxyRoute? proxyRoute, string logPrefix)
        {
            string name = proxyRoute.HasValue ? VanillaProxyPattern.NameForRoute(proxyRoute.Value) : "Vanilla MMO";
            if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Configured proxy is unknown; no input sent.");
            string lastEvidence = null;
            VanillaServiceSelection.Select(name, () =>
            {
                using (Bitmap image = input.CaptureClientBitmap())
                {
                    string evidence;
                    if (proxyRoute.HasValue)
                    {
                        VanillaProxyLayout layout;
                        VanillaServiceRow row;
                        bool detected = VanillaProxyPattern.TryDetect(image, out layout, out evidence);
                        TraceServiceRecognition(logPrefix, evidence, ref lastEvidence);
                        if (!detected || !layout.TryFind(proxyRoute.Value, out row)) return null;
                        return new VanillaServiceObservation { Name = row.Name, Bounds = row.Bounds,
                            ImageSize = image.Size, Highlighted = row.IsHighlighted };
                    }
                    VanillaServerLayout server;
                    bool serverDetected = VanillaAuthPattern.TryDetectServerDialog(image, out server, out evidence);
                    TraceServiceRecognition(logPrefix, evidence, ref lastEvidence);
                    if (!serverDetected) return null;
                    return new VanillaServiceObservation { Name = server.ServerName, Bounds = server.ServerRow,
                        ImageSize = image.Size, Highlighted = server.IsHighlighted };
                }
            }, row => input.ClickNormalized((row.Bounds.Left + row.Bounds.Width / 2.0) / row.ImageSize.Width,
                (row.Bounds.Top + row.Bounds.Height / 2.0) / row.ImageSize.Height),
                () => input.Press(Keys.Enter), milliseconds =>
                {
                    if (input.CancellationRequested != null && input.CancellationRequested())
                        throw new OperationCanceledException("Service selection cancelled.");
                    Thread.Sleep(milliseconds);
                }, input.CancellationRequested);
            Log(logPrefix + "service '" + name + "' selected by name and confirmed by two fresh highlight captures before Enter.");
        }

        private static void TraceServiceRecognition(string prefix, string evidence, ref string last)
        {
            if (evidence == last) return;
            last = evidence;
            VanillaDebugLog.Write("SERVICE", prefix + evidence);
        }
    }
}
