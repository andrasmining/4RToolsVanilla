from pathlib import Path
import difflib

def edit(path, fn):
    p = Path(path)
    raw = p.read_bytes()
    old = raw.decode('utf-8-sig').splitlines(True)
    text = ''.join(old).replace('\r\n', '\n')
    new = fn(text).splitlines(True)
    out = []
    for op, a, b, c, d in difflib.SequenceMatcher(None, [x.rstrip('\r\n') for x in old], [x.rstrip('\r\n') for x in new], autojunk=False).get_opcodes():
        out.extend(old[a:b] if op == 'equal' else new[c:d])
    p.write_bytes((b'\xef\xbb\xbf' if raw.startswith(b'\xef\xbb\xbf') else b'') + ''.join(out).encode('utf-8'))

def rep(s, old, new):
    if s.count(old) != 1: raise RuntimeError('Expected one exact source match: ' + old[:100])
    return s.replace(old, new)

def launcher(s):
    s = rep(s, '        internal const int VisualConfirmationDelayMs = 750;', '''        internal const int VisualConfirmationDelayMs = 750;
        internal const int MaximumUpdateWaitMs = 600000;
        private sealed class UpdateResetCompletedException : Exception { }

        internal static string RequireLauncher(string executablePath)
        {
            string resolved = IsPatcher(executablePath) ? executablePath : PreferPatcherBesideClient(executablePath);
            if (!IsPatcher(resolved))
                throw new InvalidOperationException("Vanilla must start through Vanilla Launcher.exe or patcher.exe so updates can finish. No direct game fallback is allowed.");
            return resolved;
        }''')
    s = rep(s, '        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);', '''        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);''')
    s = rep(s, '''            string debugDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(executablePath)''', '''            string debugDirectory = null,
            Func<VanillaLauncherUpdateProcess, Func<bool>, bool> recoverUpdate = null)
        {
            executablePath = RequireLauncher(executablePath);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return LaunchAttempt(executablePath, arguments, log, cancelled, timeoutMs, retryMs,
                        gameStartX, gameStartY, debugDirectory, attempt == 0 ? recoverUpdate : null);
                }
                catch (UpdateResetCompletedException)
                {
                    log?.Invoke("event=update-reset-complete All same-installation clients and patchers exited. Restarting the launcher; direct game launch remains forbidden.");
                }
            }
            throw new InvalidOperationException("Launcher update recovery exceeded its single-reset budget.");
        }

        private static int? LaunchAttempt(string executablePath, string arguments, Action<string> log,
            Func<bool> cancelled, int timeoutMs, int retryMs, double gameStartX, double gameStartY,
            string debugDirectory, Func<VanillaLauncherUpdateProcess, Func<bool>, bool> recoverUpdate)
        {
            if (string.IsNullOrWhiteSpace(executablePath)''')
    s = rep(s, '            var before = new HashSet<int>(GetVanillaProcessIds());', '''            if (cancelled != null && cancelled()) throw new OperationCanceledException("Launcher start cancelled.");
            var before = new HashSet<int>(GetVanillaProcessIds());''')
    s = rep(s, '''                if (!IsPatcher(executablePath))
                {
                    log?.Invoke("Started configured Vanilla executable directly.");
                    return launched == null ? (int?)null : launched.Id;
                }

''', '')
    s = rep(s, '                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);', '''                var clock = Stopwatch.StartNew();
                TimeSpan deadline = TimeSpan.FromMilliseconds(timeoutMs);
                var patchWatch = new VanillaLauncherPatchWatch();
                string lastPatchSignature = null;''')
    s = rep(s, '                while (DateTime.UtcNow < deadline)', '                while (clock.Elapsed < deadline)')
    s = s.replace('FindNewVanillaProcess(before)', 'FindNewVanillaProcess(before, launcherDirectory)')
    s = rep(s, '''                                    nextClick = DateTime.UtcNow.AddMilliseconds(1000);
                                    continue;
                                }
                                log?.Invoke("Launcher foreground verified''', '''                                    patchWatch.Reset();
                                    nextClick = DateTime.UtcNow.AddMilliseconds(1000);
                                    continue;
                                }
                                log?.Invoke("Launcher foreground verified''')
    s = rep(s, '''                                if (nativeFound)
                                {
                                    UIntPtr result;''', '''                                if (nativeFound)
                                {
                                    patchWatch.Reset();
                                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                                    UIntPtr result;''')
    s = rep(s, '''                                        log?.Invoke("GAME START is not safely detected yet; no fallback coordinate click was sent. " + firstEvidence);
                                        nextClick = DateTime.UtcNow.AddMilliseconds(1000);''', '''                                        log?.Invoke("GAME START is not safely detected yet; no fallback coordinate click was sent. " + firstEvidence);
                                        var frame = ObservePatchFrame(patcherPid.Value, launcherHwnd);
                                        var blocked = VanillaLauncherUpdateProcess.Read(patcherPid.Value);
                                        bool stalled = patchWatch.Observe(patcherPid.Value, launcherHwnd, frame, clock.Elapsed, blocked.StartedUtc.Ticks);
                                        if (frame != null && lastPatchSignature != frame.Signature)
                                        {
                                            deadline = TimeSpan.FromMilliseconds(Math.Min(MaximumUpdateWaitMs, clock.ElapsedMilliseconds + timeoutMs));
                                            log?.Invoke("event=launcher-updating Update progress/status observed; waiting for GAME START (maximum 10 minutes).");
                                        }
                                        lastPatchSignature = frame?.Signature;
                                        if (stalled && recoverUpdate != null)
                                        {
                                            bool recovered = recoverUpdate(blocked, () =>
                                            {
                                                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                                                if (FindNewVanillaProcess(before, launcherDirectory).HasValue) return false;
                                                var fresh = ObservePatchFrame(blocked.Pid, launcherHwnd);
                                                return fresh != null && fresh.Signature == frame.Signature;
                                            });
                                            if (recovered) throw new UpdateResetCompletedException();
                                            patchWatch.Reset();
                                        }
                                        nextClick = DateTime.UtcNow.AddMilliseconds(1000);''')
    s = rep(s, '                                    SleepCancellable(VisualConfirmationDelayMs, cancelled);', '''                                    patchWatch.Reset();
                                    SleepCancellable(VisualConfirmationDelayMs, cancelled);''')
    s = rep(s, '''                            catch (Exception ex)
                            {
                                log?.Invoke("Launcher GAME START attempt failed''', '''                            catch (UpdateResetCompletedException) { throw; }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                patchWatch.Reset();
                                log?.Invoke("Launcher GAME START attempt failed''')
    s = rep(s, '''                            stableLauncherPid = 0;
                            launcherWindowStableAt = null;''', '''                            patchWatch.Reset();
                            stableLauncherPid = 0;
                            launcherWindowStableAt = null;''')
    s = rep(s, '''                throw new TimeoutException("Vanilla launcher did not start a new Vanilla MMO client within " + (timeoutMs / 1000) + " seconds.");''', '''                throw new TimeoutException("Vanilla launcher did not start a new Vanilla MMO client within the bounded wait ("
                    + (int)clock.Elapsed.TotalSeconds + " seconds). No direct-game fallback was used.");''')
    s = rep(s, '                if (found == IntPtr.Zero && title.IndexOf("GAME START", StringComparison.OrdinalIgnoreCase) >= 0)', '''                if (found == IntPtr.Zero && IsWindowVisible(hwnd) && IsWindowEnabled(hwnd)
                    && title.IndexOf("GAME START", StringComparison.OrdinalIgnoreCase) >= 0)''')
    s = rep(s, '''        private static int? FindNewVanillaProcess(HashSet<int> before)
        {
            foreach (int pid in GetVanillaProcessIds())
                if (!before.Contains(pid)) return pid;
            return null;
        }''', '''        private static int? FindNewVanillaProcess(HashSet<int> before, string directory)
        {
            foreach (int pid in GetVanillaProcessIds())
            {
                if (before.Contains(pid)) continue;
                try
                {
                    var identity = VanillaLauncherUpdateProcess.Read(pid);
                    if (identity.Game && string.Equals(Path.GetDirectoryName(identity.Executable),
                        Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return pid;
                }
                catch { /* Unverified identity never authorizes binding/input. */ }
            }
            return null;
        }''')
    s = rep(s, 'string candidateDirectory = Path.GetDirectoryName(process.MainModule.FileName);', 'string candidateDirectory = Path.GetDirectoryName(VanillaLauncherUpdateProcess.Read(process.Id).Executable);')
    s = rep(s, '''                            catch { }
                        }
                        DateTime started = process.StartTime;''', '''                            catch { continue; }
                        }
                        DateTime started = process.StartTime;''')
    anchor = '        private static string ClickTargetedWindowAtPoint(int processId, double x, double y)'
    s = rep(s, anchor, '''        private static VanillaLauncherPatchFrame ObservePatchFrame(int pid, IntPtr hwnd)
        {
            try
            {
                uint owner;
                if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetForegroundWindow() != hwnd
                    || GetWindowThreadProcessId(hwnd, out owner) == 0 || owner != (uint)pid
                    || !string.Equals(WindowClass(hwnd), "TThorForm", StringComparison.OrdinalIgnoreCase)) return null;
                RECT rect;
                if (!GetClientRect(hwnd, out rect)) return null;
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                if (width < 200 || height < 120 || width > 4096 || height > 2160) return null;
                using (var image = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    bool captured;
                    using (var graphics = Graphics.FromImage(image))
                    {
                        IntPtr dc = graphics.GetHdc();
                        try { captured = PrintWindow(hwnd, dc, 1); }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    var frame = captured ? VanillaLauncherPatchFrame.Read(image) : null;
                    if (frame != null) return frame;
                    var origin = new POINT();
                    if (!ClientToScreen(hwnd, ref origin) || GetForegroundWindow() != hwnd) return null;
                    foreach (int dx in new[] { width / 12, width / 2, width * 11 / 12 })
                    {
                        IntPtr at = WindowFromPoint(new POINT { X = origin.X + dx, Y = origin.Y + height * 93 / 100 });
                        if (at != hwnd && !IsChild(hwnd, at)) return null;
                    }
                    using (var graphics = Graphics.FromImage(image))
                        graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height));
                    return GetForegroundWindow() == hwnd ? VanillaLauncherPatchFrame.Read(image) : null;
                }
            }
            catch { return null; } // Failed capture is unknown, never stalled-update evidence.
        }

''' + anchor)
    return s

