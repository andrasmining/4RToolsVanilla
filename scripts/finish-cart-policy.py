from pathlib import Path
import difflib

ROOT = Path.cwd()
def load(path):
    return (ROOT / path).read_text(encoding='utf-8-sig')
def save(path, text):
    target = ROOT / path
    raw = target.read_bytes()
    old = raw.decode('utf-8-sig').splitlines(keepends=True)
    new = text.splitlines()
    output = []
    for tag, a, b, c, d in difflib.SequenceMatcher(None, [line.rstrip('\r\n') for line in old], new, autojunk=False).get_opcodes():
        output.extend(old[a:b] if tag == 'equal' else [line + '\n' for line in new[c:d]])
    prefix = b'\xef\xbb\xbf' if raw.startswith(b'\xef\xbb\xbf') else b''
    target.write_bytes(prefix + ''.join(output).encode('utf-8'))
def replace(text, old, new, count=1):
    actual = text.count(old)
    if actual != count:
        raise RuntimeError(f'Expected {count} exact matches, found {actual}: {old[:100]}')
    return text.replace(old, new)

path = 'Model/Vanilla/VanillaWeightAlerts.cs'
s = load(path)
s = replace(s, '        internal const int PrecisionCartRetrySeconds = 60;', '''        internal static bool IsCombinedCapacityReached(VanillaWeightObservation observation)
        {
            return observation != null && observation.Verified && observation.CartVerified
                && observation.Percent.HasValue && observation.CartPercent.HasValue
                && VanillaWeightCartAutomation.IsFarmingComplete(
                    observation.CartPercent.Value, observation.Percent.Value);
        }

        // Resolve policy again immediately before dispatch. A queued completion must not
        // reuse Mail/Cart switches or SMTP settings captured before the user edited them.
        private bool TryGetAutomaticMailSettings(int processId, string accountId,
            VanillaWeightMailMode expectedMode, out VanillaWeightAlertSettings current)
        {
            current = null;
            lock (gate)
            {
                if (disposed || timer == null) return false;
                current = settings.Clone();
            }
            if (string.IsNullOrWhiteSpace(accountId)
                || !string.Equals(accountId, supervisor.ManagedAccountIdForProcess(processId),
                    StringComparison.OrdinalIgnoreCase)) return false;
            return ResolveMailMode(current.Enabled, supervisor.IsWeightEmailEnabledForProcess(processId),
                current.AutoCartEnabled, supervisor.IsCartMaintenanceEnabledForProcess(processId)) == expectedMode
                && MilestoneMailConfigured(current);
        }

        internal const int PrecisionCartRetrySeconds = 60;''')
s = replace(s, '''                // Cart-full and DONE mails are independent of the optional carried-weight
                // warning switch. They only require a valid saved SMTP transport.''', '''                // Transport validation is separate from authorization. Every automatic
                // dispatch must also pass the current shared and per-character Mail policy.''')
s = replace(s, '            bool cartAtFullMilestone = observation.CartPercent.Value >= VanillaWeightCartAutomation.CartFullPercent;\n', '')
s = replace(s, '''                // Exact 100% remains the Cart-full mail milestone. Farming completion is
                // intentionally >=99% Cart plus >=50% carried weight.
                if (!cartAtFullMilestone)
                    state.CartFullNotified = false;

''', '''                // Only the combined capacity threshold is a mail event. A full Cart
                // alone still leaves carried capacity available and must not send mail.
''')
start = s.index('            DateTimeOffset now = DateTimeOffset.UtcNow;\n            bool milestoneMail = mailEnabled', s.index('private void ProcessFarmingMilestones'))
end = s.index('            bool alreadyDone;', start)
s = s[:start] + '''            if (!IsCombinedCapacityReached(observation)) return;
            bool milestoneMail = mailEnabled && MilestoneMailConfigured(current);

''' + s[end:]
s = replace(s, '''            bool sendDone;
            lock (gate) sendDone = !state.DoneNotified && DateTimeOffset.UtcNow >= state.NextMilestoneMailAt;
            if (!sendDone) return;
            try
            {
                SendMail(current, "DONE: " + observation.CharacterName,''', '''            lock (gate)
            {
                if (state.DoneNotified || state.Sending || DateTimeOffset.UtcNow < state.NextMilestoneMailAt) return;
                state.Sending = true;
            }
            try
            {
                if (!IsCombinedCapacityReached(observation)
                    || !supervisor.IsWeightCompletedHold(accountId)
                    || !TryGetAutomaticMailSettings(observation.ProcessId, accountId,
                        VanillaWeightMailMode.CartMilestones, out current)) return;
                SendMail(current, "DONE: " + observation.CharacterName,''')
