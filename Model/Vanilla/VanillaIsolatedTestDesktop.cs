using System;
using System.Runtime.InteropServices;
using System.Text;

namespace _4RTools.Model.Vanilla
{
    // Test entry points may create real controls only on a private, non-input desktop.
    // This guard never changes desktops or sends input. Normal product startup does not call it.
    internal static class VanillaIsolatedTestDesktop
    {
        internal static void AssertCurrent()
        {
            string expected = Environment.GetEnvironmentVariable("FOURRTOOLS_ISOLATED_DESKTOP");
            if (string.IsNullOrEmpty(expected) || !expected.StartsWith("4RTools-Test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Run this inert test through scripts/test-isolated.ps1.");
            string current = Name(GetThreadDesktop(GetCurrentThreadId()));
            IntPtr input = OpenInputDesktop(0, false, 1 /* DESKTOP_READOBJECTS */);
            if (input == IntPtr.Zero) throw new InvalidOperationException("Cannot establish input-desktop separation.");
            try
            {
                if (!string.Equals(current, expected, StringComparison.Ordinal) ||
                    string.Equals(current, Name(input), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Inert tests must run on their own non-input desktop.");
            }
            finally { CloseDesktop(input); }
        }

        private static string Name(IntPtr desktop)
        {
            var name = new StringBuilder(512);
            uint needed;
            if (desktop == IntPtr.Zero || !GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed))
                throw new InvalidOperationException("Cannot read desktop identity.");
            return name.ToString();
        }

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out uint needed);
    }
}