edit('Model/Vanilla/VanillaPatcherLauncher.cs', launcher)

def supervisor(s):
    s = rep(s, '        private void TickLocked()\n        {', '''        private void TickLocked()
        {
            // One update owner intentionally closes both clients. No sibling adoption/relaunch mid-reset.
            if (launcherUpdateResetRunning) return;''')
    s = rep(s, '''                try
                {
                    int? launchedPid = VanillaPatcherLauncher.Launch(executable, arguments,''', '''                Func<bool> launchCancelled = () =>
                {
                    lock (gate)
                    {
                        Runtime current;
                        aborted = disposed || !running || generation != Volatile.Read(ref resumeVerificationGeneration)
                            || !runtimes.TryGetValue(accountId, out current) || !ReferenceEquals(runtime, current)
                            || current.ResumeOperationGeneration != generation || !current.ScriptRunning;
                        return aborted;
                    }
                };
                try
                {
                    int? launchedPid = VanillaPatcherLauncher.Launch(executable, arguments,''')
    s = rep(s, '''                        () =>
                        {
                            lock (gate)
                            {
                                Runtime current;
                                aborted = disposed || !running || generation != Volatile.Read(ref resumeVerificationGeneration)
                                    || !runtimes.TryGetValue(accountId, out current) || !ReferenceEquals(runtime, current)
                                    || current.ResumeOperationGeneration != generation || !current.ScriptRunning;
                                return aborted;
                            }
                        });''', '''                        launchCancelled,
                        recoverUpdate: (blocked, stillBlocked) => RecoverLauncherUpdate(runtime, generation,
                            launchCancelled, blocked, stillBlocked));''')
    return s