s = replace(s, '''                    + " pid=" + observation.ProcessId + " reason='" + ex.Message + "'.");
            }
        }

        private void ProcessAutoCart''', '''                    + " pid=" + observation.ProcessId + " reason='" + ex.Message + "'.");
            }
            finally { lock (gate) state.Sending = false; }
        }

        private void ProcessAutoCart''')
s = replace(s, 'if (!account.WeightEnabled)\n                throw new InvalidOperationException("Weight/Cart is disabled for this character.");', 'if (!account.EffectiveCartMaintenanceEnabled)\n                throw new InvalidOperationException("Cart maintenance is disabled for this character.");')
s = replace(s, '                state.CartFullNotified = false;\n', '')
s = replace(s, '            public bool CartFullNotified;\n', '')
s = replace(s, '''                state.DoneNotified = false;
                state.NextCartAttemptAt = DateTimeOffset.MinValue;''', '''                state.DoneNotified = false;
                state.NextCartAttemptAt = DateTimeOffset.MinValue;
                state.NextCompletionAttemptAt = DateTimeOffset.MinValue;
                state.NextMilestoneMailAt = DateTimeOffset.MinValue;''')
s = replace(s, '''                string percent = observation.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture);
                string subject = "Weight warning:''', '''                if (!TryGetAutomaticMailSettings(observation.ProcessId, accountId,
                    VanillaWeightMailMode.CarriedWeight, out current)
                    || observation.Percent.Value < current.ThresholdPercent) return;
                string percent = observation.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture);
                string subject = "Weight warning:''')
save(path, s)

path = 'Model/Vanilla/VanillaWeightCartAutomation.cs'
s = replace(load(path), '!runtime.Account.Enabled || !runtime.Account.WeightEnabled || CharacterOwnershipChanged', '!runtime.Account.Enabled || !runtime.Account.EffectiveCartMaintenanceEnabled || CharacterOwnershipChanged')
save(path, s)

path = 'Model/Vanilla/VanillaReconnect.cs'
s = load(path)
start = s.index('        public void Apply(VanillaReconnectSettings value, bool save)')
end = s.index('        public string GetPassword(', start)
method = s[start:end]
body_start = method.index('                Interlocked.Increment(ref resumeVerificationGeneration);')
body_end = method.index('            }\n            RaiseUpdated();')
body = method[body_start:body_end]
method = method[:body_start] + '''                if (IsMailOnlySettingsChange(settings, copy))
                {
                    // Mail is observational: changing it must not cancel a drag, recovery
                    // or sibling operation. All input-affecting edits use the normal path.
                    settings = copy;
                    foreach (Runtime runtime in runtimes.Values)
                        runtime.Account = copy.Accounts.Single(account => account.Id == runtime.Account.Id);
                    if (save) store.Save(settings);
                }
                else
                {
''' + ''.join('    ' + line if line.strip() else line for line in body.splitlines(True)) + '''                }
''' + method[body_end:]
helper = '''        internal static bool IsMailOnlySettingsChange(VanillaReconnectSettings before, VanillaReconnectSettings after)
        {
            if (before == null || after == null || before.Accounts == null || after.Accounts == null) return false;
            var left = Newtonsoft.Json.Linq.JObject.FromObject(before);
            var right = Newtonsoft.Json.Linq.JObject.FromObject(after);
            foreach (var snapshot in new[] { left, right })
            {
                foreach (var row in snapshot["Accounts"])
                {
                    bool cart = (bool?)row["CartMaintenanceEnabled"] ?? (bool?)row["WeightEnabled"] ?? true;
                    row["WeightEnabled"] = cart;
                    row["CartMaintenanceEnabled"] = cart;
                    row["WeightEmailEnabled"] = null;
                }
            }
            return Newtonsoft.Json.Linq.JToken.DeepEquals(left, right);
        }

'''
s = s[:start] + helper + method + s[end:]
save(path, s)

# Preserve concurrent UI/layout edits; change only the known help fragments.
path = 'Model/Vanilla/VanillaWeightAlertsPanel.cs'
s = load(path)
variants = [
    'With Cart: carried-only warnings are suppressed; Cart-full and combined DONE milestone mail is used.',
    "If that character's Cart switch and the Cart master are active, carried-only warnings are suppressed and Cart-full / combined DONE milestone mail is used."
]
matched = [v for v in variants if v in s]
if len(matched) != 1: raise RuntimeError('Unexpected Cart mail tooltip')
s = replace(s, matched[0], 'When Cart maintenance is active, mail waits for BOTH Cart >=99% and carried weight >=50%, after verified Autobattle STOP. No carried-only or Cart-only warning is sent.')
s = replace(s, 'Cart ON = no carried-only warning; exact Cart 100% and combined Cart>=99% + carried>=50% milestones use this SMTP transport.', 'Cart ON = one DONE notification only when Cart>=99% AND carried>=50%, after verified STOP. Cart-full alone does not notify.')
s = replace(s, '''            status.Text = "Saved. Character Cart/Mail switches control who uses these shared settings"
                + (milestoneMail ? "; SMTP ready." : "; SMTP not configured.");''', '''            status.Text = milestoneMail ? "Saved. SMTP ready." : "Saved. SMTP not configured.";''')
