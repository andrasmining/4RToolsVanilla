using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using _4RTools.Forms;
using _4RTools.Model;
using _4RTools.Model.Vanilla;
using _4RTools.Model.Vanilla.Automation;

namespace Vanilla.Diagnostics.Tests
{
    public static class VanillaEditorAutosaveTests
    {
        public static int Run()
        {
            int failed = 0;
            var tests = new Dictionary<string, System.Action>
            {
                { "Diagnostics autosave keeps invalid drafts separate from valid fields", DiagnosticsFields },
                { "Diagnostics pending edits stay bound to their original profile", DiagnosticsProfileOwnership },
                { "Diagnostics profile reload clears an invalid numeric draft without saving", DiagnosticsReload },
                { "Diagnostics failed writes preserve durable and active settings", DiagnosticsWriteFailure },
                { "Diagnostics saves raw typed numbers and rejects out-of-range drafts", DiagnosticsNumbers },
                { "Rules autosave validates before persistence and retains last successful rules", RulesValidation },
                { "Invalid rule drafts stay selected until corrected", RulesSelection },
                { "Rule disposal flushes pending edits and loading never writes", RulesLifecycle }
            };
            foreach (var test in tests)
            {
                try { test.Value(); Console.WriteLine("PASS " + test.Key); }
                catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Key + ": " + ex); }
            }
            Console.WriteLine("Editor autosave: {0} passed; {1} failed.", tests.Count - failed, failed);
            return failed;
        }

        private static void DiagnosticsFields()
        {
            WithProfile(root =>
            {
                string path = Path.Combine(root, "First.json");
                string original = File.ReadAllText(path);
                using (var form = new VanillaDiagnosticsForm(null, false))
                {
                    Assert(File.ReadAllText(path) == original, "Loading rewrote settings.");
                    NoSaveButtons(form);
                    var interval = Field<NumericUpDown>(form, "interval");
                    var map = Field<TextBox>(form, "mapEditor");
                    map.Text = "{invalid draft";
                    Flush(form);
                    Assert(File.ReadAllText(path) == original, "Invalid map reached disk.");
                    interval.Value = 1250;
                    Flush(form);
                    Assert(ProfileSingleton.GetCurrent().VanillaDiagnostics.PollIntervalMilliseconds == 1250,
                        "An unrelated invalid draft blocked a valid interval.");
                    Assert(Field<Label>(form, "saveStatus").Text.StartsWith("Not saved"), "Invalid draft lost its feedback.");
                    var saved = JObject.Parse(File.ReadAllText(path));
                    Assert(saved["Unrelated"].Value<string>() == "preserve", "Autosave overwrote an unrelated section.");
                    map.Text = new VanillaDiagnosticsSettings().MemoryMapJson;
                }
                ProfileSingleton.Load("First");
                Assert(ProfileSingleton.GetCurrent().VanillaDiagnostics.PollIntervalMilliseconds == 1250, "Reload lost persisted settings.");
            });
        }

        private static void DiagnosticsProfileOwnership()
        {
            WithProfile(root =>
            {
                var first = ProfileSingleton.GetCurrent();
                using (var form = new VanillaDiagnosticsForm(null, false))
                {
                    Field<NumericUpDown>(form, "interval").Value = 2000;
                    File.WriteAllText(Path.Combine(root, "Second.json"), "{}");
                    ProfileSingleton.Load("Second");
                    Flush(form);
                    Assert(first.VanillaDiagnostics.PollIntervalMilliseconds == 2000, "Pending edit did not reach its owner.");
                    Assert(ProfileSingleton.GetCurrent().VanillaDiagnostics.PollIntervalMilliseconds == 500,
                        "Pending edit leaked into the replacement profile.");
                }
                ProfileSingleton.Load("First");
                Assert(ProfileSingleton.GetCurrent().VanillaDiagnostics.PollIntervalMilliseconds == 2000, "Old profile was not persisted.");
                Assert(File.ReadAllText(Path.Combine(root, "Second.json")) == "{}", "Replacement file was rewritten.");
            });
        }

        private static void DiagnosticsWriteFailure()
        {
            WithProfile(root =>
            {
                string path = Path.Combine(root, "First.json");
                string original = File.ReadAllText(path);
                using (var form = new VanillaDiagnosticsForm(null, false))
                {
                    Directory.CreateDirectory(path + ".tmp");
                    Field<NumericUpDown>(form, "interval").Value = 3000;
                    Flush(form);
                    Assert(File.ReadAllText(path) == original, "Failed write changed the original file.");
                    Assert(ProfileSingleton.GetCurrent().VanillaDiagnostics.PollIntervalMilliseconds == 500,
                        "Failed write activated an unsaved setting.");
                    Assert(Field<Label>(form, "saveStatus").Text.StartsWith("Not saved"), "Failed write was not visible.");
                }
            });
        }

        private static void DiagnosticsReload()
        {
            WithProfile(root =>
            {
                var subject = new _4RTools.Utils.Subject();
                using (var form = new VanillaDiagnosticsForm(subject, false))
                {
                    Field<NumericUpDown>(form, "interval").Text = "90000";
                    Flush(form);
                    File.WriteAllText(Path.Combine(root, "Second.json"), "{}");
                    ProfileSingleton.Load("Second");
                    subject.Notify(new _4RTools.Utils.Message(_4RTools.Utils.MessageCode.PROFILE_CHANGED, null));
                    Assert(VanillaSettingsNumber.Read(Field<NumericUpDown>(form, "interval")) == 500,
                        "Profile reload retained the previous profile's invalid text.");
                    Assert(!Field<VanillaSettingsAutoSave>(form, "autosave").HasPending
                        && File.ReadAllText(Path.Combine(root, "Second.json")) == "{}", "Profile rendering scheduled a write.");
                }
            });
        }

        private static void RulesValidation()
        {
            int writes = 0;
            bool rejectWrite = false;
            using (var form = new VanillaRulesEditor(new List<AutomationRuleSettings> { new AutomationRuleSettings() }, 19,
                rules => { if (rejectWrite) throw new IOException("Simulated unavailable store"); writes++; }))
            {
                Assert(writes == 0, "Loading persisted the rule list.");
                NoSaveButtons(form);
                var name = Field<TextBox>(form, "name");
                name.Text = "Renamed rule";
                Flush(form);
                Assert(writes == 1 && form.Rules[0].Name == "Renamed rule", "Valid edit was not saved.");
                name.Text = "";
                Flush(form);
                Assert(writes == 1 && form.Rules[0].Name == "Renamed rule", "Invalid name replaced the saved rule.");
                name.Text = "Failed write";
                rejectWrite = true;
                Flush(form);
                Assert(writes == 1 && form.Rules[0].Name == "Renamed rule", "Failed persistence published the draft.");
                Assert(Field<Label>(form, "saveStatus").Text.StartsWith("Not saved"), "Rule error was not visible.");
                rejectWrite = false;
                name.Text = "Recovered";
                Flush(form);
                Assert(writes == 2 && form.Rules[0].Name == "Recovered", "A corrected edit could not recover.");
            }
        }

        private static void DiagnosticsNumbers()
        {
            WithProfile(root =>
            {
                using (var form = new VanillaDiagnosticsForm(null, false))
                {
                    var number = Field<NumericUpDown>(form, "interval");
                    number.Text = "11250";
                    Flush(form);
                    Assert(ProfileSingleton.GetCurrent().VanillaDiagnostics.PollIntervalMilliseconds == 500,
                        "An out-of-range draft was clamped or persisted.");
                    number.Text = "2250";
                }
                ProfileSingleton.Load("First");
                Assert(ProfileSingleton.GetCurrent().VanillaDiagnostics.PollIntervalMilliseconds == 2250,
                    "Closing lost a raw typed numeric edit.");
            });
        }

        private static void RulesSelection()
        {
            int writes = 0;
            using (var form = new VanillaRulesEditor(new List<AutomationRuleSettings>
                { new AutomationRuleSettings { Name = "First" }, new AutomationRuleSettings { Name = "Second" } }, 19,
                rules => writes++))
            {
                var name = Field<TextBox>(form, "name");
                var list = Field<ListBox>(form, "rules");
                name.Text = "";
                list.SelectedIndex = 1;
                Assert(list.SelectedIndex == 0 && name.Text == "" && writes == 0,
                    "Switching rules hid an invalid draft or persisted it.");
                name.Text = "Corrected";
                list.SelectedIndex = 1;
                Assert(list.SelectedIndex == 1 && name.Text == "Second" && writes == 1,
                    "Correcting the draft did not save and release the selection.");
                Field<NumericUpDown>(form, "period").Text = "45.5";
                Flush(form);
                Assert(form.Rules[1].PeriodMs == 45500, "Rule numeric draft was not saved from raw text.");
                Flush(form);
                Assert(writes == 2, "No-op flush wrote again.");
            }
            Assert(writes == 2, "No-op close wrote again.");
        }

        private static void RulesLifecycle()
        {
            string savedName = null;
            var form = new VanillaRulesEditor(new List<AutomationRuleSettings> { new AutomationRuleSettings() }, 19,
                rules => savedName = rules[0].Name);
            Field<TextBox>(form, "name").Text = "Pending at close";
            form.Dispose();
            Assert(savedName == "Pending at close", "Disposal lost a valid pending edit.");
        }

        private static void WithProfile(System.Action<string> test)
        {
            var folder = typeof(ProfileSingleton).Assembly.GetType("_4RTools.Utils.AppConfig").GetField("ProfileFolder");
            string previousFolder = (string)folder.GetValue(null);
            var previousProfile = ProfileSingleton.profile;
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "4RTools-EditorAutosaveTests"));
            string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                folder.SetValue(null, root + Path.DirectorySeparatorChar);
                File.WriteAllText(Path.Combine(root, "First.json"), "{\"Unrelated\":\"preserve\"}");
                ProfileSingleton.Load("First");
                test(root);
            }
            finally
            {
                folder.SetValue(null, previousFolder);
                ProfileSingleton.profile = previousProfile;
                string resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Test cleanup escaped its temporary root.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }

        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }
        private static void Flush(object target) { Field<VanillaSettingsAutoSave>(target, "autosave").Flush(); }
        private static void NoSaveButtons(Control root)
        {
            foreach (Control child in root.Controls)
            {
                Assert(!(child is Button) || (!child.Text.StartsWith("Save", StringComparison.OrdinalIgnoreCase)
                    && !child.Text.StartsWith("Apply", StringComparison.OrdinalIgnoreCase)), "Manual settings button remains.");
                NoSaveButtons(child);
            }
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
