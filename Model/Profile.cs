using System.Collections.Generic;
using Newtonsoft.Json;
using _4RTools.Utils;
using _4RTools.Forms;
using System.IO;
using System;
using Newtonsoft.Json.Linq;
using _4RTools.Model.Vanilla;

namespace _4RTools.Model
{
    public class ProfileSingleton
    {
        internal static readonly object PersistenceGate = new object();
        public static Profile profile = new Profile("Default");

        public static void Load(string profileName)
        {
            try
            {
                string path = AppConfig.ProfileFolder + profileName + ".json";
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Profile exceeds 1 MiB.");
                JObject rawObject;
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { MaxDepth = 32 })
                {
                    rawObject = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidDataException("Unexpected content after the profile.");
                }

                // Build an isolated replacement. A missing section cannot inherit another
                // character's settings, and a late parse failure cannot partially switch profiles.
                var loaded = new Profile(profileName);
                loaded.UserPreferences = ReadSection(rawObject, loaded.UserPreferences, nameof(Profile.UserPreferences));
                loaded.AHK = ReadSection(rawObject, loaded.AHK, nameof(Profile.AHK));
                loaded.Autopot = ReadSection(rawObject, loaded.Autopot, nameof(Profile.Autopot));
                loaded.AutopotYgg = ReadSection(rawObject, loaded.AutopotYgg, nameof(Profile.AutopotYgg));
                loaded.StatusRecovery = ReadSection(rawObject, loaded.StatusRecovery, nameof(Profile.StatusRecovery));
                loaded.AutoRefreshSpammer1 = ReadSection(rawObject, loaded.AutoRefreshSpammer1, nameof(Profile.AutoRefreshSpammer1));
                loaded.AutoRefreshSpammer2 = ReadSection(rawObject, loaded.AutoRefreshSpammer2, nameof(Profile.AutoRefreshSpammer2));
                loaded.AutoRefreshSpammer3 = ReadSection(rawObject, loaded.AutoRefreshSpammer3, nameof(Profile.AutoRefreshSpammer3));
                loaded.Autobuff = ReadSection(rawObject, loaded.Autobuff, nameof(Profile.Autobuff));
                loaded.SongMacro = ReadSection(rawObject, loaded.SongMacro, nameof(Profile.SongMacro));
                loaded.AtkDefMode = ReadSection(rawObject, loaded.AtkDefMode, nameof(Profile.AtkDefMode));
                loaded.MacroSwitch = ReadSection(rawObject, loaded.MacroSwitch, nameof(Profile.MacroSwitch));
                loaded.DebuffsRecovery = ReadSection(rawObject, loaded.DebuffsRecovery, nameof(Profile.DebuffsRecovery));
                loaded.VanillaDiagnostics = VanillaDiagnosticsSettings.FromToken(rawObject["VanillaDiagnostics"]);
                System.Windows.Forms.Keys toggleKey;
                if (!Enum.TryParse(loaded.UserPreferences.toggleStateKey, out toggleKey) || !Enum.IsDefined(typeof(System.Windows.Forms.Keys), toggleKey) || toggleKey == System.Windows.Forms.Keys.None)
                    throw new InvalidDataException("The profile's ON/OFF hotkey is invalid.");
                profile = loaded;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Could not load profile '" + profileName + "'. The previous profile and all saved files were preserved. " + ex.Message, ex);
            }
        }