edit('Model/Vanilla/VanillaReconnect.cs', supervisor)

def startup(s):
    s = rep(s, '''            try
            {
                for (int index = 0; index < accounts.Length; index++)''', '''            try
            {
                int updateSerial = launcherUpdateResetSerial;
                for (int index = 0; index < accounts.Length; index++)''')
    s = rep(s, '''                    RunOneColdStart(generation, account, config, index + 1, accounts.Length);
                }''', '''                    RunOneColdStart(generation, account, config, index + 1, accounts.Length);
                    if (updateSerial != launcherUpdateResetSerial)
                    {
                        // The update may have closed an earlier completed row: revisit before supervision starts.
                        updateSerial = launcherUpdateResetSerial;
                        index = -1;
                        Log("Launcher update completed; rechecking all enabled characters sequentially.");
                    }
                }''')
    s = rep(s, '                pid = VanillaPatcherLauncher.Launch(config.LaunchExecutable, string.Empty,', '                pid = VanillaPatcherLauncher.Launch(config.LaunchExecutable, config.LaunchArguments,')
    s = rep(s, '                    debugDirectory: Path.Combine(baseDirectory, "Logs"));', '''                    debugDirectory: Path.Combine(baseDirectory, "Logs"),
                    recoverUpdate: (blocked, stillBlocked) => RecoverLauncherUpdate(runtime, resumeGeneration,
                        () => StartupCancelled(generation), blocked, stillBlocked));''')
    return s
