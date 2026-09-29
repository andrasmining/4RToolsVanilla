using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace _4RTools.Model.Vanilla
{
    public enum VanillaFarmAutoSource
    {
        Manual = 0,
        Use = 1,
        Equip = 2,
        Etc = 3,
        Any = 4
    }

    public sealed class VanillaFarmMonitorItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public decimal ZenyPerItem { get; set; }
        public long Count { get; set; }
        public VanillaFarmAutoSource AutoSource { get; set; }
        public uint UnitWeight { get; set; } = 1;

        public VanillaFarmMonitorItem Clone()
        {
            return new VanillaFarmMonitorItem
            {
                Id = Id,
                Name = Name,
                ZenyPerItem = ZenyPerItem,
                Count = Count,
                AutoSource = AutoSource,
                UnitWeight = UnitWeight
            };
        }

        internal void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
            Name = (Name ?? "").Trim();
            if (UnitWeight == 0) UnitWeight = 1;
        }

        internal void Validate()
        {
            Normalize();
            if (Id.Length > 64 || Id.Any(char.IsControl))
                throw new ArgumentException("Farming-monitor item ID is invalid.");
            if (Name.Length > 80 || Name.Any(char.IsControl))
                throw new ArgumentException("Farming-monitor item name is invalid.");
            if (ZenyPerItem < 0m || ZenyPerItem > 1000000000000m)
                throw new ArgumentException("Zeny per item must be between 0 and 1,000,000,000,000.");
            if (Count < 0 || Count > 1000000000000L)
                throw new ArgumentException("Item count must be between 0 and 1,000,000,000,000.");
            if (!Enum.IsDefined(typeof(VanillaFarmAutoSource), AutoSource))
                throw new ArgumentException("Automatic farming-monitor source is invalid.");
            if (UnitWeight < 1 || UnitWeight > 10000)
                throw new ArgumentException("Item unit weight must be between 1 and 10,000.");
        }
    }

    public sealed class VanillaFarmMonitorSession
    {
        public string AccountId { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Running { get; set; }
        public long ElapsedTicks { get; set; }
        public DateTimeOffset? RunningSinceUtc { get; set; }
        public long UnassignedUseWeight { get; set; }
        public long UnassignedEquipWeight { get; set; }
        public long UnassignedEtcWeight { get; set; }

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<VanillaFarmMonitorItem> Items { get; set; } = new List<VanillaFarmMonitorItem>();

        internal void Normalize()
        {
            AccountId = (AccountId ?? "").Trim();
            Label = (Label ?? "").Trim();
            if (ElapsedTicks < 0) ElapsedTicks = 0;
            if (UnassignedUseWeight < 0) UnassignedUseWeight = 0;
            if (UnassignedEquipWeight < 0) UnassignedEquipWeight = 0;
            if (UnassignedEtcWeight < 0) UnassignedEtcWeight = 0;
            if (Items == null) Items = new List<VanillaFarmMonitorItem>();
            foreach (VanillaFarmMonitorItem item in Items)
                if (item != null) item.Normalize();
        }

        internal void Validate()
        {
            Normalize();
            if (string.IsNullOrWhiteSpace(AccountId) || AccountId.Length > 128 || AccountId.Any(char.IsControl))
                throw new ArgumentException("Farming-monitor account ID is invalid.");
            if (Label.Length > 100 || Label.Any(char.IsControl))
                throw new ArgumentException("Farming-monitor character label is invalid.");
            if (ElapsedTicks > TimeSpan.MaxValue.Ticks)
                throw new ArgumentException("Farming-monitor elapsed time is invalid.");
            if (Items.Count > 50)
                throw new ArgumentException("A farming monitor supports at most 50 item rows.");

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var automaticSources = new HashSet<VanillaFarmAutoSource>();
            foreach (VanillaFarmMonitorItem item in Items)
            {
                if (item == null) throw new ArgumentException("Farming-monitor item rows cannot be null.");
                item.Validate();
                if (!ids.Add(item.Id)) throw new ArgumentException("Farming-monitor item IDs must be unique.");
                if (item.AutoSource != VanillaFarmAutoSource.Manual && !automaticSources.Add(item.AutoSource))
                    throw new ArgumentException("Only one automatic item row may use each Cart source (Use, Equip, Etc or Any).");
            }
        }

        internal TimeSpan ElapsedAt(DateTimeOffset now)
        {
            long ticks = ElapsedTicks;
            if (Running && RunningSinceUtc.HasValue)
            {
                TimeSpan current = now - RunningSinceUtc.Value;
                if (current > TimeSpan.Zero)
                {
                    long remaining = TimeSpan.MaxValue.Ticks - ticks;
                    ticks += Math.Min(remaining, current.Ticks);
                }
            }
            return TimeSpan.FromTicks(Math.Max(0, ticks));
        }

        internal decimal TotalZeny()
        {
            decimal total = 0m;
            foreach (VanillaFarmMonitorItem item in Items)
                total += item.ZenyPerItem * item.Count;
            return total;
        }
    }

    public sealed class VanillaFarmMonitorDocument
    {
        public int Version { get; set; } = 1;

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<VanillaFarmMonitorSession> Sessions { get; set; } = new List<VanillaFarmMonitorSession>();

        internal void Normalize()
        {
            if (Sessions == null) Sessions = new List<VanillaFarmMonitorSession>();
            foreach (VanillaFarmMonitorSession session in Sessions)
                if (session != null) session.Normalize();
        }

        internal void Validate()
        {
            Normalize();
            if (Version != 1) throw new ArgumentException("Unsupported farming-monitor settings version.");
            if (Sessions.Count > 100) throw new ArgumentException("Too many farming-monitor sessions are stored.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VanillaFarmMonitorSession session in Sessions)
            {
                if (session == null) throw new ArgumentException("Farming-monitor sessions cannot be null.");
                session.Validate();
                if (!ids.Add(session.AccountId)) throw new ArgumentException("Farming-monitor account IDs must be unique.");
            }
        }
    }

    public sealed class VanillaFarmMonitorSnapshot
    {
        public string AccountId { get; internal set; }
        public string Label { get; internal set; }
        public bool Running { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
        public decimal TotalZeny { get; internal set; }
        public decimal ZenyPerHour { get; internal set; }
        public long UnassignedUseWeight { get; internal set; }
        public long UnassignedEquipWeight { get; internal set; }
        public long UnassignedEtcWeight { get; internal set; }
        public IReadOnlyList<VanillaFarmMonitorItem> Items { get; internal set; }

        public long UnassignedWeight
        {
            get { return UnassignedUseWeight + UnassignedEquipWeight + UnassignedEtcWeight; }
        }
    }

    public sealed class VanillaFarmMonitorStore
    {
        private readonly string path;

        public VanillaFarmMonitorStore()
        {
            VanillaAppData.InitializeAndMigrateLegacy(AppDomain.CurrentDomain.BaseDirectory);
            path = Path.Combine(VanillaAppData.RootDirectory, "farm-monitor.json");
        }

        internal VanillaFarmMonitorStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentNullException(nameof(filePath));
            path = Path.GetFullPath(filePath);
        }

        public string FilePath { get { return path; } }

        public VanillaFarmMonitorDocument Load()
        {
            if (!File.Exists(path)) return new VanillaFarmMonitorDocument();
            var value = JsonConvert.DeserializeObject<VanillaFarmMonitorDocument>(File.ReadAllText(path),
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
            if (value == null) throw new InvalidDataException("Farming-monitor settings are empty.");
            value.Validate();
            return value;
        }

        public void Save(VanillaFarmMonitorDocument value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            value.Validate();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path))
            {
                string backup = path + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(temp, path, backup);
            }
            else File.Move(temp, path);
        }
    }

    public sealed class VanillaFarmMonitorService
    {
        private readonly object gate = new object();
        private readonly VanillaFarmMonitorStore store;
        private VanillaFarmMonitorDocument document;

        public VanillaFarmMonitorService()
            : this(new VanillaFarmMonitorStore())
        {
        }

        internal VanillaFarmMonitorService(VanillaFarmMonitorStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            document = store.Load();
        }

        public string StorePath { get { return store.FilePath; } }

        public VanillaFarmMonitorSnapshot Snapshot(string accountId, string label)
        {
            return SnapshotAt(accountId, label, DateTimeOffset.UtcNow);
        }

        internal VanillaFarmMonitorSnapshot SnapshotAt(string accountId, string label, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentNullException(nameof(accountId));
            lock (gate)
            {
                VanillaFarmMonitorSession session = Find(accountId);
                if (session == null)
                    return EmptySnapshot(accountId, label);

                TimeSpan elapsed = session.ElapsedAt(now);
                decimal total = session.TotalZeny();
                decimal perHour = elapsed.TotalSeconds >= 1.0
                    ? total * 3600m / (decimal)elapsed.TotalSeconds
                    : 0m;
                return new VanillaFarmMonitorSnapshot
                {
                    AccountId = session.AccountId,
                    Label = string.IsNullOrWhiteSpace(label) ? session.Label : label,
                    Running = session.Running,
                    Elapsed = elapsed,
                    TotalZeny = total,
                    ZenyPerHour = perHour,
                    UnassignedUseWeight = session.UnassignedUseWeight,
                    UnassignedEquipWeight = session.UnassignedEquipWeight,
                    UnassignedEtcWeight = session.UnassignedEtcWeight,
                    Items = session.Items.Select(item => item.Clone()).ToArray()
                };
            }
        }

        public void SetRunning(string accountId, string label, bool running)
        {
            SetRunningAt(accountId, label, running, DateTimeOffset.UtcNow);
        }

        internal void SetRunningAt(string accountId, string label, bool running, DateTimeOffset now)
        {
            lock (gate)
            {
                VanillaFarmMonitorSession session = GetOrCreate(accountId, label);
                UpdateLabel(session, label);
                if (session.Running == running) return;
                if (running)
                {
                    session.Running = true;
                    session.RunningSinceUtc = now;
                }
                else
                {
                    CommitElapsed(session, now);
                    session.Running = false;
                    session.RunningSinceUtc = null;
                }
                SaveLocked();
            }
        }

        public void Reset(string accountId, string label)
        {
            ResetAt(accountId, label, DateTimeOffset.UtcNow);
        }

        internal void ResetAt(string accountId, string label, DateTimeOffset now)
        {
            lock (gate)
            {
                VanillaFarmMonitorSession session = GetOrCreate(accountId, label);
                UpdateLabel(session, label);
                foreach (VanillaFarmMonitorItem item in session.Items) item.Count = 0;
                session.UnassignedUseWeight = 0;
                session.UnassignedEquipWeight = 0;
                session.UnassignedEtcWeight = 0;
                session.ElapsedTicks = 0;
                session.Running = true;
                session.RunningSinceUtc = now;
                SaveLocked();
            }
        }

        public void ReplaceItems(string accountId, string label, IEnumerable<VanillaFarmMonitorItem> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            lock (gate)
            {
                VanillaFarmMonitorSession session = GetOrCreate(accountId, label);
                if (session.Running)
                    throw new InvalidOperationException("Pause the farming monitor before editing item rows.");
                var replacement = items.Select(item =>
                {
                    if (item == null) throw new ArgumentException("Farming-monitor item rows cannot be null.", nameof(items));
                    return item.Clone();
                }).ToList();
                var probe = new VanillaFarmMonitorSession
                {
                    AccountId = session.AccountId,
                    Label = string.IsNullOrWhiteSpace(label) ? session.Label : label.Trim(),
                    Items = replacement
                };
                probe.Validate();
                UpdateLabel(session, label);
                session.Items = replacement;
                SaveLocked();
            }
        }

        public void RecordCartTransfer(string accountId, string label, VanillaFarmAutoSource source, uint cartWeightDelta)
        {
            RecordCartTransferAt(accountId, label, source, cartWeightDelta, DateTimeOffset.UtcNow);
        }

        internal void RecordCartTransferAt(string accountId, string label, VanillaFarmAutoSource source,
            uint cartWeightDelta, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(accountId) || cartWeightDelta == 0) return;
            if (source != VanillaFarmAutoSource.Use && source != VanillaFarmAutoSource.Equip
                && source != VanillaFarmAutoSource.Etc) return;

            string log = null;
            lock (gate)
            {
                VanillaFarmMonitorSession session = Find(accountId);
                if (session == null || !session.Running) return;
                UpdateLabel(session, label);
                CommitElapsed(session, now);
                session.RunningSinceUtc = now;

                VanillaFarmMonitorItem item = session.Items.FirstOrDefault(row => row.AutoSource == source)
                    ?? session.Items.FirstOrDefault(row => row.AutoSource == VanillaFarmAutoSource.Any);
                if (item == null || item.UnitWeight == 0 || cartWeightDelta % item.UnitWeight != 0)
                {
                    AddUnassigned(session, source, cartWeightDelta);
                    SaveLocked();
                    log = "event=farm-monitor-unassigned accountId=" + accountId
                        + " source=" + source + " cartWeightDelta=" + cartWeightDelta
                        + " reason='" + (item == null ? "no matching automatic row" : "delta not divisible by configured unit weight") + "'.";
                }
                else
                {
                    long quantity = cartWeightDelta / item.UnitWeight;
                    if (quantity <= 0) return;
                    if (item.Count > 1000000000000L - quantity)
                        throw new InvalidOperationException("Farming-monitor item count would exceed its supported range.");
                    item.Count += quantity;
                    SaveLocked();
                    log = "event=farm-monitor-count accountId=" + accountId
                        + " source=" + source + " cartWeightDelta=" + cartWeightDelta
                        + " item='" + (item.Name ?? "").Replace("'", "") + "' unitWeight=" + item.UnitWeight
                        + " added=" + quantity + " count=" + item.Count + ".";
                }
            }
            if (!string.IsNullOrWhiteSpace(log)) VanillaDebugLog.Write("FARM", log);
        }

        private static VanillaFarmMonitorSnapshot EmptySnapshot(string accountId, string label)
        {
            return new VanillaFarmMonitorSnapshot
            {
                AccountId = accountId,
                Label = label ?? "",
                Running = false,
                Elapsed = TimeSpan.Zero,
                TotalZeny = 0m,
                ZenyPerHour = 0m,
                Items = new VanillaFarmMonitorItem[0]
            };
        }

        private VanillaFarmMonitorSession Find(string accountId)
        {
            return document.Sessions.FirstOrDefault(session =>
                string.Equals(session.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
        }

        private VanillaFarmMonitorSession GetOrCreate(string accountId, string label)
        {
            if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentNullException(nameof(accountId));
            VanillaFarmMonitorSession session = Find(accountId);
            if (session != null) return session;
            session = new VanillaFarmMonitorSession { AccountId = accountId.Trim(), Label = (label ?? "").Trim() };
            document.Sessions.Add(session);
            return session;
        }

        private static void UpdateLabel(VanillaFarmMonitorSession session, string label)
        {
            if (!string.IsNullOrWhiteSpace(label)) session.Label = label.Trim();
        }

        private static void CommitElapsed(VanillaFarmMonitorSession session, DateTimeOffset now)
        {
            if (!session.Running || !session.RunningSinceUtc.HasValue) return;
            TimeSpan delta = now - session.RunningSinceUtc.Value;
            if (delta <= TimeSpan.Zero) return;
            long remaining = TimeSpan.MaxValue.Ticks - session.ElapsedTicks;
            session.ElapsedTicks += Math.Min(remaining, delta.Ticks);
        }

        private static void AddUnassigned(VanillaFarmMonitorSession session, VanillaFarmAutoSource source, uint delta)
        {
            switch (source)
            {
                case VanillaFarmAutoSource.Use: session.UnassignedUseWeight += delta; break;
                case VanillaFarmAutoSource.Equip: session.UnassignedEquipWeight += delta; break;
                case VanillaFarmAutoSource.Etc: session.UnassignedEtcWeight += delta; break;
            }
        }

        private void SaveLocked()
        {
            document.Validate();
            store.Save(document);
        }
    }
}