save(path, s)
path = 'Model/Vanilla/VanillaMinimalAccountUi.cs'
s = load(path)
s = replace(s, 'Cart/full-farming milestone mail is used.', 'mail waits for BOTH Cart >=99% and carried weight >=50%, after verified Autobattle STOP.')
save(path, s)

path = 'Tests/VanillaMemoryAndWeightTests.cs'
s = load(path)
s = replace(s, 'Cart milestone mail uses saved SMTP independently of weight-warning switch', 'SMTP transport validation does not authorize automatic mail')
s = replace(s, '''            failed += Test("Mail interpretation changes when Cart maintenance is active", WeightMailMode);''', '''            failed += Test("Mail interpretation changes when Cart maintenance is active", WeightMailMode);
            failed += Test("Cart mail waits for BOTH verified capacity limits", CombinedCapacityMail);
            failed += Test("Mail-only edits preserve input ownership without masking Cart or identity edits", MailOnlySettingsEdits);''')
start = s.index('        private static void WeightMailMode()')
end = s.index('        private static void InventoryVision()', start)
s = s[:start] + '''        private static void WeightMailMode()
        {
            foreach (bool masterMail in new[] { false, true })
            foreach (bool rowMail in new[] { false, true })
            foreach (bool masterCart in new[] { false, true })
            foreach (bool rowCart in new[] { false, true })
            {
                var expected = !masterMail || !rowMail ? VanillaWeightMailMode.None
                    : masterCart && rowCart ? VanillaWeightMailMode.CartMilestones : VanillaWeightMailMode.CarriedWeight;
                if (VanillaWeightAlertService.ResolveMailMode(masterMail, rowMail, masterCart, rowCart) != expected)
                    throw new Exception("Cart/Mail master and row truth table failed.");
            }
            var explicitCart = JsonConvert.DeserializeObject<VanillaReconnectAccount>(
                "{\\"WeightEnabled\\":false,\\"CartMaintenanceEnabled\\":true,\\"WeightEmailEnabled\\":false}");
            if (!explicitCart.Clone().EffectiveCartMaintenanceEnabled || explicitCart.EffectiveWeightEmailEnabled)
                throw new Exception("Explicit Cart ON did not override legacy Weight OFF.");
        }

        private static void CombinedCapacityMail()
        {
            var observation = new VanillaWeightObservation { Verified = true, CartVerified = true, CartPercent = 100m, Percent = 45m };
            if (VanillaWeightAlertService.IsCombinedCapacityReached(observation)) throw new Exception("Cart-full alone notified.");
            observation.CartPercent = 98.99m; observation.Percent = 100m;
            if (VanillaWeightAlertService.IsCombinedCapacityReached(observation)) throw new Exception("Carried-full alone notified.");
            observation.CartPercent = 99m; observation.Percent = 50m;
            if (!VanillaWeightAlertService.IsCombinedCapacityReached(observation)) throw new Exception("Exact combined boundary rejected.");
            observation.Percent = 49.99m;
            if (VanillaWeightAlertService.IsCombinedCapacityReached(observation)) throw new Exception("Below carried boundary notified.");
            observation.Percent = 50m; observation.CartVerified = false;
            if (VanillaWeightAlertService.IsCombinedCapacityReached(observation)) throw new Exception("Unverified Cart notified.");
            observation.CartVerified = true; observation.Verified = false;
            if (VanillaWeightAlertService.IsCombinedCapacityReached(observation)) throw new Exception("Unverified carried weight notified.");
            observation.Verified = true; observation.CartPercent = null;
            if (VanillaWeightAlertService.IsCombinedCapacityReached(observation)) throw new Exception("Missing Cart notified.");
            observation.CartPercent = 100m; observation.Percent = null;
            if (VanillaWeightAlertService.IsCombinedCapacityReached(observation)
                || VanillaWeightAlertService.IsCombinedCapacityReached(null)) throw new Exception("Missing carried weight notified.");
        }

        private static void MailOnlySettingsEdits()
        {
            var before = VanillaReconnectSettings.CreateDefault();
            before.Accounts[0].CartMaintenanceEnabled = false;
            before.Accounts[0].WeightEmailEnabled = true;
            before.Accounts[0].WeightEnabled = true;
            string original = JsonConvert.SerializeObject(before);
            var after = before.Clone();
            after.Accounts[0].WeightEmailEnabled = false;
            after.Accounts[0].WeightEnabled = false;
            if (!VanillaReconnectSupervisor.IsMailOnlySettingsChange(before, after))
                throw new Exception("Mail-only change would interrupt Cart/recovery ownership.");
            after.Accounts[1].WeightEmailEnabled = false;
            if (!VanillaReconnectSupervisor.IsMailOnlySettingsChange(before, after))
                throw new Exception("Sibling Mail edit invalidated input ownership.");
            var changed = after.Clone(); changed.Accounts[0].CartMaintenanceEnabled = true;
            if (VanillaReconnectSupervisor.IsMailOnlySettingsChange(before, changed)) throw new Exception("Cart change was ignored.");
            changed = after.Clone(); changed.Accounts[0].Enabled = false;
            if (VanillaReconnectSupervisor.IsMailOnlySettingsChange(before, changed)) throw new Exception("Disable was ignored.");
            changed = after.Clone(); changed.Accounts[0].CharacterName = "another character";
            if (VanillaReconnectSupervisor.IsMailOnlySettingsChange(before, changed)) throw new Exception("Identity change was ignored.");
            changed = after.Clone(); changed.Accounts[0].ResumeAlt = !changed.Accounts[0].ResumeAlt;
            if (VanillaReconnectSupervisor.IsMailOnlySettingsChange(before, changed)) throw new Exception("Hotkey change was ignored.");
            changed = after.Clone(); changed.MovementRestartSeconds++;
            if (VanillaReconnectSupervisor.IsMailOnlySettingsChange(before, changed)) throw new Exception("Global setting change was ignored.");
            changed = after.Clone(); changed.Accounts.RemoveAt(1);
            if (VanillaReconnectSupervisor.IsMailOnlySettingsChange(before, changed)) throw new Exception("Removed row was ignored.");
            if (JsonConvert.SerializeObject(before) != original) throw new Exception("Comparison changed persisted settings.");
        }

''' + s[end:]
save(path, s)

