using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace _4RTools.Model.Vanilla
{
    public sealed class VanillaTemporaryActionSettings
    {
        public int Version { get; set; } = 1;
        public int ActionKey { get; set; }
        public bool ActionCtrl { get; set; }
        public bool ActionAlt { get; set; }
        public bool ActionShift { get; set; }
        public int IntervalMs { get; set; } = 1500;
        public bool ClickTargetAfterKey { get; set; }
        public decimal TargetXPercent { get; set; } = 50m;
        public decimal TargetYPercent { get; set; } = 50m;
        public string TargetPatch { get; set; }
        public string TargetCharacterKey { get; set; }
        public string TargetMap { get; set; }
        public int TargetClickDelayMs { get; set; } = 180;
        public bool SpRestEnabled { get; set; } = true;
        public decimal RestBelowPercent { get; set; } = 10m;
        public decimal ResumeAbovePercent { get; set; } = 80m;
        public int SitStandKey { get; set; } = (int)Keys.Insert;
        public bool SitCtrl { get; set; }
        public bool SitAlt { get; set; }
        public bool SitShift { get; set; }
        public bool RestMoveCaptured { get; set; }
        public string RestMoveCharacterKey { get; set; }
        public string RestMoveMap { get; set; }
        public decimal RestMoveXPercent { get; set; } = 50m;
        public decimal RestMoveYPercent { get; set; } = 50m;

        public void Validate()
        {
            if (Version != 1) throw new ArgumentException("Unsupported temporary-action settings version.");
            if (ActionKey != 0 && !VanillaHotkeyBox.IsMainKey((Keys)ActionKey)) throw new ArgumentException("Action hotkey is invalid.");
            if (!VanillaHotkeyBox.IsMainKey((Keys)SitStandKey)) throw new ArgumentException("Sit/stand hotkey is invalid.");
            if (IntervalMs < 250 || IntervalMs > 3600000) throw new ArgumentException("Action interval must be between 0.25 seconds and one hour.");
            if (TargetClickDelayMs < 0 || TargetClickDelayMs > 5000) throw new ArgumentException("Target click delay must be between 0 and 5 seconds.");
            foreach (decimal coordinate in new[] { TargetXPercent, TargetYPercent, RestMoveXPercent, RestMoveYPercent })
                if (coordinate < 0 || coordinate > 100) throw new ArgumentException("Captured points must be inside the selected game client.");
            if (TargetPatch != null && TargetPatch.Length > 65536) throw new ArgumentException("Captured target patch is oversized.");
            if (RestBelowPercent <= 0 || RestBelowPercent >= 100 || ResumeAbovePercent <= 0 || ResumeAbovePercent > 100
                || ResumeAbovePercent <= RestBelowPercent) throw new ArgumentException("SP thresholds need 0 < rest < resume <= 100.");
        }
        public VanillaTemporaryActionSettings Clone()
        {
            Validate();
            return JsonConvert.DeserializeObject<VanillaTemporaryActionSettings>(JsonConvert.SerializeObject(this));
        }
    }

    internal enum VanillaTemporaryPhase { Ready, TargetPending, MovingBeforeSit, Resting, StandSettle }

    internal sealed class VanillaTemporarySample
    {
        internal string Identity, Map;
        internal Guid Session;
        internal DateTimeOffset At;
        internal int X, Y;
        internal decimal Sp;
        internal bool Alive;
    }

    internal interface IVanillaTemporaryIo
    {
        VanillaTemporarySample Read();
        bool Acquire();
        void Release();
        void CheckCancelled();
        void PrepareTarget();
        void ActionHotkey();
        void TargetClick();
        void MoveBeforeSit();
        void SitStand();
    }

    // Every pending action is tied to the original identity/session. A low SP reading
    // cannot abandon an already-started targeted cast and press Sit into its cursor.
    internal sealed class VanillaTemporaryCycle
    {
        private readonly IVanillaTemporaryIo io;
        private readonly VanillaTemporaryActionSettings settings;
        private readonly VanillaTemporarySample identity;
        private TimeSpan due, lastCast, restProgress;
        private DateTimeOffset lastAt, moveAt;
        private int moveX, moveY, moveAttempts;
        private bool moveObserved;
        private TimeSpan moveSettledSince, moveDeadline;
        private decimal restSp;
        internal VanillaTemporaryPhase Phase { get; private set; }
        internal int SentCycles { get; private set; }
        internal string Status { get; private set; }
        internal decimal Sp { get; private set; }
        internal VanillaTemporaryCycle(IVanillaTemporaryIo io, VanillaTemporaryActionSettings settings, TimeSpan now)
        {
            this.io = io; this.settings = settings.Clone(); identity = io.Read();
            RequireSample(identity); lastAt = identity.At; due = now; lastCast = now - TimeSpan.FromSeconds(1);
            Status = "Running";
        }
        internal void Tick(TimeSpan now)
        {
            io.CheckCancelled();
            VanillaTemporarySample sample = io.Read(); RequireSample(sample);
            if (sample.Identity != identity.Identity || sample.Session != identity.Session || sample.Map != identity.Map
                || sample.At < lastAt) throw new InvalidOperationException("Character/session/map changed; pending temporary input cancelled.");
            lastAt = sample.At; Sp = sample.Sp;
            if (Phase == VanillaTemporaryPhase.TargetPending)
            {
                if (now < due) return;
                io.CheckCancelled(); io.TargetClick(); io.Release(); SentCycles++; lastCast = now;
                Phase = VanillaTemporaryPhase.Ready; due = now + TimeSpan.FromMilliseconds(settings.IntervalMs);
                Status = "Targeted input cycle " + SentCycles + " sent";
                return;
            }
            if (Phase == VanillaTemporaryPhase.MovingBeforeSit)
            {
                if (now >= moveDeadline) throw new InvalidOperationException("Move-before-sit exceeded its bounded wait; no sit hotkey sent.");
                if (sample.At > moveAt && (sample.X != moveX || sample.Y != moveY))
                {
                    moveObserved = true; moveX = sample.X; moveY = sample.Y; moveAt = sample.At;
                    moveSettledSince = now;
                    Status = "Movement observed; waiting for the walk to settle";
                    return;
                }
                if (moveObserved && sample.At > moveAt && now - moveSettledSince >= TimeSpan.FromMilliseconds(450))
                {
                    io.CheckCancelled(); io.SitStand(); io.Release(); Phase = VanillaTemporaryPhase.Resting;
                    restProgress = now; restSp = sample.Sp;
                    Status = "Movement verified; sit requested; waiting for SP";
                    return;
                }
                if (moveObserved) return;
                if (now < due) return;
                if (++moveAttempts >= 2) throw new InvalidOperationException("Move-before-sit was not verified; no sit hotkey sent. Capture a reachable nearby ground point.");
                io.MoveBeforeSit(); due = now + TimeSpan.FromSeconds(4);
                Status = "Waiting for verified movement before sitting";
                return;
            }
            if (Phase == VanillaTemporaryPhase.Resting)
            {
                if (sample.Sp > restSp) { restSp = sample.Sp; restProgress = now; }
                if (now - restProgress > TimeSpan.FromMinutes(2))
                    throw new InvalidOperationException("No SP recovery observed for two minutes; temporary action stopped. Sitting was requested, not independently observed.");
                if (sample.Sp < settings.ResumeAbovePercent) { Status = "Waiting for SP " + sample.Sp.ToString("0.0") + "%"; return; }
                if (!io.Acquire()) { Status = "SP recovered; waiting for input lease"; return; }
                io.SitStand(); io.Release(); Phase = VanillaTemporaryPhase.StandSettle; due = now + TimeSpan.FromMilliseconds(600);
                Status = "SP recovered; stand requested"; return;
            }
            if (Phase == VanillaTemporaryPhase.StandSettle)
            { if (now < due) return; Phase = VanillaTemporaryPhase.Ready; }
            if (settings.SpRestEnabled && sample.Sp <= settings.RestBelowPercent)
            {
                if (now - lastCast < TimeSpan.FromMilliseconds(600)) return;
                if (!settings.RestMoveCaptured) throw new InvalidOperationException("Capture a nearby ground point for move-before-sit first.");
                if (!io.Acquire()) { Status = "Waiting for input lease before SP rest"; return; }
                moveX = sample.X; moveY = sample.Y; moveAt = sample.At; moveAttempts = 0; moveObserved = false; moveDeadline = now + TimeSpan.FromSeconds(10);
                io.MoveBeforeSit(); Phase = VanillaTemporaryPhase.MovingBeforeSit; due = now + TimeSpan.FromSeconds(4);
                Status = "Moving before sit; awaiting fresh X/Y change"; return;
            }
            if (now < due) return;
            if (!io.Acquire()) { Status = "Waiting for serialized input lease"; return; }
            if (settings.ClickTargetAfterKey) io.PrepareTarget();
            io.ActionHotkey();
            if (settings.ClickTargetAfterKey)
            { Phase = VanillaTemporaryPhase.TargetPending; due = now + TimeSpan.FromMilliseconds(settings.TargetClickDelayMs); Status = "Skill hotkey sent; target click pending"; }
            else
            { io.Release(); SentCycles++; lastCast = now; due = now + TimeSpan.FromMilliseconds(settings.IntervalMs); Status = "Input cycle " + SentCycles + " sent"; }
        }
        private static void RequireSample(VanillaTemporarySample value)
        {
            if (value == null || !value.Alive || value.Session == Guid.Empty || string.IsNullOrEmpty(value.Identity)
                || string.IsNullOrEmpty(value.Map) || value.Sp < 0 || value.Sp > 100)
                throw new InvalidOperationException("Fresh verified living character, SP and position are required for temporary actions.");
        }
    }

    public sealed class VanillaTemporaryActionRunner : IDisposable, IVanillaTemporaryIo
    {
        private readonly VanillaFleetMonitor fleet;
        private readonly VanillaReconnectSupervisor supervisor;
        private readonly bool ownServices;
        private readonly bool ownsOnlySupervisor;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private VanillaForegroundInput input;
        private VanillaTemporaryOwner owner;
        private VanillaTemporaryCycle cycle;
        private VanillaTemporaryActionSettings settings;
        private VanillaTemporarySample last;
        private bool disposed;
        public bool Active { get; private set; }
        public bool Resting { get { return Active && cycle?.Phase == VanillaTemporaryPhase.Resting; } }
        public int ProcessId { get; private set; }
        public string CharacterName { get; private set; }
        public string Status { get; private set; } = "Stopped";
        public int CompletedActions { get { return cycle?.SentCycles ?? 0; } }
        public decimal? SpPercent { get { return last?.Sp; } }

        public VanillaTemporaryActionRunner(string baseDirectory)
            : this(new VanillaFleetMonitor(baseDirectory), new VanillaReconnectSupervisor(VanillaAppData.RootDirectory), true) { }
        internal VanillaTemporaryActionRunner(VanillaFleetMonitor fleet, VanillaReconnectSupervisor supervisor, bool ownServices = false, bool ownsOnlySupervisor = false)
        { this.fleet = fleet; this.supervisor = supervisor; this.ownServices = ownServices; this.ownsOnlySupervisor = ownsOnlySupervisor; }

        public void Start(int pid, VanillaTemporaryActionSettings value)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VanillaTemporaryActionRunner));
            Stop("Starting temporary action");
            settings = value.Clone();
            if (settings.ActionKey == 0) throw new InvalidOperationException("Press the action hotkey in its field first.");
            if (settings.ClickTargetAfterKey && string.IsNullOrEmpty(settings.TargetPatch))
                throw new InvalidOperationException("Use CAPTURE TARGET first; older unverified point-only settings need one new capture.");
            if (settings.SpRestEnabled && !settings.RestMoveCaptured)
                throw new InvalidOperationException("Use CAPTURE REST GROUND first so movement can be verified before sitting.");
            ProcessId = pid;
            try
            {
                last = ReadSample();
                if (settings.ClickTargetAfterKey && (last.Identity != settings.TargetCharacterKey || last.Map != settings.TargetMap))
                    throw new InvalidOperationException("Target capture belongs to a different character/map; capture again.");
                if (settings.SpRestEnabled && (last.Identity != settings.RestMoveCharacterKey || last.Map != settings.RestMoveMap))
                    throw new InvalidOperationException("Rest ground capture belongs to a different character/map; capture again.");
                owner = supervisor.RegisterTemporaryAction(pid);
                input = new VanillaForegroundInput(pid);
                Active = true;
                input.CancellationRequested = () => !Active || supervisor.TemporaryActionCancelled(owner)
                    || last == null || DateTimeOffset.UtcNow < last.At || DateTimeOffset.UtcNow - last.At > TimeSpan.FromSeconds(3);
                cycle = new VanillaTemporaryCycle(this, settings, clock.Elapsed);
                Status = "Running for " + CharacterName;
                VanillaDebugLog.Write("TEMPORARY", "event=start pid=" + pid + " targeted=" + settings.ClickTargetAfterKey + ".");
            }
            catch { Stop("Temporary action could not start"); throw; }
        }
        public void Tick()
        {
            if (!Active || disposed) return;
            try { cycle.Tick(clock.Elapsed); Status = cycle.Status + " | SP " + cycle.Sp.ToString("0.0") + "%"; }
            catch (Exception ex) { Stop("Stopped: " + ex.Message); }
        }
        public void Stop(string reason = "Stopped")
        {
            bool wasActive = Active; Active = false;
            supervisor.UnregisterTemporaryAction(owner); owner = null;
            input?.Dispose(); input = null;
            Status = reason;
            if (wasActive) VanillaDebugLog.Write("TEMPORARY", "event=stop pid=" + ProcessId + " reason='" + reason + "'.");
        }
        private VanillaTemporarySample ReadSample()
        {
            VanillaFleetClientInfo info = fleet.Poll().FirstOrDefault(c => c.ProcessId == ProcessId);
            var value = Sample(info);
            CharacterName = info.CharacterName; last = value; return value;
        }
        internal static VanillaTemporarySample Sample(VanillaFleetClientInfo info)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (info == null || !info.HpVerified || !info.SpVerified || !info.NameVerified
                || info.CurrentHP.GetValueOrDefault() == 0 || !info.SpPercent.HasValue || info.Identity == null
                || !info.Identity.IsFresh(now) || info.Position == null || !info.Position.Verified
                || info.Position.Error != null || info.Position.Session != info.Identity.Session || !info.Position.X.HasValue || !info.Position.Y.HasValue
                || info.Position.At > now || now - info.Position.At > TimeSpan.FromSeconds(3))
                throw new InvalidOperationException("Verified HP/SP/identity/position is unavailable; no temporary input sent.");
            string key = VanillaCharacterRoster.Key(info.Identity);
            if (key == null || string.IsNullOrWhiteSpace(info.Position.Map))
                throw new InvalidOperationException("Gameplay username/character/map is not verified.");
            return new VanillaTemporarySample { Identity = key, Map = info.Position.Map, Session = info.Identity.Session,
                At = info.Position.At, X = info.Position.X.Value, Y = info.Position.Y.Value, Sp = info.SpPercent.Value, Alive = true };
        }
        VanillaTemporarySample IVanillaTemporaryIo.Read() { return ReadSample(); }
        bool IVanillaTemporaryIo.Acquire() { return supervisor.TryAcquireTemporaryInput(owner); }
        void IVanillaTemporaryIo.Release() { supervisor.ReleaseTemporaryInput(owner); }
        void IVanillaTemporaryIo.CheckCancelled()
        { if (!Active || supervisor.TemporaryActionCancelled(owner)) throw new OperationCanceledException("Temporary action ownership changed."); }
        void IVanillaTemporaryIo.PrepareTarget() { LocateTarget(false); }
        void IVanillaTemporaryIo.TargetClick() { LocateTarget(true); }
        void IVanillaTemporaryIo.ActionHotkey()
        { input.Chord(settings.ActionCtrl, settings.ActionAlt, settings.ActionShift, (Keys)settings.ActionKey); }
        void IVanillaTemporaryIo.SitStand()
        { input.Chord(settings.SitCtrl, settings.SitAlt, settings.SitShift, (Keys)settings.SitStandKey); }
        void IVanillaTemporaryIo.MoveBeforeSit()
        {
            using (Bitmap image = input.CaptureClientBitmap())
            {
                Point point = PointFor(image.Size, settings.RestMoveXPercent, settings.RestMoveYPercent);
                input.CompatibilityClickFromProof(new Rectangle(point.X - 1, point.Y - 1, 3, 3), input.LastCaptureProof);
            }
            VanillaDebugLog.Write("TEMPORARY", "event=move-before-sit-sent pid=" + ProcessId + "; awaiting verified X/Y.");
        }
        private void LocateTarget(bool click)
        {
            using (Bitmap image = input.CaptureClientBitmap())
            {
                Point expected = PointFor(image.Size, settings.TargetXPercent, settings.TargetYPercent), found;
                string evidence;
                if (!VanillaCapturedTarget.TryLocate(image, settings.TargetPatch, expected, out found, out evidence))
                    throw new InvalidOperationException(evidence + "; no target click sent.");
                if (click)
                {
                    input.CompatibilityClickFromProof(new Rectangle(found.X - 1, found.Y - 1, 3, 3), input.LastCaptureProof);
                    VanillaDebugLog.Write("TEMPORARY", "event=target-click-sent pid=" + ProcessId + " evidence='" + evidence + "'. Windows delivery is not a claim of skill success.");
                }
            }
        }
        internal static Point PointFor(Size size, decimal x, decimal y)
        { return new Point(Math.Max(1, Math.Min(size.Width - 2, (int)Math.Round((size.Width - 1) * x / 100m))),
            Math.Max(1, Math.Min(size.Height - 2, (int)Math.Round((size.Height - 1) * y / 100m)))); }
        public static PointF CaptureTargetPercent(int pid)
        {
            using (var input = new VanillaForegroundInput(pid))
            {
                Point point; Size size;
                CapturePoint(input.Window, out point, out size);
                return new PointF(point.X * 100f / (size.Width - 1), point.Y * 100f / (size.Height - 1));
            }
        }
        internal static void CapturePoint(IntPtr window, out Point point, out Size size)
        {
            POINT cursor; RECT rect;
            if (GetForegroundWindow() != window || !GetCursorPos(out cursor)) throw new InvalidOperationException("The selected game must remain foreground while capturing.");
            IntPtr hit = WindowFromPoint(cursor);
            if (hit != window && !IsChild(window, hit)) throw new InvalidOperationException("Place the pointer inside the selected game window.");
            if (!ScreenToClient(window, ref cursor) || !GetClientRect(window, out rect)) throw new InvalidOperationException("Game geometry could not be verified.");
            size = new Size(rect.Right - rect.Left, rect.Bottom - rect.Top); point = new Point(cursor.X, cursor.Y);
            if (size.Width < 320 || size.Height < 240 || !new Rectangle(Point.Empty, size).Contains(point)) throw new InvalidOperationException("Captured point is outside the game.");
        }
        public void Dispose()
        {
            if (disposed) return; Stop(); disposed = true;
            if (ownServices) fleet.Dispose();
            if (ownServices || ownsOnlySupervisor) supervisor.Dispose();
        }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    }

    public sealed class VanillaTemporaryActionsPanel : UserControl
    {
        private readonly VanillaFleetMonitor fleet;
        private readonly VanillaTemporaryActionRunner runner;
        private readonly VanillaReconnectSupervisor supervisor;
        private VanillaTemporaryOwner captureOwner;
        private readonly bool ownsFleet;
        private readonly string settingsPath;
        private readonly Timer timer = new Timer { Interval = 150 };
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly ComboBox clients = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
        private readonly VanillaHotkeyBox actionKey = new VanillaHotkeyBox(), sitKey = new VanillaHotkeyBox();
        private readonly NumericUpDown interval = Number(.25m, 3600, 1.5m, 2), delay = Number(0, 5, .18m, 2);
        private readonly NumericUpDown rest = Number(1, 99, 10, 0), resume = Number(2, 100, 80, 0);
        private readonly CheckBox click = new CheckBox { Text = "Click captured target", AutoSize = true };
        private readonly CheckBox spRest = new CheckBox { Text = "SP rest with verified move-before-sit", AutoSize = true, Checked = true };
        private readonly Label target = new Label { AutoSize = true }, ground = new Label { AutoSize = true }, status = new Label { AutoSize = true, MaximumSize = new Size(900, 0) };
        private VanillaTemporaryActionSettings saved = new VanillaTemporaryActionSettings();
        private bool loading, capturing, groundCapture;
        private DateTime nextRefresh, captureAt;
        private int capturePid;
        private DateTime captureBirth;

        public VanillaTemporaryActionsPanel(string baseDirectory) : this(baseDirectory, null, null) { }
        internal VanillaTemporaryActionsPanel(string baseDirectory, VanillaFleetMonitor sharedFleet, VanillaReconnectSupervisor sharedSupervisor)
        {
            AutoScroll = true; Dock = DockStyle.Fill;
            fleet = sharedFleet ?? new VanillaFleetMonitor(baseDirectory); ownsFleet = sharedFleet == null;
            supervisor = sharedSupervisor ?? new VanillaReconnectSupervisor(VanillaAppData.RootDirectory);
            runner = new VanillaTemporaryActionRunner(fleet, supervisor, ownsOnlySupervisor: sharedSupervisor == null);
            settingsPath = Path.Combine(VanillaAppData.RootDirectory, "temporary-actions.json");
            Build(); LoadSettings();
            timer.Tick += (s, e) => Tick(); timer.Start();
        }
        private void Build()
        {
            var root = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(14) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Add(root, "Client", clients, "Every operation is bound to this client's verified character/session. Changing the client cancels pending input.");
            Add(root, "Action hotkey", actionKey, "Click the field and press the desired key, with Ctrl/Alt/Shift as needed. Right-click to clear. OS-reserved secure shortcuts cannot be intercepted.");
            Add(root, "Repeat interval (s)", interval, "Time between completed input cycles. A sent cycle is not independent proof that the game cast the skill.");
            Add(root, "Targeting", click, "Target is captured inside the foreground game. Its image patch can reacquire the target after camera/scale changes; ambiguous matches stop safely.");
            var targetRow = Row(Button("CAPTURE TARGET (3s)", () => ArmCapture(false)), target);
            Add(root, "Skill target", targetRow, "After pressing Capture, move the mouse over the stationary target character/name in the game before the three-second countdown ends.");
            Add(root, "Target click delay (s)", delay, "The pending target click finishes before the SP-rest sequence begins.");
            Add(root, "SP policy", spRest, "Rest uses verified HP/SP/identity/position. Movement must be observed before the sit hotkey; sitting itself is not a mapped state.");
            Add(root, "Rest / resume (%)", Row(rest, new Label { Text = "/", AutoSize = true, Margin = new Padding(5, 7, 5, 0) }, resume), "Sit at/below the first threshold; request standing at/above the second. No SP progress for two minutes stops the action.");
            Add(root, "Sit / stand hotkey", sitKey, "Capture the actual sit/stand chord, for example F12 or Insert.");
            Add(root, "Before sitting", Row(Button("CAPTURE REST GROUND (3s)", () => ArmCapture(true)), ground), "Capture reachable nearby empty ground, not a character/UI control. The program verifies X/Y movement before requesting Sit.");
            Add(root, "", Row(Button("START TEMPORARY ACTION", StartRunner), Button("STOP", StopRunner)), "STOP and settings changes cancel pending clicks. No automatic stand/resume is sent after STOP.");
            root.Controls.Add(status, 0, root.RowCount); root.SetColumnSpan(status, 2); root.RowCount++;
            Controls.Add(root);
            clients.SelectedIndexChanged += (s, e) => { if (!loading) StopRunner(); };
            actionKey.ChordChanged += Changed; sitKey.ChordChanged += Changed;
            interval.ValueChanged += Changed; delay.ValueChanged += Changed; rest.ValueChanged += Changed; resume.ValueChanged += Changed;
            click.CheckedChanged += Changed; spRest.CheckedChanged += Changed;
        }
        private void Changed(object sender, EventArgs e)
        { if (loading) return; StopRunner(); Guard(() => { Save(); status.Text = "Saved"; }); }
        private void Tick()
        {
            try
            {
                if (capturing)
                {
                    if (DateTime.UtcNow < captureAt) { status.Text = "Place the pointer in the game: " + Math.Max(1, (int)Math.Ceiling((captureAt - DateTime.UtcNow).TotalSeconds)) + "s"; return; }
                    capturing = false;
                    try { FinishCapture(); } finally { supervisor.UnregisterTemporaryAction(captureOwner); captureOwner = null; }
                    return;
                }
                if (runner.Active) { runner.Tick(); status.Text = runner.Status; }
                if (DateTime.UtcNow >= nextRefresh) RefreshClients();
            }
            catch (Exception ex) { capturing = false; supervisor.UnregisterTemporaryAction(captureOwner); captureOwner = null; runner.Stop("Stopped: " + ex.Message); status.Text = runner.Status; }
        }
        private void ArmCapture(bool forGround)
        {
            StopRunner();
            var selected = clients.SelectedItem as ClientChoice;
            if (selected == null) throw new InvalidOperationException("Select a running Vanilla client first.");
            VanillaTemporaryActionRunner.Sample(fleet.Poll().FirstOrDefault(c => c.ProcessId == selected.Id));
            var identity = VanillaLauncherUpdateProcess.Read(selected.Id);
            captureOwner = supervisor.RegisterTemporaryAction(selected.Id);
            try
            {
                if (!supervisor.TryAcquireTemporaryInput(captureOwner)) throw new InvalidOperationException("Capture is waiting for another input owner; try again when recovery completes.");
                using (var input = new VanillaForegroundInput(selected.Id)) input.Activate();
            }
            catch { supervisor.UnregisterTemporaryAction(captureOwner); captureOwner = null; throw; }
            capturePid = selected.Id; captureBirth = identity.StartedUtc; groundCapture = forGround;
            captureAt = DateTime.UtcNow.AddSeconds(3); capturing = true;
        }
        private void FinishCapture()
        {
            if (supervisor.TemporaryActionCancelled(captureOwner) || (clients.SelectedItem as ClientChoice)?.Id != capturePid || VanillaLauncherUpdateProcess.Read(capturePid).StartedUtc != captureBirth)
                throw new InvalidOperationException("Capture client changed; no point saved.");
            VanillaTemporarySample sample = VanillaTemporaryActionRunner.Sample(fleet.Poll().FirstOrDefault(c => c.ProcessId == capturePid));
            using (var input = new VanillaForegroundInput(capturePid))
            {
                Point point; Size size;
                VanillaTemporaryActionRunner.CapturePoint(input.Window, out point, out size);
                using (Bitmap image = input.CaptureClientBitmap())
                {
                    Point confirmedPoint; Size confirmedSize;
                    VanillaTemporaryActionRunner.CapturePoint(input.Window, out confirmedPoint, out confirmedSize);
                    if (confirmedPoint != point || confirmedSize != size || image.Size != size) throw new InvalidOperationException("Game resized during capture; try again.");
                    decimal x = point.X * 100m / (size.Width - 1), y = point.Y * 100m / (size.Height - 1);
                    if (groundCapture) { saved.RestMoveCaptured = true; saved.RestMoveXPercent = x; saved.RestMoveYPercent = y; saved.RestMoveCharacterKey = sample.Identity; saved.RestMoveMap = sample.Map; }
                    else
                    {
                        saved.TargetPatch = VanillaCapturedTarget.Capture(image, point);
                        saved.TargetXPercent = x; saved.TargetYPercent = y;
                        saved.TargetCharacterKey = sample.Identity; saved.TargetMap = sample.Map;
                        loading = true; click.Checked = true; loading = false;
                    }
                }
            }
            Save(); ShowPoints(); status.Text = groundCapture ? "Rest ground captured" : "Target captured";
        }
        private void StartRunner()
        {
            capturing = false;
            var selected = clients.SelectedItem as ClientChoice;
            if (selected == null) throw new InvalidOperationException("Select a running Vanilla client first.");
            Save(); runner.Start(selected.Id, saved); status.Text = runner.Status;
        }
        internal void StopForApplicationUpdate() { StopRunner(); }
        private void StopRunner()
        {
            capturing = false; supervisor.UnregisterTemporaryAction(captureOwner); captureOwner = null;
            runner.Stop("Stopped by user/settings change"); status.Text = runner.Status;
        }
        private void RefreshClients()
        {
            int? selected = (clients.SelectedItem as ClientChoice)?.Id;
            var list = fleet.Poll().Select(c => new ClientChoice(c.ProcessId, c.NameVerified ? c.CharacterName : "Unverified character")).ToArray();
            nextRefresh = DateTime.UtcNow.AddSeconds(3);
            if (list.Select(c => c.Id).SequenceEqual(clients.Items.Cast<ClientChoice>().Select(c => c.Id))) return;
            loading = true;
            clients.Items.Clear(); clients.Items.AddRange(list);
            clients.SelectedItem = list.FirstOrDefault(c => c.Id == selected) ?? list.FirstOrDefault();
            loading = false;
            if (runner.Active && selected != (clients.SelectedItem as ClientChoice)?.Id) StopRunner();
        }
        private void LoadSettings()
        {
            loading = true;
            try
            {
                if (File.Exists(settingsPath)) { saved = JsonConvert.DeserializeObject<VanillaTemporaryActionSettings>(File.ReadAllText(settingsPath)); saved.Validate(); }
            }
            catch { saved = new VanillaTemporaryActionSettings(); status.Text = "Saved temporary settings could not be read; original file retained."; }
            actionKey.Set(saved.ActionKey, saved.ActionCtrl, saved.ActionAlt, saved.ActionShift);
            sitKey.Set(saved.SitStandKey, saved.SitCtrl, saved.SitAlt, saved.SitShift);
            interval.Value = saved.IntervalMs / 1000m; delay.Value = saved.TargetClickDelayMs / 1000m;
            click.Checked = saved.ClickTargetAfterKey; spRest.Checked = saved.SpRestEnabled;
            rest.Value = saved.RestBelowPercent; resume.Value = saved.ResumeAbovePercent;
            ShowPoints(); loading = false;
        }
        private void Save()
        {
            var value = saved.Clone();
            value.ActionKey = actionKey.Key; value.ActionCtrl = actionKey.Ctrl; value.ActionAlt = actionKey.Alt; value.ActionShift = actionKey.Shift;
            value.SitStandKey = sitKey.Key; value.SitCtrl = sitKey.Ctrl; value.SitAlt = sitKey.Alt; value.SitShift = sitKey.Shift;
            value.IntervalMs = (int)(interval.Value * 1000); value.TargetClickDelayMs = (int)(delay.Value * 1000);
            value.ClickTargetAfterKey = click.Checked; value.SpRestEnabled = spRest.Checked; value.RestBelowPercent = rest.Value; value.ResumeAbovePercent = resume.Value;
            value.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            string temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
                if (File.Exists(settingsPath)) File.Replace(temp, settingsPath, settingsPath + ".previous"); else File.Move(temp, settingsPath);
                saved = value;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private void ShowPoints()
        {
            target.Text = string.IsNullOrEmpty(saved.TargetPatch) ? "Not captured" : saved.TargetXPercent.ToString("0.00") + "%, " + saved.TargetYPercent.ToString("0.00") + "%";
            ground.Text = !saved.RestMoveCaptured ? "Not captured" : saved.RestMoveXPercent.ToString("0.00") + "%, " + saved.RestMoveYPercent.ToString("0.00") + "%";
        }
        private void Add(TableLayoutPanel root, string text, Control control, string hint)
        {
            int row = root.RowCount++;
            var label = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 8, 14, 4) };
            control.Margin = new Padding(3, 4, 3, 4);
            root.Controls.Add(label, 0, row); root.Controls.Add(control, 1, row);
            tips.SetToolTip(label, hint); tips.SetToolTip(control, hint);
            foreach (Control child in control.Controls) tips.SetToolTip(child, hint);
        }
        private Button Button(string text, System.Action action)
        { var button = new Button { Text = text, AutoSize = true }; button.Click += (s, e) => Guard(action); return button; }
        private void Guard(System.Action action) { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }
        private static FlowLayoutPanel Row(params Control[] controls)
        { var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill }; row.Controls.AddRange(controls); return row; }
        private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals)
        { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 100 }; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); tips.Dispose(); supervisor.UnregisterTemporaryAction(captureOwner); captureOwner = null; runner.Dispose(); if (ownsFleet) fleet.Dispose(); }
            base.Dispose(disposing);
        }
        private sealed class ClientChoice
        {
            internal int Id; private readonly string name;
            internal ClientChoice(int id, string name) { Id = id; this.name = name; }
            public override string ToString() { return name + " — PID " + Id; }
        }
    }
}
