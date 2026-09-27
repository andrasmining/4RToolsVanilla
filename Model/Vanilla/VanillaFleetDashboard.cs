using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using _4RTools.Model.Vanilla.Automation;
using _4RTools.Utils;

namespace _4RTools.Model.Vanilla
{
    public sealed class VanillaFleetClientInfo
    {
        public int ProcessId { get; internal set; }
        public string CharacterName { get; internal set; }
        public uint? CurrentHP { get; internal set; }
        public uint? MaxHP { get; internal set; }
        public uint? CurrentSP { get; internal set; }
        public uint? MaxSP { get; internal set; }
        public uint? CurrentWeight { get; internal set; }
        public uint? MaxWeight { get; internal set; }
        public uint? CurrentCartWeight { get; internal set; }
        public uint? MaxCartWeight { get; internal set; }
        public bool HpVerified { get; internal set; }
        public bool SpVerified { get; internal set; }
        public bool NameVerified { get; internal set; }
        public bool WeightVerified { get; internal set; }
        public bool CartWeightVerified { get; internal set; }
        public string Location { get; internal set; }
        public string Build { get; internal set; }
        public string Error { get; internal set; }
        public bool Ready { get; internal set; }
        internal VanillaPositionSample Position { get; set; }
        internal VanillaClientState Snapshot { get; set; }
        internal VanillaCharacterIdentity Identity { get; set; }

        public decimal? HpPercent { get { return Percent(CurrentHP, MaxHP); } }
        public decimal? SpPercent { get { return Percent(CurrentSP, MaxSP); } }
        public decimal? WeightPercent { get { return Percent(CurrentWeight, MaxWeight); } }
        public decimal? CartWeightPercent { get { return Percent(CurrentCartWeight, MaxCartWeight); } }

        private static decimal? Percent(uint? current, uint? maximum)
        {
            if (!current.HasValue || !maximum.HasValue || maximum.Value == 0) return null;
            return Math.Max(0m, Math.Min(100m, current.Value * 100m / maximum.Value));
        }
    }

    /// <summary>
    /// Keeps lightweight read-only observations for the at-most-two live Vanilla clients.
    /// It never sends input. Each PID is fingerprinted and resolved through the audited build profile.
    /// </summary>
    public sealed partial class VanillaFleetMonitor : IDisposable
    {
        private readonly string baseDirectory;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<int, IClientReader> readers = new Dictionary<int, IClientReader>();
        private readonly Func<IEnumerable<IProcessMetadata>> enumerateProcesses;
        private readonly Func<int, IClientReader> createReader;
        private readonly Func<ProcessObservationContext> observerContext;
        private readonly object gate = new object();
        private MemoryObservationException enumerationFailure;
        private bool disposed;
        private int pollCount;

        internal int PollCount { get { lock (gate) return pollCount; } }

        public VanillaFleetMonitor(string baseDirectory)
            : this(baseDirectory, EnumerateProcesses, null, () => ProcessObservationContext.Current)
        {
        }

