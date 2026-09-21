using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// A test runner, not a product launcher. No SwitchDesktop/SetForegroundWindow/input API exists here.
public static class IsolatedDesktopProcess
{
    public static int Run(string executable, string[] arguments, string workingDirectory,
        string output, string error, string dataRoot, int timeoutMilliseconds)
    {
        string desktopName = "4RTools-Test-" + Guid.NewGuid().ToString("N");
        IntPtr desktop = IntPtr.Zero, job = IntPtr.Zero, environment = IntPtr.Zero;
        IntPtr stdout = IntPtr.Zero, stderr = IntPtr.Zero, stdin = IntPtr.Zero;
        PROCESS_INFORMATION process = new PROCESS_INFORMATION();
        try
        {
            // Desktop access deliberately excludes DESKTOP_SWITCHDESKTOP.
            desktop = CreateDesktop(desktopName, null, IntPtr.Zero, 0, 0x00C7, IntPtr.Zero);
            Check(desktop != IntPtr.Zero, "Create private test desktop");
            job = CreateJobObject(IntPtr.Zero, null);
            Check(job != IntPtr.Zero, "Create test process job");
            var limits = new JOB_LIMITS();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JOB_LIMITS))), "Set test job cleanup");
            stdout = OpenLog(output, false); stderr = OpenLog(error, false); stdin = OpenLog("NUL", true);
            var start = new STARTUPINFO();
            start.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            start.lpDesktop = desktopName;
            start.dwFlags = 0x100; // STARTF_USESTDHANDLES
            start.hStdOutput = stdout; start.hStdError = stderr; start.hStdInput = stdin;
            var variables = new SortedList(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables()) variables[item.Key] = item.Value;
            variables["FOURRTOOLS_ISOLATED_DESKTOP"] = desktopName;
            variables["FOURRTOOLS_DATA_ROOT"] = dataRoot;
            var block = new StringBuilder();
            foreach (DictionaryEntry item in variables) block.Append(item.Key).Append('=').Append(item.Value).Append('\0');
            block.Append('\0');
            environment = Marshal.StringToHGlobalUni(block.ToString());
            var command = new StringBuilder(Quote(executable));
            foreach (string argument in arguments) command.Append(' ').Append(Quote(argument));
            Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                0x08000404 /* CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED */,
                environment, workingDirectory, ref start, out process), "Create inert test process");
            // Never let the child execute outside the lifetime-bounded job.
            Check(AssignProcessToJobObject(job, process.hProcess), "Assign inert test process to cleanup job");
            Check(ResumeThread(process.hThread) != uint.MaxValue, "Start inert test process");
            uint waited = WaitForSingleObject(process.hProcess, (uint)timeoutMilliseconds);
            if (waited == 258) throw new TimeoutException("Isolated test timed out; its complete child process tree was terminated. See " + output);
            Check(waited == 0, "Wait for inert test completion");
            uint exitCode;
            Check(GetExitCodeProcess(process.hProcess, out exitCode), "Read inert test exit code");
            return unchecked((int)exitCode);
        }
        finally
        {
            // Explicitly kill on exceptional pre-assignment exits too; no user process is targeted.
            if (process.hProcess != IntPtr.Zero)
            {
                uint code;
                if (GetExitCodeProcess(process.hProcess, out code) && code == 259) TerminateProcess(process.hProcess, 1);
            }
            if (job != IntPtr.Zero) CloseHandle(job); // kills all test-owned descendants
            if (process.hProcess != IntPtr.Zero) WaitForSingleObject(process.hProcess, 5000);
            Close(process.hThread); Close(process.hProcess);
            Close(stdin); Close(stdout); Close(stderr);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        }
    }

    private static IntPtr OpenLog(string path, bool input)
    {
        var security = new SECURITY_ATTRIBUTES();
        security.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)); security.bInheritHandle = true;
        IntPtr result = CreateFile(path, input ? 0x80000000u : 0x40000000u, 3, ref security, input ? 3u : 2u, 0x80, IntPtr.Zero);
        Check(result != new IntPtr(-1), "Open inert test log"); return result;
    }
    private static void Close(IntPtr value) { if (value != IntPtr.Zero && value != new IntPtr(-1)) CloseHandle(value); }
    private static void Check(bool success, string operation) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BASIC_LIMITS
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JOB_LIMITS
    {
        public BASIC_LIMITS Basic; public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, string device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref JOB_LIMITS limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string path, uint access, uint share, ref SECURITY_ATTRIBUTES security, uint creation, uint flags, IntPtr template);
}
