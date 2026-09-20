using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace _4RTools.Model.Vanilla
{
    // A missing GAME START alone is not evidence of an update or a stalled update.
    internal sealed class VanillaLauncherPatchFrame
    {
        internal string Signature { get; private set; }
        internal VanillaLauncherPatchFrame(string signature) { Signature = signature; }
        internal static VanillaLauncherPatchFrame Read(Bitmap image)
        {
            if (image == null || image.Width < 200 || image.Height < 120 || image.Width > 4096 || image.Height > 2160) return null;
            double x, y; string ignored;
            if (VanillaPatcherLauncher.TryFindGameStart(image, out x, out y, out ignored)) return null;
            int width = image.Width, height = image.Height, left = width, right = -1, top = height, bottom = -1;
            // Detect the thin progress bar in the white footer, not a click location.
            for (int py = height * 82 / 100; py < height * 99 / 100; py++)
            {
                int start = -1;
                for (int px = 0; px <= width; px++)
                {
                    bool yellow = px < width && Yellow(image.GetPixel(px, py));
                    if (yellow && start < 0) start = px;
                    if (yellow || start < 0) continue;
                    if (start >= width / 100 && start <= width * 12 / 100 && px - start >= width * 6 / 100)
                    { left = Math.Min(left, start); right = Math.Max(right, px - 1); top = Math.Min(top, py); bottom = Math.Max(bottom, py); }
                    start = -1;
                }
            }
            int barHeight = bottom - top + 1;
            if (right < left || barHeight < Math.Max(2, height / 200) || barHeight > height * .07) return null;
            int white = 0, blue = 0;
            for (int i = 1; i < 32; i++)
            {
                Color footer = image.GetPixel(i * width / 32, Math.Max(0, top - Math.Max(2, height / 60)));
                if (footer.R > 215 && footer.G > 215 && footer.B > 215) white++;
                Color header = image.GetPixel(i * width / 32, height / 8);
                if (header.B > 145 && header.G > 115 && header.B > header.R + 15) blue++;
            }
            if (white < 23 || blue < 8) return null;
            // Observe filename/status and progress, excluding animated artwork.
            // Coarse color classes tolerate small compression/capture color variation.
            int bandTop = Math.Max(0, top - height / 10);
            int bandBottom = Math.Min(height - 1, bottom + Math.Max(2, height / 100));
            var signature = new StringBuilder(2100);
            signature.Append(width).Append('x').Append(height).Append(':');
            for (int gy = 0; gy < 16; gy++) for (int gx = 0; gx < 128; gx++)
            {
                Color c = image.GetPixel(gx * (width - 1) / 127, bandTop + gy * (bandBottom - bandTop) / 15);
                signature.Append(Yellow(c) ? 'Y' : c.R < 160 && c.G < 160 && c.B < 160 ? 'D'
                    : c.R > 215 && c.G > 215 && c.B > 215 ? 'W' : 'G');
            }
            return new VanillaLauncherPatchFrame(signature.ToString());
        }
        private static bool Yellow(Color c)
        { return c.R >= 220 && c.G >= 150 && c.G <= 235 && c.B <= 120 && c.R - c.B >= 105; }
    }

    internal sealed class VanillaLauncherPatchWatch
    {
        internal const int StallMs = 60000, MaximumSampleGapMs = 5000;
        private string key;
        private TimeSpan since, previous;
        private int samples;
        internal void Reset() { key = null; samples = 0; }
        internal bool Observe(int pid, IntPtr window, VanillaLauncherPatchFrame frame, TimeSpan now, long birth = 0)
        {
            if (pid <= 0 || window == IntPtr.Zero || frame == null) { Reset(); return false; }
            string current = pid + ":" + birth + ":" + window.ToInt64() + ":" + frame.Signature;
            if (current != key || now <= previous || (now - previous).TotalMilliseconds > MaximumSampleGapMs)
            { key = current; since = now; samples = 1; }
            else samples++;
            previous = now;
            return samples >= 10 && (now - since).TotalMilliseconds >= StallMs;
        }
    }

    internal sealed class VanillaLauncherUpdateProcess
    {
        internal const uint MetadataAccess = 0x1000; // PROCESS_QUERY_LIMITED_INFORMATION; no VM access
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        internal int Pid { get; private set; }
        internal DateTime StartedUtc { get; private set; }
        internal string Executable { get; private set; }
        internal bool Game { get; private set; }
        internal VanillaLauncherUpdateProcess(int pid, DateTime startedUtc, string executable, bool game)
        { Pid = pid; StartedUtc = startedUtc; Executable = executable; Game = game; }
        internal static VanillaLauncherUpdateProcess Read(int pid)
        {
            using (var handle = OpenProcess(MetadataAccess, false, pid))
            {
                if (handle == null || handle.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Update identity unavailable; no alternate access attempted.");
                var path = new StringBuilder(32768); int size = path.Capacity;
                long created, exited, kernel, user;
                if (!QueryFullProcessImageName(handle, 0, path, ref size) || !GetProcessTimes(handle, out created, out exited, out kernel, out user))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Update process path/creation time could not be verified.");
                string executable = Path.GetFullPath(path.ToString());
                return new VanillaLauncherUpdateProcess(pid, DateTime.FromFileTimeUtc(created), executable,
                    string.Equals(Path.GetFileName(executable), "Vanilla MMO.exe", StringComparison.OrdinalIgnoreCase));
            }
        }
        internal bool Same(VanillaLauncherUpdateProcess other)
        {
            return other != null && Pid == other.Pid && StartedUtc == other.StartedUtc && Game == other.Game
                && string.Equals(Executable, other.Executable, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class VanillaLauncherUpdateReset
    {
        internal const int CooldownMs = 600000;
        internal static VanillaLauncherUpdateProcess[] Snapshot(string launcherPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(launcherPath));
            var result = new List<VanillaLauncherUpdateProcess>();
            foreach (string name in new[] { "Vanilla MMO", "Vanilla Launcher", "patcher" })
            {
                Process[] processes = Process.GetProcessesByName(name);
                try
                {
                    foreach (Process process in processes)
                    {
                        process.Refresh();
                        if (process.HasExited) continue;
                        // Unknown path/start time aborts preflight, never guesses an installation.
                        var identity = VanillaLauncherUpdateProcess.Read(process.Id);
                        if (string.Equals(Path.GetDirectoryName(identity.Executable), directory, StringComparison.OrdinalIgnoreCase)) result.Add(identity);
                    }
                }
                finally { foreach (Process process in processes) process.Dispose(); }
            }
            return result.ToArray();
        }
        internal static bool Run(VanillaLauncherUpdateProcess launcher, Func<bool> stillBlocked,
            Func<VanillaLauncherUpdateProcess[]> snapshot, Action<VanillaLauncherUpdateProcess> close,
            Action<VanillaLauncherUpdateProcess> exited, Func<bool> cancelled, System.Action begin)
        {
            System.Action check = () => { if (cancelled()) throw new OperationCanceledException("Launcher update recovery cancelled."); };
            check();
            var targets = snapshot(); Validate(launcher, targets);
            if (!targets.Any(p => p.Game)) return false;
            check();
            if (!stillBlocked()) return false;
            var confirmed = snapshot(); Validate(launcher, confirmed);
            if (targets.Length != confirmed.Length || !targets.All(p => confirmed.Any(p.Same)))
                throw new InvalidOperationException("Launcher/client identities changed during preflight; no close sent.");
            check(); begin();
            // Stop patchers first so they cannot start a game during client shutdown.
            foreach (var target in targets.OrderBy(p => p.Game ? 1 : 0).ThenBy(p => p.Pid))
            { check(); close(target); exited(target); }
            check();
            if (snapshot().Length != 0)
                throw new InvalidOperationException("A Vanilla process appeared/remained during update recovery; launcher restart withheld.");
            return true;
        }
        private static void Validate(VanillaLauncherUpdateProcess launcher, VanillaLauncherUpdateProcess[] targets)
        {
            if (launcher == null || launcher.Game || !VanillaPatcherLauncher.IsPatcher(launcher.Executable)
                || targets == null || targets.Any(p => p == null || p.Pid <= 0 || p.StartedUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(p.Executable)))
                throw new InvalidOperationException("Unverified update process identity.");
            string directory = Path.GetDirectoryName(Path.GetFullPath(launcher.Executable));
            if (targets.Select(p => p.Pid).Distinct().Count() != targets.Length || targets.Count(p => p.Game) > 2
                || targets.Length > 18 || !targets.Any(launcher.Same)
                || targets.Any(p => !string.Equals(Path.GetDirectoryName(Path.GetFullPath(p.Executable)), directory, StringComparison.OrdinalIgnoreCase)
                    || (p.Game ? !string.Equals(Path.GetFileName(p.Executable), "Vanilla MMO.exe", StringComparison.OrdinalIgnoreCase) : !VanillaPatcherLauncher.IsPatcher(p.Executable))))
                throw new InvalidOperationException("Update reset requires one installation, at most two game clients and the original launcher identity.");
        }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private bool launcherUpdateResetRunning;
        private TimeSpan? nextLauncherUpdateReset;
        private int launcherUpdateResetSerial;
        private bool RecoverLauncherUpdate(Runtime owner, int generation, Func<bool> callerCancelled,
            VanillaLauncherUpdateProcess launcher, Func<bool> stillBlocked, Func<VanillaLauncherUpdateProcess[]> snapshot = null)
        {
            Dictionary<Runtime, Tuple<int?, Guid?>> identities = null;
            Func<bool> cancelled = () =>
            {
                lock (gate)
                {
                    Runtime current;
                    bool changed = identities != null && identities.Any(pair => !runtimes.TryGetValue(pair.Key.Account.Id, out current)
                        || !ReferenceEquals(current, pair.Key) || current.ProcessId != pair.Value.Item1 || current.CharacterSession != pair.Value.Item2);
                    return changed || disposed || callerCancelled() || generation != Volatile.Read(ref resumeVerificationGeneration)
                        || !runtimes.TryGetValue(owner.Account.Id, out current) || !ReferenceEquals(current, owner)
                        || !current.Account.Enabled || !current.ScriptRunning || !current.RecoveryOwned
                        || current.ResumeOperationGeneration != generation || current.ProcessId.HasValue;
                }
            };
            lock (gate)
            {
                if (cancelled()) throw new OperationCanceledException("Launcher operation was replaced.");
                if (launcherUpdateResetRunning || OtherRecoveryOwner(owner) != null) return false;
                if (nextLauncherUpdateReset.HasValue && restartEnvironment.MonotonicNow < nextLauncherUpdateReset.Value)
                { Log(owner.Account.Label + ": launcher update reset is cooling down; no further clients closed."); return false; }
                identities = runtimes.Values.ToDictionary(row => row, row => Tuple.Create(row.ProcessId, row.CharacterSession));
                launcherUpdateResetRunning = true;
            }
            try
            {
                return VanillaLauncherUpdateReset.Run(launcher, stillBlocked,
                    snapshot ?? (() => VanillaLauncherUpdateReset.Snapshot(launcher.Executable)),
                    target => restartEnvironment.CloseClient(target.Pid, target.StartedUtc, cancelled, action =>
                    { lock (gate) { if (cancelled()) throw new OperationCanceledException(); action(); } }),
                    target =>
                    {
                        lock (gate)
                        {
                            Log("Launcher update: exit confirmed for " + (target.Game ? "game" : "patcher") + " PID " + target.Pid + ".");
                            if (!target.Game || cancelled()) return;
                            foreach (Runtime row in runtimes.Values.Where(r => r.ProcessId == target.Pid))
                            {
                                identities.Remove(row);
                                row.ProcessId = null; row.CharacterSession = null; row.ConfirmedCharacter = null;
                                row.ScriptRunning = row.RecoveryOwned = row.ClosingForRecovery = false;
                                row.ResumeSent = row.HasBeenOnline = row.ResumeVerificationFailed = false;
                                row.ResumeFailureDetail = null; row.Visual = VanillaVisualState.Unknown;
                                row.GameplaySince = row.LoginLikeSince = row.LastLaunch = null;
                                row.NextRecoveryAt = restartEnvironment.UtcNow; row.MovementRecoveryPending = false;
                                row.MovementWatchdog.Reset(); ResetTerminalEvidence(row);
                                SetStage(row, VanillaReconnectStage.WaitingForClient, "Launcher update: queued for sequential relaunch");
                            }
                            try { positionClientExited?.Invoke(target.Pid); }
                            catch (Exception ex) { Log("Launcher update: exited-reader cleanup failed: " + ex.Message); }
                        }
                    }, cancelled, () =>
                    {
                        lock (gate)
                        {
                            if (cancelled()) throw new OperationCanceledException();
                            nextLauncherUpdateReset = restartEnvironment.MonotonicNow.Add(TimeSpan.FromMilliseconds(VanillaLauncherUpdateReset.CooldownMs));
                            launcherUpdateResetSerial++;
                            Interlocked.Increment(ref weightMaintenanceGeneration); Interlocked.Increment(ref smartTeleportGeneration);
                            Log(owner.Account.Label + ": update stalled without GAME START for 60s. Closing launchers and both same-installation clients under the global recovery lease.");
                            VanillaDebugLog.Write("LAUNCHER", "event=update-reset-start accountId=" + owner.Account.Id + " launcherPid=" + launcher.Pid);
                        }
                    });
            }
            finally { lock (gate) launcherUpdateResetRunning = false; RaiseUpdated(); }
        }
    }
}