        internal VanillaFleetMonitor(string baseDirectory, Func<IEnumerable<IProcessMetadata>> enumerateProcesses,
            Func<int, IClientReader> createReader, Func<ProcessObservationContext> observerContext)
        {
            this.baseDirectory = Path.GetFullPath(baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory)));
            this.enumerateProcesses = enumerateProcesses ?? throw new ArgumentNullException(nameof(enumerateProcesses));
            this.createReader = createReader ?? (pid => new Reader(this.baseDirectory, pid));
            this.observerContext = observerContext ?? throw new ArgumentNullException(nameof(observerContext));
        }

        public IReadOnlyList<VanillaFleetClientInfo> Poll()
        {
            lock (gate)
            {
                if (disposed) return new VanillaFleetClientInfo[0];
                pollCount++;
                if (enumerationFailure != null) throw enumerationFailure;
                var live = new List<int>();
                var enumerated = new HashSet<int>();
                foreach (IProcessMetadata process in EnumerateOrStop())
                {
                    using (process)
                    {
                        int pid = process.ProcessId;
                        if (!enumerated.Add(pid)) continue;

                        // Process.GetProcessesByName already gave us a current PID snapshot. Do not
                        // immediately ask System.Diagnostics for HasExited/MainWindowHandle: those
                        // convenience properties request process-query/synchronization access and can
                        // be denied by a protected Vanilla client before our actual VM_READ-only
                        // observation is even attempted. Let the read-only reader be the authority.
                        live.Add(pid);
                    }
                }
                live = live.Distinct().OrderBy(value => value).ToList();
                foreach (int dead in readers.Keys.Where(pid => !enumerated.Contains(pid)).ToArray())
                {
                    readers[dead].Dispose();
                    readers.Remove(dead);
                }
                foreach (int pid in live)
                {
                    if (readers.ContainsKey(pid)) continue;
                    try { readers.Add(pid, createReader(pid)); }
                    catch (Exception ex) { readers.Add(pid, Reader.Failed(pid, ex.Message)); }
                }
                var clients = live.Select(pid => readers[pid].Poll(clock.Elapsed)).ToArray();
                PublishPositions(clients);
                PublishCharacters(clients);
                return clients;
            }
        }

        private IProcessMetadata[] EnumerateOrStop()
        {
            var processes = new List<IProcessMetadata>();
            try
            {
                // Own every wrapper returned by the process enumeration snapshot.
                foreach (IProcessMetadata process in enumerateProcesses()) processes.Add(process);
                return processes.ToArray();
            }
            catch (Exception ex)
            {
                foreach (IProcessMetadata process in processes) process.Dispose();
                foreach (IClientReader reader in readers.Values) reader.Dispose();
                readers.Clear();
                System.Threading.Volatile.Write(ref characterCache, new VanillaCharacterIdentity[0]);
                const string operation = "Process.GetProcessesByName(Vanilla MMO)";
                int? nativeCode = (ex as Win32Exception)?.NativeErrorCode
                    ?? (ex as MemoryObservationException)?.NativeErrorCode;
                string details = nativeCode.HasValue
                    ? "Win32 " + nativeCode.Value + " (" + new Win32Exception(nativeCode.Value).Message + ")"
                    : ex.Message;
                enumerationFailure = new MemoryObservationException(operation + " failed: " + details + ". "
                    + observerContext() + " Fleet observation stopped; no further enumeration will be attempted.", nativeCode);
                throw enumerationFailure;
            }
        }

        public IReadOnlyList<int> LiveProcessIds()
        {
            return Poll().Select(item => item.ProcessId).ToArray();
        }

        internal interface IProcessMetadata : IDisposable
        {
            int ProcessId { get; }
            bool HasExited { get; }
            IntPtr MainWindowHandle { get; }
        }

        internal interface IClientReader : IDisposable
        {
            bool IsStopped { get; }
            VanillaFleetClientInfo Poll(TimeSpan now);
        }

        private static IEnumerable<IProcessMetadata> EnumerateProcesses()
        {
            return Process.GetProcessesByName("Vanilla MMO").Select(process => new ProcessMetadata(process)).ToArray();
        }

        private sealed class ProcessMetadata : IProcessMetadata
        {
            private readonly Process process;
            public ProcessMetadata(Process process) { this.process = process; }
            public int ProcessId { get { return process.Id; } }
            // Retained on the internal test abstraction for compatibility. Production polling no
            // longer calls either property because they require rights unrelated to memory reading.
            public bool HasExited { get { return process.HasExited; } }
            public IntPtr MainWindowHandle { get { return process.MainWindowHandle; } }
            public void Dispose() { process.Dispose(); }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (var reader in readers.Values) reader.Dispose();
                readers.Clear();
                System.Threading.Volatile.Write(ref characterCache, new VanillaCharacterIdentity[0]);
                clock.Stop();
            }
        }

        private sealed class Reader : IClientReader
        {
            private readonly int processId;
            private readonly MemoryStateSource source;
            private readonly VanillaStateAdapter adapter;
            private readonly string fingerprint, build;
            private string stoppedError;
            private bool disposed;
            private VanillaFleetClientInfo last;

            public bool IsStopped { get { return disposed || stoppedError != null || source?.IsStopped == true; } }

            public static Reader Failed(int processId, string error) { return new Reader(processId, error); }

            private Reader(int processId, string error)
            {
                this.processId = processId;
                stoppedError = error;
            }

            public Reader(string baseDirectory, int processId)
            {
                this.processId = processId;
                ReadOnlyProcessMemory memory = null;
                try
                {
                    memory = new ReadOnlyProcessMemory(processId);
                    if (!string.Equals(memory.ProcessName, "Vanilla MMO", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Selected process is not Vanilla MMO.");
                    if (memory.PointerSize != 4) throw new InvalidOperationException("Only the 32-bit Vanilla client is supported by the current build profile.");
                    var identity = VanillaExecutableIdentity.Read(memory.ExecutablePath);
                    fingerprint = identity.Sha256;
                    var profile = VanillaBuildProfile.Find(Path.Combine(baseDirectory, "VanillaBuilds"), identity, _ => { });
                    if (profile == null) throw new InvalidOperationException("No verified Vanilla build profile matches this executable.");
                    build = profile.Label;
                    adapter = new VanillaStateAdapter(profile, identity);
                    source = new MemoryStateSource(memory, profile.MemoryMap);
                    memory = null; // MemoryStateSource owns it now.
                }
                finally { memory?.Dispose(); }
            }

            public VanillaFleetClientInfo Poll(TimeSpan now)
            {
                if (disposed) return last ?? ErrorInfo("Observation closed.");
                if (stoppedError != null) return last = ErrorInfo(stoppedError);
                try
                {
                    var snapshot = source.Poll(DateTimeOffset.UtcNow);
                    snapshot.Fingerprint = fingerprint;
                    snapshot.BuildProfile = build;
                    var observation = adapter.Observe(snapshot, now);
                    if (source.IsStopped || snapshot.Error != null)
                    {
                        stoppedError = snapshot.Error ?? source.Status;
                        source.Dispose();
                        return last = ErrorInfo(stoppedError);
                    }
                    return last = BuildInfo(snapshot, observation);
                }
                catch (Exception ex)
                {
                    stoppedError = ex.Message;
                    source.Dispose();
                    return last = ErrorInfo(stoppedError);
                }
            }

            private VanillaFleetClientInfo BuildInfo(VanillaClientState state, RuleObservation observation)
            {
                string name = state.CharacterName.IsAvailable ? state.CharacterName.Value : "Unknown character";
                var location = BuildLocation(state);
                return new VanillaFleetClientInfo
                {
                    ProcessId = processId,
                    Position = new VanillaPositionSample(processId, state.SessionId, state.SampledAtUtc,
                        state.X.IsAvailable ? (int?)state.X.Value : null, state.Y.IsAvailable ? (int?)state.Y.Value : null,
                        state.Map.IsAvailable && state.Map.Validation == StateValidation.Valid ? state.Map.Value : null,
                        state.X.Validation == StateValidation.Valid && state.Y.Validation == StateValidation.Valid,
                        state.Error, state.LastMovementAtUtc),
                    CharacterName = name,
                    Identity = VanillaCharacterIdentity.FromState(state),
                    CurrentHP = state.CurrentHP.IsAvailable ? (uint?)state.CurrentHP.Value : null,
                    MaxHP = state.MaxHP.IsAvailable ? (uint?)state.MaxHP.Value : null,
                    CurrentSP = state.CurrentSP.IsAvailable ? (uint?)state.CurrentSP.Value : null,
                    MaxSP = state.MaxSP.IsAvailable ? (uint?)state.MaxSP.Value : null,
                    CurrentWeight = state.CurrentWeight.IsAvailable ? (uint?)state.CurrentWeight.Value : null,
                    MaxWeight = state.MaxWeight.IsAvailable ? (uint?)state.MaxWeight.Value : null,
                    CurrentCartWeight = state.CurrentCartWeight.IsAvailable ? (uint?)state.CurrentCartWeight.Value : null,
                    MaxCartWeight = state.MaxCartWeight.IsAvailable ? (uint?)state.MaxCartWeight.Value : null,
                    HpVerified = state.CurrentHP.Validation == StateValidation.Valid && state.MaxHP.Validation == StateValidation.Valid,
                    SpVerified = state.CurrentSP.Validation == StateValidation.Valid && state.MaxSP.Validation == StateValidation.Valid,
                    NameVerified = state.CharacterName.Validation == StateValidation.Valid,
                    WeightVerified = state.CurrentWeight.Validation == StateValidation.Valid && state.MaxWeight.Validation == StateValidation.Valid,
                    CartWeightVerified = state.CurrentCartWeight.Validation == StateValidation.Valid && state.MaxCartWeight.Validation == StateValidation.Valid,
                    Snapshot = state,
                    Location = location,
                    Build = build,
                    Ready = observation.Ready,
                    Error = null
                };
            }

            private static string BuildLocation(VanillaClientState state)
            {
                bool map = state.Map.IsAvailable && state.Map.Validation == StateValidation.Valid;
                bool xy = state.X.IsAvailable && state.Y.IsAvailable
                    && state.X.Validation == StateValidation.Valid && state.Y.Validation == StateValidation.Valid;
                if (map && xy) return state.Map.Value + "  (" + state.X.Value + ", " + state.Y.Value + ")";
                if (map) return state.Map.Value;
                if (xy) return "(" + state.X.Value + ", " + state.Y.Value + ")";
                return "Location mapping pending";
            }

            private VanillaFleetClientInfo ErrorInfo(string error)
            {
                return new VanillaFleetClientInfo
                {
                    ProcessId = processId,
                    CharacterName = "Vanilla MMO",
                    Location = "Unavailable",
                    Build = build,
                    Error = error,
                    Ready = false
                };
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                source?.Dispose();
            }
        }
    }

    /// <summary>Always-visible two-client status strip for the Vanilla-first workspace.</summary>
    public sealed class VanillaFleetDashboardPanel : UserControl
    {
        private readonly VanillaFleetMonitor monitor;
        private readonly VanillaReconnectSupervisor supervisor;
        private readonly bool observeClients;
        private readonly Timer timer = new Timer { Interval = 500 };
        private readonly ClientCard[] cards = { new ClientCard("Client 1"), new ClientCard("Client 2") };
        private readonly Label extra = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 4, 0, 0) };

        internal bool IsPolling { get { return timer.Enabled; } }
        public event EventHandler PresentationChanged;

        public VanillaFleetDashboardPanel(VanillaFleetMonitor monitor, bool observeClients = true,
            VanillaReconnectSupervisor supervisor = null)
        {
            this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            this.supervisor = supervisor;
            this.observeClients = observeClients;
            Dock = DockStyle.Top;
            Height = 150;
            MinimumSize = new Size(600, 145);
            BackColor = Color.White;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(4) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(cards[0], 0, 0);
            root.Controls.Add(cards[1], 1, 0);
            root.Controls.Add(extra, 0, 1);
            root.SetColumnSpan(extra, 2);
            Controls.Add(root);
            timer.Tick += (s, e) => RefreshNow();
            if (observeClients) timer.Start();
            RefreshNow();
        }

        public void RefreshNow()
        {
            if (!observeClients)
            {
                SetText(extra, "Offline UI validation: live client observation is disabled.");
                return;
            }
            IReadOnlyList<VanillaFleetClientInfo> clients;
            string observationError = null;
            try { clients = monitor.Poll(); }
            catch (Exception ex)
            {
                clients = new VanillaFleetClientInfo[0];
                observationError = ex.Message;
            }
            RenderSnapshot(clients, supervisor?.Settings.Accounts ?? new List<VanillaReconnectAccount>(),
                supervisor?.Statuses() ?? new VanillaReconnectStatus[0],
                supervisor?.FarmingStopStatuses() ?? new VanillaFarmingStopStatus[0], DateTimeOffset.UtcNow, observationError);
            if (observationError != null) { SetText(extra, "Live memory observation error: " + observationError); return; }
            int unavailable = clients.Count(client => client.Error != null);
            if (unavailable > 0) SetText(extra, clients.Count + " Vanilla processes detected; observation stopped for " + unavailable
                + ". Hover over a client observation error for details.");
            else if (clients.Count <= 2) SetText(extra, clients.Count == 0
                ? "No Vanilla clients running. Saved character status remains visible above."
                : "Live HP, SP, carried weight, Cart weight and location are read from the selected Vanilla build's verified read-only memory map.");
            else SetText(extra, clients.Count + " Vanilla processes detected; the dashboard shows the first two only.");
        }

        // Saved identities own card positions, so a closed first client cannot be
        // silently replaced by its healthy sibling or by a reused PID.
        internal void RenderSnapshot(IReadOnlyList<VanillaFleetClientInfo> clients,
            IReadOnlyList<VanillaReconnectAccount> accounts, IReadOnlyList<VanillaReconnectStatus> runtimes,
            IReadOnlyList<VanillaFarmingStopStatus> stops, DateTimeOffset now, string observationError = null)
        {
            var configured = accounts.Where(a => a.Enabled && VanillaCharacterRoster.Key(a) != null).Take(cards.Length).ToArray();
            var used = new HashSet<int>();
            for (int i = 0; i < configured.Length; i++)
            {
                VanillaReconnectAccount account = configured[i];
                var stop = StopForAccount(account, stops);
                var identity = VanillaCharacterRoster.FindUnique(account, clients.Select(c => c.Identity),
                    clients.Select(c => c.ProcessId), now);
                VanillaFleetClientInfo info = identity == null ? null : clients.First(c => c.ProcessId == identity.ProcessId);
                if (info != null) used.Add(info.ProcessId);
                // A confirmed offline stop is stronger than an observation captured
                // immediately before close. Historical resources stay in Detail only.
                if (stop != null && (info == null || stop.ProcessId != info.ProcessId)) info = null;
                var runtime = runtimes.FirstOrDefault(r => string.Equals(r.AccountId, account.Id, StringComparison.Ordinal));
                string error = observationError;
                if (error == null && info == null && runtime?.ProcessId.HasValue == true)
                {
                    // Error text can describe the assigned PID without claiming that
                    // its unreadable identity or resources were verified.
                    var unavailable = clients.FirstOrDefault(c => c.ProcessId == runtime.ProcessId
                        && c.Identity == null && !string.IsNullOrWhiteSpace(c.Error));
                    if (unavailable != null) error = "Observation for assigned PID " + runtime.ProcessId.Value + ": " + unavailable.Error;
                }
                cards[i].ShowManagedClient(info, account, runtime, stop, error);
            }
            var remaining = clients.Where(c => !used.Contains(c.ProcessId)).ToArray();
            for (int i = configured.Length; i < cards.Length; i++)
            {
                int index = i - configured.Length;
                if (observationError != null) cards[i].ShowObservationUnavailable(observationError);
                else cards[i].ShowClient(index < remaining.Length ? remaining[index] : null);
            }
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }

        internal static VanillaFarmingStopStatus StopForAccount(VanillaReconnectAccount account,
            IEnumerable<VanillaFarmingStopStatus> stops)
        {
            string key = VanillaCharacterRoster.Key(account);
            return key == null ? null : stops.Where(s => s != null
                && VanillaCharacterRoster.Key(s.UserName, s.CharacterName) == key)
                .OrderByDescending(s => string.Equals(s.Status, "Emergency", StringComparison.Ordinal)).FirstOrDefault();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        private static void SetText(Label label, string text)
        {
            if (!string.Equals(label.Text, text, StringComparison.Ordinal)) label.Text = text;
        }

        private sealed class ClientCard : GroupBox
        {
            private readonly Label title = new Label { AutoSize = true, Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
            private readonly Label hp = MetricLabel();
            private readonly Label sp = MetricLabel();
            private readonly Label weight = MetricLabel();
            private readonly Label cartWeight = MetricLabel();
            private readonly Label location = new Label { AutoSize = true, ForeColor = Color.DimGray };
            private readonly Label state = new Label { AutoSize = true, ForeColor = Color.DimGray };
            private readonly Label stopDetail = new Label { AutoSize = true, Visible = false, ForeColor = Color.Firebrick };
            private readonly StaticLevelBar hpBar = MetricBar();
            private readonly StaticLevelBar spBar = MetricBar();
            private readonly StaticLevelBar weightBar = MetricBar();
            private readonly StaticLevelBar cartWeightBar = MetricBar();
            private readonly string emptyTitle;
            private readonly ToolTip errorTip = new ToolTip { AutoPopDelay = 30000 };

            public ClientCard(string emptyTitle)
            {
                this.emptyTitle = emptyTitle;
                Dock = DockStyle.Fill;
                Margin = new Padding(4);
                Padding = new Padding(10);
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 6 };
                for (int i = 0; i < 4; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 4);
                layout.Controls.Add(hp, 0, 1);
                layout.Controls.Add(sp, 1, 1);
                layout.Controls.Add(weight, 2, 1);
                layout.Controls.Add(cartWeight, 3, 1);
                layout.Controls.Add(hpBar, 0, 2);
                layout.Controls.Add(spBar, 1, 2);
                layout.Controls.Add(weightBar, 2, 2);
                layout.Controls.Add(cartWeightBar, 3, 2);
                layout.Controls.Add(location, 0, 3); layout.SetColumnSpan(location, 4);
                layout.Controls.Add(state, 0, 4); layout.SetColumnSpan(state, 4);
                layout.Controls.Add(stopDetail, 0, 5); layout.SetColumnSpan(stopDetail, 4);
                layout.SizeChanged += (s, e) =>
                    stopDetail.MaximumSize = new Size(Math.Max(1, layout.ClientSize.Width - stopDetail.Margin.Horizontal), 0);
                Controls.Add(layout);
                ShowClient(null);
            }

            private static Label MetricLabel()
            {
                return new Label { AutoSize = true, Margin = new Padding(0, 2, 8, 0) };
            }

            private static StaticLevelBar MetricBar()
            {
                return new StaticLevelBar { Height = 6, Width = 112, Anchor = AnchorStyles.Left, Margin = new Padding(0, 1, 8, 1) };
            }

            public void ShowClient(VanillaFleetClientInfo info)
            {
                SetText(state, info == null ? "Offline" : "Running");
                state.ForeColor = Color.DimGray;
                SetText(stopDetail, ""); stopDetail.Visible = false;
                errorTip.SetToolTip(this, info?.Error);
                errorTip.SetToolTip(location, info?.Error);
                if (info == null)
                {
                    if (!string.Equals(Text, emptyTitle, StringComparison.Ordinal)) Text = emptyTitle;
                    SetText(title, "Not running");
                    SetText(hp, "HP —");
                    SetText(sp, "SP —");
                    SetText(weight, "Weight —");
                    SetText(cartWeight, "Cart —");
                    hpBar.Value = spBar.Value = weightBar.Value = cartWeightBar.Value = 0;
                    SetText(location, "Location —");
                    return;
                }

                string caption = "PID " + info.ProcessId;
                if (!string.Equals(Text, caption, StringComparison.Ordinal)) Text = caption;
                SetText(title, info.CharacterName + (info.NameVerified ? "" : "  [unverified name]"));
                SetText(hp, "HP " + Vital(info.CurrentHP, info.MaxHP) + (info.HpVerified ? "" : " [unverified]"));
                SetText(sp, "SP " + Vital(info.CurrentSP, info.MaxSP) + (info.SpVerified ? "" : " [unverified]"));
                SetText(weight, "Weight " + Vital(info.CurrentWeight, info.MaxWeight) + (info.WeightVerified ? "" : " [unverified]"));
                SetText(cartWeight, "Cart " + Vital(info.CurrentCartWeight, info.MaxCartWeight)
                    + (info.CartWeightVerified ? "" : " [unverified]"));
                hpBar.Value = Clamp(info.HpPercent);
                spBar.Value = Clamp(info.SpPercent);
                weightBar.Value = Clamp(info.WeightPercent);
                cartWeightBar.Value = Clamp(info.CartWeightPercent);
                string locationText = "Location: " + info.Location;
                if (info.CartWeightVerified && info.CurrentCartWeight.HasValue && info.MaxCartWeight.HasValue
                    && info.MaxCartWeight.Value >= info.CurrentCartWeight.Value)
                    locationText += "   |   Cart left " + (info.MaxCartWeight.Value - info.CurrentCartWeight.Value);
                SetText(location, info.Error == null ? locationText : "Observation: " + info.Error);
            }

            public void ShowManagedClient(VanillaFleetClientInfo info, VanillaReconnectAccount account,
                VanillaReconnectStatus runtime, VanillaFarmingStopStatus stop, string observationError)
            {
                ShowClient(info);
                SetText(title, account.CharacterName);
                bool assignedUnverified = info == null && runtime?.ProcessId.HasValue == true
                    && (stop == null || stop.ProcessId.HasValue);
                bool stoppedRuntime = runtime != null && (runtime.Stage == VanillaReconnectStage.Error
                    || (runtime.Stage == VanillaReconnectStage.Stopped && !string.IsNullOrWhiteSpace(runtime.Detail)
                        && !string.Equals(runtime.Detail.Trim(), "Stopped", StringComparison.OrdinalIgnoreCase)));
                Text = info != null ? "PID " + info.ProcessId
                    : assignedUnverified ? "PID " + runtime.ProcessId.Value + " [unverified]" : "Offline";
                if (info == null && (observationError != null || assignedUnverified)) SetText(location, "Observation unavailable");
                SetText(state, stop?.Status ?? (stoppedRuntime ? runtime.Stage.ToString()
                    : info == null ? (assignedUnverified ? "Observation unavailable" : "Offline")
                    : runtime == null ? "Running" : runtime.Stage == VanillaReconnectStage.Stopped ? "Running (recovery off)"
                    : VanillaAutobattleStatus.Compact(runtime.Stage, runtime.Detail)));
                state.ForeColor = stop == null ? (runtime?.Stage == VanillaReconnectStage.Error || observationError != null ? Color.Firebrick : Color.DimGray)
                    : stop.Status == "Completed" ? Color.DarkGreen : Color.Firebrick;
                stopDetail.ForeColor = state.ForeColor;
                SetText(stopDetail, stop?.Detail ?? (stoppedRuntime ? runtime.Detail : info == null ? observationError : "") ?? "");
                stopDetail.Visible = !string.IsNullOrWhiteSpace(stopDetail.Text);
                errorTip.SetToolTip(state, stop?.Detail ?? runtime?.Detail);
                errorTip.SetToolTip(this, stop?.Detail ?? observationError ?? info?.Error);
            }

            public void ShowObservationUnavailable(string error)
            {
                ShowClient(null);
                SetText(title, "Status unavailable");
                SetText(location, "Observation: " + error);
                errorTip.SetToolTip(this, error);
                errorTip.SetToolTip(location, error);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) errorTip.Dispose();
                base.Dispose(disposing);
            }

            private static string Vital(uint? current, uint? maximum)
            {
                return current.HasValue && maximum.HasValue ? current.Value + "/" + maximum.Value : "Unavailable";
            }
            private static int Clamp(decimal? value) { return value.HasValue ? Math.Max(0, Math.Min(100, (int)Math.Round(value.Value))) : 0; }
        }

        private sealed class StaticLevelBar : Control
        {
            private int value;

            public StaticLevelBar()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Gainsboro;
                ForeColor = Color.Green;
            }

            public int Value
            {
                get { return value; }
                set
                {
                    int next = Math.Max(0, Math.Min(100, value));
                    if (this.value == next) return;
                    this.value = next;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.Clear(BackColor);
                int width = (int)Math.Round(ClientSize.Width * (value / 100d));
                if (width <= 0 || ClientSize.Height <= 0) return;
                using (var brush = new SolidBrush(ForeColor))
                    e.Graphics.FillRectangle(brush, 0, 0, Math.Min(width, ClientSize.Width), ClientSize.Height);
            }
        }
    }
}
