using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// Ordinary compatibility mouse messages with the real cursor at the verified
    /// target. This is the stock AHK compatibility mechanism, with ownership and
    /// cancellation guards. A rejected message never triggers an alternate input path.
    /// </summary>
    internal static class VanillaVerifiedMouse
    {
        private static readonly object Gate = new object();
        internal const int GrabHoldMs = 120;
        [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RectNative rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref PointNative point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PointNative point);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        private static void DragButton(bool up)
        {
            var inputs = new[] { new Input { Value = new InputUnion { Mouse = new MouseInput { Flags = up ? 4U : 2U } } } };
            if (SendInput(1, inputs, Marshal.SizeOf(typeof(Input))) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the captured drag button; no alternate input path used.");
        }

        internal static void CompatibilityClickFromProof(this VanillaForegroundInput input, Rectangle control, VanillaVisualInputProof proof, System.Action beforePress = null)
        {
            lock (Gate)
            {
                Verify(input, proof);
                Point center = proof.ControlCenter(control);
                PointNative target = Screen(proof, center), previous;
                bool restore = GetCursorPos(out previous), held = false;
                try
                {
                    Move(target); Pause(120, () => Verify(input, proof));
                    RequireCursor(proof, target);
                    beforePress?.Invoke();
                    Verify(input, proof); RequireCursor(proof, target);
                    Post(proof.Window, 0x0200, false, center);
                    Post(proof.Window, 0x0201, true, center); held = true;
                    Pause(110, () => { Verify(input, proof); RequireCursor(proof, target); });
                    Release(proof.Window, center); held = false;
                    Pause(80, () => { Verify(input, proof); RequireCursor(proof, target); });
                }
                finally
                {
                    try { if (held) Release(proof.Window, center); }
                    finally { RestoreIfUnchanged(restore, target, previous); }
                }
                VanillaDebugLog.Write("INPUT", "PID=" + proof.ProcessId + " compatibility click delivered at verified captured control " + control + ".");
            }
        }

        internal static void DragFromProof(this VanillaForegroundInput input, Rectangle source, Rectangle destination, VanillaVisualInputProof proof)
        {
            lock (Gate)
            {
                Verify(input, proof);
                Point from = proof.ControlCenter(source), to = proof.ControlCenter(destination);
                PointNative start = Screen(proof, from), end = Screen(proof, to), current = start, previous;
                bool restore = GetCursorPos(out previous), held = false;
                try
                {
                    Move(start); Pause(VanillaForegroundInput.DeliberateDragStartHoldMs, () => Verify(input, proof));
                    RequireCursor(proof, start);
                    DragButton(false); held = true;
                    // The game needs time to acquire the item AFTER mouse-down. The
                    // previous implementation waited before down but moved immediately.
                    Pause(GrabHoldMs, () => { Verify(input, proof); RequireCursor(proof, start); });
                    for (int step = 1; step <= VanillaForegroundInput.DeliberateDragMoveSteps; step++)
                    {
                        Verify(input, proof); RequireCursor(proof, current);
                        Point client = new Point(from.X + (to.X - from.X) * step / VanillaForegroundInput.DeliberateDragMoveSteps,
                            from.Y + (to.Y - from.Y) * step / VanillaForegroundInput.DeliberateDragMoveSteps);
                        current = Screen(proof, client); Move(current);
                        Pause(VanillaForegroundInput.DeliberateDragStepDelayMs, () => { Verify(input, proof); RequireCursor(proof, current); });
                    }
                    Pause(VanillaForegroundInput.DeliberateDragDestinationHoldMs, () => { Verify(input, proof); RequireCursor(proof, end); });
                    DragButton(true); held = false;
                    Pause(VanillaForegroundInput.DeliberateDragPostReleaseMs, () => Verify(input, proof));
                }
                finally
                {
                    try { if (held) DragButton(true); }
                    finally { RestoreIfUnchanged(restore, current, previous); }
                }
                VanillaDebugLog.Write("INPUT", "PID=" + proof.ProcessId + " verified captured drag: grabHoldMs=" + GrabHoldMs
                    + ", travelSteps=" + VanillaForegroundInput.DeliberateDragMoveSteps + ", travelStepMs=" + VanillaForegroundInput.DeliberateDragStepDelayMs + ".");
            }
        }

        internal static void Verify(VanillaForegroundInput input, VanillaVisualInputProof proof)
        {
            if (input.CancellationRequested != null && input.CancellationRequested()) throw new OperationCanceledException("Mouse operation cancelled.");
            if (proof == null) throw new InvalidOperationException("Captured mouse target proof is missing.");
            IntPtr window = input.Window;
            uint pid; RectNative rect; var origin = new PointNative();
            if (GetWindowThreadProcessId(window, out pid) == 0 || !GetClientRect(window, out rect) || !ClientToScreen(window, ref origin))
                throw new InvalidOperationException("Mouse target ownership or geometry is unavailable.");
            proof.RequireMatches((int)pid, window, GetForegroundWindow(), new Size(rect.Right - rect.Left, rect.Bottom - rect.Top),
                new Point(origin.X, origin.Y), Stopwatch.GetTimestamp());
        }
        private static void RequireCursor(VanillaVisualInputProof proof, PointNative expected)
        {
            PointNative actual;
            if (!GetCursorPos(out actual) || actual.X != expected.X || actual.Y != expected.Y)
                throw new InvalidOperationException("Human cursor movement interrupted the captured mouse operation.");
            IntPtr hit = WindowFromPoint(actual);
            if (hit != proof.Window && !IsChild(proof.Window, hit)) throw new InvalidOperationException("Mouse target is covered by another window.");
        }
        private static PointNative Screen(VanillaVisualInputProof proof, Point point)
        { return new PointNative { X = proof.ClientOrigin.X + point.X, Y = proof.ClientOrigin.Y + point.Y }; }
        private static void Move(PointNative point)
        { if (!SetCursorPos(point.X, point.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the verified cursor position."); }
        private static void Post(IntPtr window, uint message, bool held, Point point)
        {
            if (!PostMessage(window, message, held ? new IntPtr(1) : IntPtr.Zero, new IntPtr((point.Y << 16) | (point.X & 65535))))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected compatibility mouse input; no alternate path used.");
        }
        private static void Release(IntPtr window, Point point)
        {
            // Releasing only the button this operation pressed is permitted even after
            // cancellation. Never redirect this release to a replacement window.
            if (IsWindow(window)) Post(window, 0x0202, false, point);
        }
        private static void RestoreIfUnchanged(bool restore, PointNative expected, PointNative previous)
        {
            PointNative actual;
            if (restore && GetCursorPos(out actual) && actual.X == expected.X && actual.Y == expected.Y) SetCursorPos(previous.X, previous.Y);
        }
        private static void Pause(int milliseconds, System.Action verify)
        {
            for (int elapsed = 0; elapsed < milliseconds; elapsed += 25)
            { verify(); Thread.Sleep(Math.Min(25, milliseconds - elapsed)); }
            verify();
        }
    }
}