for path in ['AGENTS.md', 'Model/Vanilla/AGENTS.md']:
    s = load(path)
    s = s.replace('At Cart 100%, send a Cart-full mail only when the shared e-mail master/SMTP transport and that character\'s Mail switch are enabled.', 'Do not send a Cart-full-only mail; the user now requires BOTH capacity limits before a Cart-mode notification.')
    s = s.replace('Cart ON + Mail ON suppresses carried-weight-only warning mail and instead uses the exact Cart-100% milestone plus the combined DONE milestone (Cart >=99% and carried >=50%).', 'Cart ON + Mail ON suppresses both carried-only and Cart-only warning mail and sends one combined DONE notification (Cart >=99% AND carried >=50%) after a verified completion STOP.')
    s = s.replace('Exact Cart 100% remains the Cart-full e-mail milestone, but only for characters whose Mail switch is enabled and whose shared e-mail master/SMTP transport is configured.', 'Cart 100% alone is NOT an e-mail event; Mail in Cart mode requires both capacity limits and verified completion STOP, as well as the character Mail switch and shared e-mail master/SMTP transport.')
    s = s.replace('Cart ON + Mail ON suppresses carried-only warnings and sends Cart-full / combined DONE milestone mail instead.', 'Cart ON + Mail ON suppresses both carried-only and Cart-only warnings and sends one combined DONE notification instead.')
    s += '\n## Combined-capacity notification completion (2026-09-20)\n\nIn active Cart mode, notify only after Cart >=99% AND carried weight >=50% plus a verified completion STOP. Neither capacity alone sends mail. Without active Cart maintenance, Mail uses the configured carried-weight warning threshold. Re-check current shared and per-character Mail settings immediately before dispatch so queued work cannot use an obsolete enabled switch. Reserve a single in-flight DONE send per character. Mail-only profile edits must preserve Cart/recovery input ownership; Cart, identity, enabled-state or input-setting edits retain normal cancellation. Use EffectiveCartMaintenanceEnabled in every Cart guard, never the legacy WeightEnabled field directly.\n'
    save(path, s)

assert '!account.WeightEnabled' not in load('Model/Vanilla/VanillaWeightAlerts.cs')
assert '!runtime.Account.WeightEnabled' not in load('Model/Vanilla/VanillaWeightCartAutomation.cs')
assert 'SendMail(current, "Cart full:' not in load('Model/Vanilla/VanillaWeightAlerts.cs')
print('Applied combined-capacity mail, current policy dispatch, split Cart guards, mail-only edits, compact help and regressions.')
