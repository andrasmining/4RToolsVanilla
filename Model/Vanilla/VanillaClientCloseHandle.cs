using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace _4RTools.Model.Vanilla
{
    // Ordinary Windows process lifecycle control. No VM_READ/WRITE, injection,
    // token changes or alternate access path after denial. One handle pins identity.
    internal sealed class VanillaClientCloseHandle : IDisposable
    {
        internal const uint RequiredAccess = 0x00100000 | 0x00001000 | 0x00000001; // synchronize, limited query, terminate
        private readonly SafeProcessHandle handle;
        private readonly int pid;
        private readonly IntPtr window;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint rights, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);

        internal VanillaClientCloseHandle(int pid, DateTime expectedStart, bool resolveWindow = true)
        {
            this.pid = pid;
            handle = OpenProcess(RequiredAccess, false, pid);
            if (handle == null || handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); handle?.Dispose();
                throw new Win32Exception(error, "Client close access denied/unavailable; no alternate access attempted.");
            }
            try
            {
                long created, exited, kernel, user;
                if (!GetProcessTimes(handle, out created, out exited, out kernel, out user))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Client creation time unavailable.");
                if (DateTime.FromFileTimeUtc(created) != expectedStart)
                    throw new InvalidOperationException("Client PID was replaced; no close sent.");
                if (resolveWindow)
                    using (var process = Process.GetProcessById(pid)) window = process.MainWindowHandle;
            }
            catch { handle.Dispose(); throw; }
        }
        internal bool HasExited()
        {
            uint state = WaitForSingleObject(handle, 0);
            if (state == 0) return true;
            if (state == 258) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Client exit cannot be verified.");
        }
        internal void CloseWindow()
        {
            if (HasExited() || window == IntPtr.Zero) return;
            uint owner;
            if (GetWindowThreadProcessId(window, out owner) == 0 || owner != (uint)pid)
                throw new InvalidOperationException("Client window ownership changed; no close sent.");
            if (!PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Client rejected ordinary window close.");
        }
        internal void Terminate()
        {
            if (!HasExited() && !TerminateProcess(handle, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Client termination failed; no replacement launched.");
        }
        public void Dispose() { handle.Dispose(); }
    }
}