        private static T ReadSection<T>(JObject rawObject, T defaults, string propertyName) where T : class, Action
        {
            // Initial stock profiles contain object properties; later stock saves use action
            // names and JSON strings. Preserve both forms, preferring the explicit action key.
            JToken token = rawObject[defaults.GetActionName()] ?? rawObject[propertyName];
            if (token == null) return defaults;
            string json = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
            T result = JsonConvert.DeserializeObject<T>(json, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 32 });
            if (result == null) throw new InvalidDataException(propertyName + " cannot be null.");
            return result;
        }

        public static void Create(string profileName)
        {
            ValidateProfileName(profileName);
            lock (PersistenceGate)
            {
                string jsonFileName = Path.Combine(AppConfig.ProfileFolder, profileName + ".json");
                if (!File.Exists(jsonFileName))
                {
                    Directory.CreateDirectory(AppConfig.ProfileFolder);
                    WriteProfileDocument(jsonFileName, JObject.FromObject(new Profile(profileName)));
                }
                ProfileSingleton.Load(profileName);
            }
        }

        public static void Delete(string profileName)
        {
            try
            {
                if (profileName != "Default") { File.Delete(AppConfig.ProfileFolder + profileName + ".json"); }
            }
            catch { }
        }

        public static void Rename(string oldProfileName, string newProfileName)
        {
            ValidateProfileName(oldProfileName);
            ValidateProfileName(newProfileName);
            lock (PersistenceGate)
            {
                if (string.Equals(oldProfileName, newProfileName, StringComparison.Ordinal)) return;
                if (string.Equals(oldProfileName, "Default", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Default profile cannot be renamed.");
                string path = Path.Combine(AppConfig.ProfileFolder, newProfileName + ".json");
                if (File.Exists(path)) throw new IOException("A profile with this name already exists.");
                File.Move(Path.Combine(AppConfig.ProfileFolder, oldProfileName + ".json"), path);
                // Pending edits retain this object; its path must follow a completed rename.
                if (profile != null && string.Equals(profile.Name, oldProfileName, StringComparison.OrdinalIgnoreCase))
                    profile.Name = newProfileName;
            }
        }

        private static void ValidateProfileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name != name.Trim()
                || name.EndsWith(".", StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Use a profile name of 1–64 characters without path characters or trailing spaces/periods.");
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CLOCK$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && stem[3] >= '1' && stem[3] <= '9'))
                throw new ArgumentException("This profile name is reserved by Windows.");
        }

        public static void Copy(string profileName)
        {
            try
            {
                string jsonFileName = AppConfig.ProfileFolder + profileName + " Copy.json";
                if (profileName != "Default" && !File.Exists(jsonFileName)) {
                    File.Copy(AppConfig.ProfileFolder + profileName + ".json", jsonFileName);
                }
            }
            catch { }
        }

        public static void SetConfiguration(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (PersistenceGate)
            {
                if (profile == null) return;
                string path = Path.Combine(AppConfig.ProfileFolder, profile.Name + ".json");
                JObject json = JObject.Parse(File.ReadAllText(path));
                json[action.GetActionName()] = action.GetConfiguration();
                WriteProfileDocument(path, json);
            }
        }

        public static Profile GetCurrent()
        {
            return profile;
        }

        public static void SetVanillaDiagnostics(VanillaDiagnosticsSettings settings)
        {
            SetVanillaDiagnostics(profile, settings);
        }

        internal static void SetVanillaDiagnostics(Profile target, VanillaDiagnosticsSettings settings)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            var copy = new VanillaDiagnosticsSettings { PollIntervalMilliseconds = settings.PollIntervalMilliseconds,
                MemoryMapJson = settings.MemoryMapJson };
            lock (PersistenceGate)
            {
                string path = AppConfig.ProfileFolder + target.Name + ".json";
                JObject json = JObject.Parse(File.ReadAllText(path));
                json["VanillaDiagnostics"] = JObject.FromObject(copy);
                WriteProfileDocument(path, json);
                target.VanillaDiagnostics = copy;
            }
        }

        internal static void WriteProfileDocument(string path, JObject json)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json.ToString(Formatting.Indented));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
    }

    public class Profile
    {
        public string Name { get; set; }
        public VanillaDiagnosticsSettings VanillaDiagnostics { get; set; } = new VanillaDiagnosticsSettings();
        public UserPreferences UserPreferences { get; set; }
        public AHK AHK { get; set; }
        public Autopot Autopot { get; set; }
        public Autopot AutopotYgg { get; set; }
        public AutoRefreshSpammer AutoRefreshSpammer1 { get; set; }
        public AutoRefreshSpammer AutoRefreshSpammer2 { get; set; }
        public AutoRefreshSpammer AutoRefreshSpammer3 { get; set; }
        public AutoBuff Autobuff { get; set; }
        public StatusRecovery StatusRecovery { get; set; }
        public Macro SongMacro { get; set; }
        public Macro MacroSwitch { get; set; }

        public ATKDEFMode AtkDefMode { get; set; }
        public DebuffsRecovery DebuffsRecovery { get; set; }

        public Profile(string name)
        {
            this.Name = name;

            this.UserPreferences = new UserPreferences();
            this.AHK = new AHK();
            this.Autopot = new Autopot(Autopot.ACTION_NAME_AUTOPOT);
            this.AutopotYgg = new Autopot(Autopot.ACTION_NAME_AUTOPOT_YGG);
            this.AutoRefreshSpammer1 = new AutoRefreshSpammer(actionName: "AutoRefreshSpammer01");
            this.AutoRefreshSpammer2 = new AutoRefreshSpammer(actionName: "AutoRefreshSpammer02");
            this.AutoRefreshSpammer3 = new AutoRefreshSpammer(actionName: "AutoRefreshSpammer03");
            this.Autobuff = new AutoBuff();
            this.StatusRecovery = new StatusRecovery();
            this.SongMacro = new Macro(Macro.ACTION_NAME_SONG_MACRO, MacroSongForm.TOTAL_MACRO_LANES_FOR_SONGS);
            this.MacroSwitch = new Macro(Macro.ACTION_NAME_MACRO_SWITCH, MacroSwitchForm.TOTAL_MACRO_LANES);
            this.AtkDefMode = new ATKDEFMode();
            this.DebuffsRecovery = new DebuffsRecovery();
        }

        public static object GetByAction(dynamic obj, Action action)
        {
            if (obj != null && obj[action.GetActionName()] != null)
            {
                return obj[action.GetActionName()].ToString();
            }

            return action.GetConfiguration();
        }

        public static List<string> ListAll()
        {
            List<string> profiles = new List<string>();
            try
            {
                string[] files = Directory.GetFiles(AppConfig.ProfileFolder, "*.json");

                foreach (string fileName in files)
                {
                    string profileName = Path.GetFileNameWithoutExtension(fileName);
                    profiles.Add(profileName);
                }
            }
            catch { }
            return profiles;
        }
    }

}
