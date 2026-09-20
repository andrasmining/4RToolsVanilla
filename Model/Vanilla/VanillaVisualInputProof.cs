using System;
using System.Diagnostics;
using System.Drawing;

namespace _4RTools.Model.Vanilla
{
    /// <summary>A captured frame authorizes input only on that same, still focused client surface.</summary>
    internal sealed class VanillaVisualInputProof
    {
        internal const int MaximumAgeMs = 5000;
        internal readonly int ProcessId;
        internal readonly IntPtr Window;
        internal readonly Size ClientSize;
        internal readonly Point ClientOrigin;
        internal readonly long CapturedAt;

        internal VanillaVisualInputProof(int processId, IntPtr window, Size size, Point origin, long capturedAt)
        { ProcessId = processId; Window = window; ClientSize = size; ClientOrigin = origin; CapturedAt = capturedAt; }

        internal void RequireMatches(int processId, IntPtr window, IntPtr foreground, Size size, Point origin, long now)
        {
            double elapsedMs = (now - CapturedAt) * 1000.0 / Stopwatch.Frequency;
            if (Window == IntPtr.Zero || ProcessId != processId || Window != window || foreground != Window
                || ClientSize != size || ClientOrigin != origin || elapsedMs < 0 || elapsedMs > MaximumAgeMs)
                throw new InvalidOperationException("Captured UI proof expired or its window/focus/geometry changed; input withheld. Re-observation is required.");
        }

        internal Point ControlCenter(Rectangle control)
        {
            if (control.Width <= 0 || control.Height <= 0 || !new Rectangle(Point.Empty, ClientSize).Contains(control))
                throw new InvalidOperationException("Detected control is outside the captured client; click withheld.");
            return new Point(control.Left + control.Width / 2, control.Top + control.Height / 2);
        }
    }
}
