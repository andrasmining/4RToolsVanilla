using System;
using System.Collections.Generic;
using System.Linq;

namespace _4RTools.Model.Vanilla
{
    /// <summary>Only independently verified fields from one read-only client snapshot.</summary>
    internal sealed class VanillaCharacterIdentity
    {
        internal readonly int ProcessId;
        internal readonly Guid Session;
        internal readonly DateTimeOffset At;
        internal readonly string CharacterName, UserName;
        internal readonly int? CharacterSlot;

        internal VanillaCharacterIdentity(int processId, Guid session, DateTimeOffset at,
            string characterName, string userName = null, int? characterSlot = null)
        {
            ProcessId = processId; Session = session; At = at;
            CharacterName = KnownText(characterName, 80);
            UserName = KnownText(userName, 128);
            CharacterSlot = characterSlot >= 1 && characterSlot <= 15 ? characterSlot : null;
        }

        internal bool IsFresh(DateTimeOffset now)
        {
            return ProcessId > 0 && Session != Guid.Empty && CharacterName != null
                && now >= At && now - At <= TimeSpan.FromSeconds(3);
        }

        internal static string KnownText(string value, int max)
        {
            return string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)
                ? null : value.Trim();
        }

        internal static VanillaCharacterIdentity FromState(VanillaClientState state)
        {
            if (state == null || state.IsDemo || state.Error != null || !state.ProcessId.HasValue) return null;
            Func<VanillaField, bool> valid = field => state.Fields != null && state.Fields.ContainsKey(field)
                && state.Fields[field] != null && state.Fields[field].IsAvailable && state.Fields[field].Validation == StateValidation.Valid
                && state.Fields[field].LastObservedAtUtc == state.SampledAtUtc;
            if ((valid(VanillaField.Loading) && state.Loading.Value)
                || (valid(VanillaField.ClientReady) && !state.ClientReady.Value)) return null;
            return new VanillaCharacterIdentity(state.ProcessId.Value, state.SessionId, state.SampledAtUtc,
                valid(VanillaField.CharacterName) ? state.CharacterName.Value : null,
                valid(VanillaField.UserName) ? state.UserName.Value : null,
                valid(VanillaField.CharacterSlot) ? (int?)state.CharacterSlot.Value : null);
        }
    }

    internal static class VanillaCharacterRoster
    {
        internal static bool Same(string left, string right)
        { return string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal); }

        // Length-prefixing prevents separator collisions. A missing component is not an ID.
        internal static string Key(string userName, string characterName)
        {
            string user = VanillaCharacterIdentity.KnownText(userName, 128);
            string name = VanillaCharacterIdentity.KnownText(characterName, 80);
            return user == null || name == null ? null : user.Length + ":" + user + name.Length + ":" + name;
        }
        internal static string Key(VanillaReconnectAccount row) { return row == null ? null : Key(row.UserName, row.CharacterName); }
        internal static string Key(VanillaCharacterIdentity identity) { return identity == null ? null : Key(identity.UserName, identity.CharacterName); }

        internal static bool Compatible(VanillaReconnectAccount row, VanillaCharacterIdentity identity)
        {
            return row != null && identity != null && Key(identity) != null
                && (string.IsNullOrWhiteSpace(row.CharacterName) || Same(row.CharacterName, identity.CharacterName))
                && (string.IsNullOrWhiteSpace(row.UserName) || Same(row.UserName, identity.UserName))
                && (!row.CharacterSlot.HasValue || !identity.CharacterSlot.HasValue || row.CharacterSlot == identity.CharacterSlot);
        }

        internal static bool Matches(VanillaReconnectAccount row, VanillaCharacterIdentity identity, DateTimeOffset now)
        {
            return row != null && identity != null && identity.IsFresh(now) && Key(row) != null
                && Key(row) == Key(identity) && Compatible(row, identity);
        }

        internal static VanillaCharacterIdentity FindUnique(VanillaReconnectAccount row,
            IEnumerable<VanillaCharacterIdentity> observed, IEnumerable<int> eligiblePids, DateTimeOffset now)
        {
            var eligible = new HashSet<int>(eligiblePids ?? Enumerable.Empty<int>());
            var fresh = (observed ?? Enumerable.Empty<VanillaCharacterIdentity>())
                .Where(i => i != null && i.IsFresh(now)).ToArray();
            var matches = fresh.Where(i => eligible.Contains(i.ProcessId) && Matches(row, i, now)
                && fresh.Count(j => j.ProcessId == i.ProcessId) == 1).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        internal static bool FillMissing(VanillaReconnectAccount row, VanillaCharacterIdentity identity)
        {
            if (!Compatible(row, identity)) return false;
            bool changed = false;
            if (string.IsNullOrWhiteSpace(row.CharacterName))
            { row.CharacterName = identity.CharacterName; changed = true; }
            if (string.IsNullOrWhiteSpace(row.UserName))
            { row.UserName = identity.UserName; changed = true; }
            if (!row.CharacterSlot.HasValue && identity.CharacterSlot.HasValue)
            { row.CharacterSlot = identity.CharacterSlot; changed = true; }
            return changed;
        }

        internal static bool MergeObserved(IList<VanillaReconnectAccount> rows,
            IEnumerable<VanillaCharacterIdentity> observed, DateTimeOffset now, ISet<string> ignored = null)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            bool changed = false;
            var fresh = (observed ?? Enumerable.Empty<VanillaCharacterIdentity>())
                .Where(i => i != null && i.IsFresh(now)).ToArray();
            var unique = fresh.Where(i => Key(i) != null && fresh.Count(j => j.ProcessId == i.ProcessId) == 1)
                .GroupBy(Key, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
            foreach (var identity in unique)
            {
                string key = Key(identity);
                var exact = rows.Where(r => Key(r) == key).ToArray();
                if (exact.Length > 0)
                {
                    if (exact.Length == 1) changed |= FillMissing(exact[0], identity);
                    continue;
                }

                // Legacy account rows have a username but no character name. Enrich the
                // existing configured ID only when both the row and live account are unique.
                // If slots are independently known they can disambiguate; never invent one.
                var legacy = rows.Where(r => string.IsNullOrWhiteSpace(r.CharacterName)
                    && !string.IsNullOrWhiteSpace(r.UserName) && Compatible(r, identity)).ToArray();
                if (legacy.Length > 0)
                {
                    var orphan = rows.Where(r => string.IsNullOrWhiteSpace(r.UserName)
                        && Same(r.CharacterName, identity.CharacterName)).ToArray();
                    if (legacy.Length == 1 && fresh.Count(i => i.UserName == null || Compatible(legacy[0], i)) == 1
                        && orphan.All(IsUntouchedNameOnlyDiscovery)
                        && (orphan.Length == 0 || fresh.Count(i => Same(i.CharacterName, identity.CharacterName)) == 1))
                    {
                        changed |= FillMissing(legacy[0], identity);
                        // Undo only v0.6.37's empty auto-created duplicate. Configured IDs,
                        // secrets, proxy selections, hotkeys and enabled rows are never removed.
                        foreach (var unused in orphan) { rows.Remove(unused); changed = true; }
                    }
                    continue; // Ambiguity must not create yet another row.
                }

                var incomplete = rows.Where(r => string.IsNullOrWhiteSpace(r.UserName)
                    && Same(r.CharacterName, identity.CharacterName)).ToArray();
                if (incomplete.Length > 0)
                {
                    if (incomplete.Length == 1 && fresh.Count(i => Same(i.CharacterName, identity.CharacterName)) == 1
                        && !rows.Any(r => Key(r) != null && Same(r.CharacterName, identity.CharacterName)))
                        changed |= FillMissing(incomplete[0], identity);
                    continue;
                }
                if (ignored != null && ignored.Contains(key)) continue;
                rows.Add(new VanillaReconnectAccount
                {
                    Label = identity.CharacterName, CharacterName = identity.CharacterName,
                    UserName = identity.UserName, CharacterSlot = identity.CharacterSlot,
                    Enabled = false, ProxyNeedsConfiguration = true
                });
                changed = true;
            }
            if (changed && rows.Any(r => !string.IsNullOrWhiteSpace(r.CharacterName)))
                foreach (var empty in rows.Where(IsEmptyDefault).ToArray()) rows.Remove(empty);
            return changed;
        }

        private static bool IsUntouchedNameOnlyDiscovery(VanillaReconnectAccount row)
        {
            var defaults = new VanillaReconnectAccount();
            return row != null && !row.Enabled && string.IsNullOrWhiteSpace(row.UserName)
                && !string.IsNullOrWhiteSpace(row.CharacterName) && row.Label == row.CharacterName
                && string.IsNullOrWhiteSpace(row.ProtectedPassword) && row.ProxyNeedsConfiguration && !row.CharacterSlot.HasValue
                && row.ResumeKey == defaults.ResumeKey && row.ResumeCtrl == defaults.ResumeCtrl
                && row.ResumeAlt == defaults.ResumeAlt && row.ResumeShift == defaults.ResumeShift
                && row.WeightEnabled == defaults.WeightEnabled
                && row.CartMaintenanceEnabled == defaults.CartMaintenanceEnabled
                && row.WeightEmailEnabled == defaults.WeightEmailEnabled
                && row.SmartTeleportEnabled == defaults.SmartTeleportEnabled
                && row.SmartTeleportIdleSeconds == defaults.SmartTeleportIdleSeconds
                && row.SmartTeleportKey == defaults.SmartTeleportKey
                && row.SmartTeleportCtrl == defaults.SmartTeleportCtrl
                && row.SmartTeleportAlt == defaults.SmartTeleportAlt
                && row.SmartTeleportShift == defaults.SmartTeleportShift;
        }

        internal static bool IsEmptyDefault(VanillaReconnectAccount row)
        {
            return row != null && string.IsNullOrWhiteSpace(row.CharacterName) && string.IsNullOrWhiteSpace(row.UserName)
                && string.IsNullOrWhiteSpace(row.ProtectedPassword)
                && (row.Label == "Client" || row.Label == "Client 1" || row.Label == "Client 2");
        }

        internal static void Validate(IList<VanillaReconnectAccount> rows)
        {
            if (rows == null || rows.Count == 0) throw new InvalidOperationException("Keep at least one character profile.");
            if (rows.Any(r => r == null || string.IsNullOrWhiteSpace(r.Id))
                || rows.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Character profile IDs must be unique.");
            if (rows.Count(r => r.Enabled) > 2) throw new InvalidOperationException("Only two characters may be enabled at once.");
            if (rows.Where(r => Key(r) != null).GroupBy(Key, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Each username + character name pair must have only one row. Edit the existing character instead.");
            foreach (var row in rows)
            {
                if (KnownInvalid(row.Label, 80) || string.IsNullOrWhiteSpace(row.Label)
                    || KnownInvalid(row.CharacterName, 80) || KnownInvalid(row.UserName, 128))
                    throw new InvalidOperationException("Description, username or character name is invalid.");
                if (row.CharacterSlot.HasValue && (row.CharacterSlot < 1 || row.CharacterSlot > 15))
                    throw new InvalidOperationException("Character slot must be 1 to 15, or unknown.");
                if (row.SmartTeleportIdleSeconds < 5 || row.SmartTeleportIdleSeconds > 3600)
                    throw new InvalidOperationException("Smart Teleport idle time must be between 5 and 3600 seconds.");
                if (row.SmartTeleportEnabled && (row.SmartTeleportKey < 8 || row.SmartTeleportKey > 254))
                    throw new InvalidOperationException("Choose a Smart Teleport hotkey before enabling Smart Teleport for a character.");
            }
        }

        private static bool KnownInvalid(string value, int max)
        { return value != null && (value.Length > max || value.Any(char.IsControl)); }
    }

    public sealed partial class VanillaFleetMonitor
    {
        private IReadOnlyList<VanillaCharacterIdentity> characterCache = new VanillaCharacterIdentity[0];
        internal IReadOnlyList<VanillaCharacterIdentity> LatestCharacters()
        { return System.Threading.Volatile.Read(ref characterCache); }
        private void PublishCharacters(IReadOnlyList<VanillaFleetClientInfo> clients)
        {
            System.Threading.Volatile.Write(ref characterCache,
                Array.AsReadOnly(clients.Where(c => c.Identity != null && c.Error == null).Select(c => c.Identity).ToArray()));
        }
    }
}