edit('Model/Vanilla/VanillaStartupOrchestrator.cs', startup)
edit('Tests/Program.cs', lambda s: rep(s, '            failed += VanillaPatcherLauncherTests.Run();', '            failed += VanillaPatcherLauncherTests.Run();\n            failed += VanillaLauncherUpdateTests.Run();'))
edit('Tests/Vanilla.Diagnostics.Tests.csproj', lambda s: rep(s, '    <Compile Include="VanillaPatcherLauncherTests.cs" />', '    <Compile Include="VanillaPatcherLauncherTests.cs" />\n    <Compile Include="VanillaLauncherUpdateTests.cs" />'))
edit('Tests/VanillaNativeRecoveryTests.cs', lambda s: rep(s, '                    DateTime start = child.StartTime.ToUniversalTime();', '''                    DateTime start = child.StartTime.ToUniversalTime();
                    var updateIdentity = VanillaLauncherUpdateProcess.Read(child.Id);
                    if (updateIdentity.Pid != child.Id || updateIdentity.StartedUtc != start
                        || !string.Equals(updateIdentity.Executable, Assembly.GetExecutingAssembly().Location, StringComparison.OrdinalIgnoreCase)
                        || VanillaLauncherUpdateProcess.MetadataAccess != 0x1000)
                        throw new Exception("Limited-query update identity did not match the inert child.");'''))
for path in ['AGENTS.md', 'Model/Vanilla/AGENTS.md']:
    edit(path, lambda s: s + '''\n## Launcher update reset exception (2026-09-20)\n\nAlways start through Vanilla Launcher.exe / patcher.exe, never bypass updates with\na direct game launch. A supplied game path may resolve to its adjacent launcher;\nmissing launcher means configuration failure, not a fallback.\n\nThe user authorizes a narrow exception to healthy-sibling isolation: a verified\nlauncher update progress screen with no GAME START and no progress/status change\nfor 60 continuous seconds may close same-installation patchers and BOTH game\nclients. Missing Start alone, failed/unknown captures and changing progress never\nauthorize this. Preflight paths plus creation times twice, freshly reconfirm the\nscreen, retain the global recovery lease, close patchers then games and confirm\nevery exit plus a final empty process snapshot before restarting the launcher.\nUse the existing bounded creation-time-pinned Windows close protocol. Metadata\nuses limited query only, with no alternate access after failure.\n\nAllow one reset per launch invocation, with a ten-minute supervisor cooldown.\nRecognized changing update progress may extend waiting to a ten-minute hard bound.\nSTOP, settings/generation, runtime/PID/session replacement cancel pending closes\nand delayed restart. Preserve disabled characters and Cart/manual/completion holds.\nRestore enabled characters sequentially through login, verified movement and\nminimization; cold startup revisits earlier closed rows before supervision starts.\nOrdinary recovery still leaves healthy siblings untouched. Log the evidence and\nconfirmed exits; do not claim a proven file lock from a frozen update screen alone.\n''')
print('Launcher integration applied with guarded source matches.')
