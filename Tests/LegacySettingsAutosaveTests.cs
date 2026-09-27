using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Input;
using Newtonsoft.Json;
using _4RTools.Forms;
using _4RTools.Model;
using _4RTools.Utils;

namespace Vanilla.Diagnostics.Tests
{
    public static class LegacySettingsAutosaveTests
    {
        private static readonly Type StoreType = typeof(Profile).Assembly.GetType("_4RTools.Model.LocalServerStore", true);

        public static int Run()
        {
            var tests = new Dictionary<string, System.Action>
            {
                { "Legacy editors render profiles without mutating or saving them", ProfileRendering },
                { "Legacy hotkeys numeric settings and sound persist automatically", ProfileEdits },
                { "Legacy numeric text is persisted when its host window closes", NumericClose },
                { "Legacy macro edits persist from an empty profile and reset durably", MacroEdits },
                { "Legacy profile rename saves only completed edits and follows the active file", ProfileRename },
                { "Legacy profile rename rejects collisions and invalid names without losing files", RenameRejection },
                { "Legacy profile saves preserve the prior document on replacement failure", ProfileWriteFailure },
                { "Legacy server edits validate before replacing their saved row", ServerValidation },
                { "Legacy server edits follow row identity after another row is removed", ServerIdentity },
                { "Stale legacy server editors cannot replace newer settings", StaleServerEditor },
                { "Legacy server writes preserve malformed and locked files", ServerWriteFailure },
                { "Existing legacy server fields autosave completed edits without Save", ServerEditor },
                { "New legacy servers require Add once then become autosaving editors", ServerCreation },
                { "Legacy server autosave errors retain edits for retry", ServerEditorFailure }
            };
            int failed = 0;
            foreach (var test in tests)
            {
                try { test.Value(); Console.WriteLine("PASS " + test.Key); }
                catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Key + ": " + ex); }
            }
            Console.WriteLine("Legacy settings autosave: {0} passed; {1} failed. Isolated files and test-owned forms only.", tests.Count - failed, failed);
            return failed;
        }

        private static void ProfileRendering()
        {
            WithProfiles(root =>
            {
                ProfileSingleton.Create("First");
                var profile = ProfileSingleton.GetCurrent();
                profile.AHK.AhkEntries["chkF1"] = new KeyConfig(Key.F1, true);
                profile.AHK.noShift = true;
                profile.AHK.AhkDelay = 75;
                profile.Autopot.hpKey = Key.F2;
                profile.Autopot.hpPercent = 64;
                profile.Autopot.spPercent = 41;
                profile.AutoRefreshSpammer1.RefreshKey = Key.F4;
                profile.AutoRefreshSpammer1.RefreshDelay = 120;
                profile.SongMacro.chainConfigs[0].trigger = Key.F3;
                profile.MacroSwitch.chainConfigs[0].macroEntries["in1mac1"] = new MacroKey(Key.F6, 90) { hasClick = true };
                profile.Autobuff.buffMapping[EffectStatusIDs.SILENCE] = Key.F5;
                profile.StatusRecovery.autoStand = true;
                profile.UserPreferences.audioEnabled = false;
                File.WriteAllText(Path.Combine(root, "First.json"), JsonConvert.SerializeObject(profile));
                ProfileSingleton.Load("First");
                string before = File.ReadAllText(Path.Combine(root, "First.json"));
                string model = JsonConvert.SerializeObject(ProfileSingleton.GetCurrent());
                var subject = new Subject();
                Form[] forms = {
                    new AHKForm(subject), new ATKDEFForm(subject), new AutopotForm(subject, false), new AutopotForm(subject, true),
                    new DebuffRecoveryForm(subject), new SkillTimerForm(subject), new MacroSwitchForm(subject), new MacroSongForm(subject),
                    new StuffAutoBuffForm(subject), new SkillAutoBuffForm(subject), new ToggleApplicationStateForm(subject, true)
                };
                try
                {
                    for (int cycle = 0; cycle < 3; cycle++)
                        subject.Notify(new _4RTools.Utils.Message(MessageCode.PROFILE_CHANGED, null));
                    Assert(File.ReadAllText(Path.Combine(root, "First.json")) == before, "Rendering wrote the profile file.");
                    Assert(JsonConvert.SerializeObject(ProfileSingleton.GetCurrent()) == model, "Rendering mutated loaded settings.");
                    Assert(Directory.GetFiles(root).Length == 1, "Rendering produced an autosave backup or temporary file.");
                }
                finally { foreach (Form form in forms) form.Dispose(); }
            });
        }

        private static void ProfileEdits()
        {
            WithProfiles(root =>
            {
                ProfileSingleton.Create("Edits");
                var subject = new Subject();
                using (var pot = new AutopotForm(subject, false))
                using (var ahk = new AHKForm(subject))
                using (var timer = new SkillTimerForm(subject))
                using (var toggle = new ToggleApplicationStateForm(subject, true))
                {
                    subject.Notify(new _4RTools.Utils.Message(MessageCode.PROFILE_CHANGED, null));
                    Find<NumericUpDown>(pot, "txtHPpct").Value = 72;
                    Find<NumericUpDown>(pot, "txtAutopotDelay").Value = 25;
                    Find<TextBox>(pot, "txtHpKey").Text = "F2";
                    Find<NumericUpDown>(ahk, "txtSpammerDelay").Value = 37;
                    Find<TextBox>(timer, "txtSkillTimerKey").Text = "F4";
                    Find<NumericUpDown>(timer, "txtAutoRefreshDelay").Value = 120;
                    Find<CheckBox>(toggle, "cbAudio").Checked = false;
                    ProfileSingleton.Load("Edits");
                    var saved = ProfileSingleton.GetCurrent();
                    Assert(saved.Autopot.hpPercent == 72 && saved.Autopot.delay == 25 && saved.Autopot.hpKey == Key.F2, "Autopot did not persist.");
                    Assert(saved.AHK.AhkDelay == 37 && saved.AutoRefreshSpammer1.RefreshKey == Key.F4 && saved.AutoRefreshSpammer1.RefreshDelay == 120, "Hotkey/delay edits did not persist.");
                    Assert(!saved.UserPreferences.audioEnabled, "Sound preference did not persist.");
                }
            });
        }

        private static void MacroEdits()
        {
            WithProfiles(root =>
            {
                ProfileSingleton.Create("Macro");
                var subject = new Subject();
                using (var switches = new MacroSwitchForm(subject))
                using (var songs = new MacroSongForm(subject))
                {
                    subject.Notify(new _4RTools.Utils.Message(MessageCode.PROFILE_CHANGED, null));
                    Find<CheckBox>(switches, "in1mac1click").Checked = true;
                    Find<NumericUpDown>(switches, "in1mac1delay").Value = 75;
                    Find<TextBox>(switches, "in1mac1").Text = "F6";
                    Find<TextBox>(songs, "inTriggerMacro1").Text = "F7";
                    ProfileSingleton.Load("Macro");
                    var entry = ProfileSingleton.GetCurrent().MacroSwitch.chainConfigs[0].macroEntries["in1mac1"];
                    Assert(entry.hasClick && entry.delay == 75 && entry.key == Key.F6, "Macro edits failed on an initially empty entry or key edit lost click state.");
                    Invoke(songs, "onReset", Find<Button>(songs, "btnResMac1"), EventArgs.Empty);
                    ProfileSingleton.Load("Macro");
                    Assert(ProfileSingleton.GetCurrent().SongMacro.chainConfigs[0].trigger == Key.None, "Macro Reset did not persist.");
                    File.WriteAllText(Path.Combine(root, "Empty.json"), "{}");
                    ProfileSingleton.Load("Empty");
                    subject.Notify(new _4RTools.Utils.Message(MessageCode.PROFILE_CHANGED, null));
                    Assert(Find<NumericUpDown>(switches, "in1mac1delay").Value == 50, "Empty profile inherited another profile's displayed macro delay.");
                }
            });
        }

        private static void NumericClose()
        {
            WithProfiles(root =>
            {
                Func<Subject, Form>[] create = { subject => new AutopotForm(subject, false), subject => new AHKForm(subject),
                    subject => new ATKDEFForm(subject), subject => new SkillTimerForm(subject), subject => new MacroSwitchForm(subject), subject => new MacroSongForm(subject) };
                string[] fields = { "txtAutopotDelay", "txtSpammerDelay", "spammerDelay", "txtAutoRefreshDelay", "in1mac1delay", "delayMac1" };
                Func<Profile, int>[] value = { profile => profile.Autopot.delay, profile => profile.AHK.AhkDelay,
                    profile => profile.AtkDefMode.ahkDelay, profile => profile.AutoRefreshSpammer1.RefreshDelay,
                    profile => profile.MacroSwitch.chainConfigs[0].macroEntries["in1mac1"].delay, profile => profile.SongMacro.chainConfigs[0].delay };
                for (int index = 0; index < create.Length; index++)
                {
                    ProfileSingleton.Create("Closing" + index);
                    var subject = new Subject();
                    using (var host = new Form())
                    using (Form editor = create[index](subject))
                    {
                        editor.TopLevel = false;
                        editor.Dock = DockStyle.Fill;
                        host.Controls.Add(editor);
                        subject.Notify(new _4RTools.Utils.Message(MessageCode.PROFILE_CHANGED, null));
                        host.Show();
                        editor.Show();
                        NumericUpDown delay = Find<NumericUpDown>(editor, fields[index]);
                        delay.Focus();
                        delay.Controls.OfType<TextBox>().Single().Text = "37";
                        host.Close();
                        ProfileSingleton.Load("Closing" + index);
                        Assert(value[index](ProfileSingleton.GetCurrent()) == 37, "Closing lost the active numeric edit for " + fields[index] + ".");
                    }
                }
            });
        }

        private static void ProfileRename()
        {
            WithProfiles(root =>
            {
                ProfileSingleton.Create("Original");
                Profile active = ProfileSingleton.GetCurrent();
                using (var form = new EditProfileName())
                {
                    form.SetProfileName("Original");
                    Assert(!Buttons(form).Any(button => button.Text == "Save"), "Rename still requires Save.");
                    TextBox name = Find<TextBox>(form, "txtProfileName");
                    name.Text = "Renamed.v2";
                    Assert(File.Exists(Path.Combine(root, "Original.json")) && !form.Changed, "Keystrokes renamed the file.");
                    Validate(name);
                    Assert(form.Changed && form.ProfileName == "Renamed.v2" && ReferenceEquals(active, ProfileSingleton.GetCurrent()), "Completed rename replaced or lost the active profile.");
                    active.UserPreferences.audioEnabled = false;
                    ProfileSingleton.SetConfiguration(active.UserPreferences);
                    Assert(!File.Exists(Path.Combine(root, "Original.json")), "Later save recreated the old file.");
                    Assert(Profile.ListAll().SequenceEqual(new[] { "Renamed.v2" }), "Backups or dotted names corrupted the profile list.");
                    ProfileSingleton.Load("Renamed.v2");
                    Assert(!ProfileSingleton.GetCurrent().UserPreferences.audioEnabled, "Later edit did not follow renamed profile.");
                }
            });
        }

        private static void RenameRejection()
        {
            WithProfiles(root =>
            {
                ProfileSingleton.Create("Existing");
                ProfileSingleton.Create("Original");
                string before = File.ReadAllText(Path.Combine(root, "Original.json"));
                foreach (string invalid in new[] { "Existing", "../outside", "CON", "", " name " })
                    Reject(() => ProfileSingleton.Rename("Original", invalid));
                Assert(ProfileSingleton.GetCurrent().Name == "Original" && File.ReadAllText(Path.Combine(root, "Original.json")) == before, "Rejected rename lost active settings.");
                using (var form = new EditProfileName())
                {
                    form.SetProfileName("Original");
                    Find<TextBox>(form, "txtProfileName").Text = "Existing";
                    Validate(Find<TextBox>(form, "txtProfileName"));
                    Assert(!form.Changed && Find<TextBox>(form, "txtProfileName").Text == "Existing", "Rejected draft was silently discarded.");
                }
            });
        }

        private static void ProfileWriteFailure()
        {
            WithProfiles(root =>
            {
                ProfileSingleton.Create("Locked");
                string path = Path.Combine(root, "Locked.json");
                string before = File.ReadAllText(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ProfileSingleton.GetCurrent().UserPreferences.audioEnabled = false;
                    Reject(() => ProfileSingleton.SetConfiguration(ProfileSingleton.GetCurrent().UserPreferences));
                    Assert(File.ReadAllText(path) == before, "Failed save truncated the original profile.");
                }
            });
        }

        private static void ServerValidation()
        {
            WithDirectory(root =>
            {
                object store = Store(root);
                ClientDTO original = Add(store, "First");
                string path = Path.Combine(root, "servers.json");
                string before = File.ReadAllText(path);
                Reject(() => Call(store, "Update", original, "bad", "00000020", "First"));
                Reject(() => Call(store, "Update", original, "00000010", "00000020", "Vanilla MMO"));
                Reject(() => Call(store, "Update", original, "00000010", "00000020", " Vanilla MMO.exe "));
                Assert(File.ReadAllText(path) == before, "Invalid server edit removed its previous saved row.");
                var updated = (ClientDTO)Call(store, "Update", original, "00000030", "00000020", "First");
                Assert(updated.hpAddressPointer == 48 && Read(store).Single().hpAddress == "00000030", "Valid update did not persist.");
            });
        }

        private static void ServerIdentity()
        {
            WithDirectory(root =>
            {
                object store = Store(root);
                ClientDTO first = Add(store, "First"), second = Add(store, "Second");
                second.index = 1;
                Call(store, "Remove", first);
                Call(store, "Update", second, "00000030", "00000020", "Second");
                Assert(Read(store).Single().name == "Second" && Read(store).Single().hpAddress == "00000030", "Edit trusted the stale row index.");
            });
        }

        private static void StaleServerEditor()
        {
            WithDirectory(root =>
            {
                object store = Store(root);
                ClientDTO original = Add(store, "First");
                Call(store, "Update", original, "00000030", "00000020", "First");
                Reject(() => Call(store, "Update", original, "00000040", "00000020", "First"));
                Reject(() => Call(store, "Remove", original));
                Assert(Read(store).Single().hpAddress == "00000030", "Stale editor overwrote a newer saved row.");
            });
        }

        private static void ServerWriteFailure()
        {
            WithDirectory(root =>
            {
                object store = Store(root);
                string path = Path.Combine(root, "servers.json");
                File.WriteAllText(path, "broken JSON");
                Reject(() => Add(store, "First"));
                Assert(File.ReadAllText(path) == "broken JSON", "Malformed server list was replaced.");
                File.WriteAllText(path, "[]");
                ClientDTO original = Add(store, "First");
                string before = File.ReadAllText(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Reject(() => Call(store, "Update", original, "00000030", "00000020", "First"));
                Assert(File.ReadAllText(path) == before && !Directory.GetFiles(root, "*.tmp").Any(), "Failed atomic server write lost the prior list or leaked temporary data.");
            });
        }

        private static void ServerEditor()
        {
            int writes = 0;
            ClientDTO saved = null;
            using (var form = ServerForm(new ClientDTO("First", null, "00000010", "00000020"), (original, hp, name, process) =>
            { writes++; return saved = new ClientDTO(process, null, hp, name); }))
            {
                Assert(writes == 0 && !Buttons(form).Any(button => button.Text == "Save"), "Loading the editor saved values or exposed Save.");
                TextBox digit = Find<TextBox>(form, "txtHP8");
                digit.Text = "F";
                Assert(writes == 0, "Server address persisted before its edit completed.");
                Validate(digit);
                Assert(writes == 1 && saved.hpAddress == "0000001F", "Completed address edit did not persist.");
                Validate(digit);
                Assert(writes == 1, "Unchanged validation wrote twice.");
                digit.Text = "";
                Validate(digit);
                Assert(writes == 1 && digit.Text == "", "Invalid address overwrote settings or disappeared.");
            }
        }

        private static void ServerCreation()
        {
            int writes = 0;
            using (var form = ServerForm(null, (original, hp, name, process) => { writes++; return new ClientDTO(process, null, hp, name); }))
            {
                for (int index = 1; index <= 8; index++)
                {
                    Find<TextBox>(form, "txtHP" + index).Text = index == 8 ? "1" : "0";
                    Find<TextBox>(form, "txtName" + index).Text = index == 8 ? "2" : "0";
                }
                Find<ComboBox>(form, "processCB").Text = "First";
                Validate(Find<ComboBox>(form, "processCB"));
                Assert(writes == 0 && Find<Button>(form, "btnAdd").Text == "Add", "New server draft was created implicitly.");
                Invoke(form, "btnAdd_Click", form, EventArgs.Empty);
                Assert(writes == 1, "Explicit Add did not create the server.");
                Find<TextBox>(form, "txtHP8").Text = "3";
                Validate(Find<TextBox>(form, "txtHP8"));
                Assert(writes == 2, "Created server did not transition to autosave editing.");
            }
        }

        private static void ServerEditorFailure()
        {
            bool fail = true;
            int writes = 0;
            using (var form = ServerForm(new ClientDTO("First", null, "00000010", "00000020"), (original, hp, name, process) =>
            {
                if (fail) throw new IOException("Synthetic write failure");
                writes++;
                Assert(original.hpAddress == "00000010", "Failed edit changed the saved identity.");
                return new ClientDTO(process, null, hp, name);
            }))
            {
                var digit = Find<TextBox>(form, "txtHP8");
                digit.Text = "A";
                Validate(digit);
                Assert(writes == 0 && digit.Text == "A", "Failed save lost the edited value.");
                Assert(FormUtils.GetAll(form, typeof(Label)).Any(label => label.Text.Contains("Not saved")), "Failure was not visible.");
                fail = false;
                Validate(digit);
                Assert(writes == 1, "Failed edit was no longer retryable.");
            }
        }

        private static AddServerForm ServerForm(ClientDTO dto, Func<ClientDTO, string, string, string, ClientDTO> save)
        {
            return (AddServerForm)Activator.CreateInstance(typeof(AddServerForm), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { dto, new Subject(), save }, null);
        }
        private static object Store(string root) { return Activator.CreateInstance(StoreType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Path.Combine(root, "servers.json") }, null); }
        private static ClientDTO Add(object store, string name) { return (ClientDTO)Call(store, "Add", "00000010", "00000020", name); }
        private static List<ClientDTO> Read(object store) { return (List<ClientDTO>)Call(store, "Read"); }
        private static object Call(object target, string method, params object[] args) { return Invoke(target, method, args); }
        private static object Invoke(object target, string method, params object[] args)
        {
            try { return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(target, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        private static void Validate(Control control) { typeof(Control).GetMethod("OnValidated", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { EventArgs.Empty }); }
        private static T Find<T>(Control owner, string name) where T : Control { return (T)owner.Controls.Find(name, true).Single(); }
        private static IEnumerable<Button> Buttons(Control owner) { return FormUtils.GetAll(owner, typeof(Button)).Cast<Button>(); }

        private static void WithProfiles(System.Action<string> test)
        {
            WithDirectory(root =>
            {
                FieldInfo folder = typeof(Profile).Assembly.GetType("_4RTools.Utils.AppConfig").GetField("ProfileFolder", BindingFlags.Public | BindingFlags.Static);
                string oldFolder = (string)folder.GetValue(null);
                Profile oldProfile = ProfileSingleton.profile;
                try { folder.SetValue(null, root + Path.DirectorySeparatorChar); test(root); }
                finally { folder.SetValue(null, oldFolder); ProfileSingleton.profile = oldProfile; }
            });
        }
        private static void WithDirectory(System.Action<string> test)
        {
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "4RTools-LegacyAutosaveTests"));
            string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); }
            finally
            {
                if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cleanup escaped test root.");
                Directory.Delete(root, true);
            }
        }
        private static void Reject(System.Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            catch (IOException) { return; }
            catch (InvalidOperationException) { return; }
            catch (JsonException) { return; }
            throw new Exception("Expected rejected edit.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
