using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace _4RTools.Model.Vanilla
{
    public sealed class VanillaUpdateInfo
    {
        public Version Version { get; set; }
        public string TagName { get; set; }
        public string ReleaseUrl { get; set; }
        public string ZipUrl { get; set; }
        public string ChecksumUrl { get; set; }
        public string ZipName { get; set; }
    }

    /// <summary>Verified GitHub-release updater. User data lives outside the install directory.</summary>
    public static class VanillaUpdater
    {
        public const string ReleasesUrl = VanillaPrivateReleaseClient.ReleasesUrl;
        private static readonly VanillaPrivateReleaseClient Releases = VanillaPrivateReleaseClient.Create();

        public static Version CurrentVersion
        {
            get
            {
                var value = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(value.Major, value.Minor, Math.Max(0, value.Build));
            }
        }
        public static string CurrentVersionText { get { return CurrentVersion.ToString(3); } }
        public static bool IsNewerVersion(Version candidate, Version current) { return candidate != null && current != null && candidate > current; }

        public static async Task<VanillaUpdateInfo> CheckAsync()
        {
            var latest = await ReadLatestReleaseAsync().ConfigureAwait(false);
            return IsNewerVersion(latest.Version, CurrentVersion) ? latest : null;
        }

        // Also used by the headless published-release probe to verify an equal-version
        // release through the identical authenticated discovery and staging path.
        public static async Task<VanillaUpdateInfo> ReadLatestReleaseAsync()
        {
            return ParseRelease(await Releases.ReadLatestAsync().ConfigureAwait(false));
        }

        internal static VanillaUpdateInfo ParseRelease(string json)
        {
            var root = JObject.Parse(json);
            if ((bool?)root["draft"] != false || (bool?)root["prerelease"] != false)
                throw new InvalidDataException("Updater requires a published stable release.");
            string tag = (string)root["tag_name"];
            if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("GitHub returned a release without a tag.");
            Version version;
            if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out version)) throw new InvalidDataException("Unsupported release tag: " + tag);
            if (version.Revision >= 0 || version.Build < 0 || !System.Text.RegularExpressions.Regex.IsMatch(tag, "^[vV]?[0-9]+\\.[0-9]+\\.[0-9]+$"))
                throw new InvalidDataException("Release tag must contain three version components.");
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));

            string zipName = "4RTools-Vanilla-v" + version.ToString(3) + "-portable.zip";
            var assets = root["assets"] as JArray;
            if (assets == null) throw new InvalidDataException("GitHub release has no assets.");
            string zipUrl = AssetUrl(assets, zipName);
            string checksumUrl = AssetUrl(assets, zipName + ".sha256");
            return new VanillaUpdateInfo
            {
                Version = version,
                TagName = tag,
                ReleaseUrl = ReleasesUrl + "/tag/" + Uri.EscapeDataString(tag),
                ZipUrl = zipUrl,
                ChecksumUrl = checksumUrl,
                ZipName = zipName
            };
        }

        private static string AssetUrl(JArray assets, string name)
        {
            var matches = assets.OfType<JObject>().Where(a => string.Equals((string)a["name"], name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || (string)matches[0]["state"] != "uploaded")
                throw new InvalidDataException("Release asset is missing, duplicated, or unfinished: " + name);
            return VanillaPrivateReleaseClient.AssetUrl((long?)matches[0]["id"] ?? 0);
        }

        public static async Task<string> DownloadAndStageAsync(VanillaUpdateInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Version == null || info.ZipName != "4RTools-Vanilla-v" + info.Version.ToString(3) + "-portable.zip")
                throw new InvalidDataException("Update package name does not match its version.");
            VanillaAppData.InitializeAndMigrateLegacy(AppDomain.CurrentDomain.BaseDirectory);
            string stage = Path.Combine(VanillaAppData.UpdatesDirectory, "v" + info.Version.ToString(3) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                byte[] zip = await Releases.ReadAssetAsync(info.ZipUrl, 256 * 1024 * 1024).ConfigureAwait(false);
                string checksumText = Encoding.UTF8.GetString(await Releases.ReadAssetAsync(info.ChecksumUrl, 16384).ConfigureAwait(false));
                string expected = checksumText.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(expected) || expected.Length != 64) throw new InvalidDataException("Release checksum is malformed.");
                string actual = Sha256(zip);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Downloaded update checksum does not match the GitHub release checksum.");

                string zipPath = Path.Combine(stage, info.ZipName);
                File.WriteAllBytes(zipPath, zip);
                string extractRoot = Path.Combine(stage, "payload");
                Directory.CreateDirectory(extractRoot);
                SafeExtract(zipPath, extractRoot);
                string[] executables = Directory.GetFiles(extractRoot, "4RTools-Vanilla.exe", SearchOption.AllDirectories);
                if (executables.Length != 1) throw new InvalidDataException("Update package must contain exactly one 4RTools-Vanilla.exe.");
                string payload = Path.GetDirectoryName(executables[0]);
                VerifyPayloadManifest(payload);
                var fileVersion = FileVersionInfo.GetVersionInfo(executables[0]);
                if (fileVersion.FileMajorPart != info.Version.Major || fileVersion.FileMinorPart != info.Version.Minor || fileVersion.FileBuildPart != info.Version.Build)
                    throw new InvalidDataException("Update executable version does not match release tag.");
                return payload;
            }
            catch
            {
                try { Directory.Delete(stage, true); } catch { }
                throw;
            }
        }

        public static void BeginApplyAndRestart(string payloadDirectory)
        {
            if (string.IsNullOrWhiteSpace(payloadDirectory)) throw new ArgumentException("Update payload is missing.");
            string stagedExe = Path.Combine(payloadDirectory, "4RTools-Vanilla.exe");
            if (!File.Exists(stagedExe)) throw new FileNotFoundException("Staged updater executable is missing.", stagedExe);
            int pid = Process.GetCurrentProcess().Id;
            string install = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var start = new ProcessStartInfo
            {
                FileName = stagedExe,
                Arguments = "--apply-update " + pid + " " + Quote(install) + " " + Quote(payloadDirectory),
                WorkingDirectory = payloadDirectory,
            };
            Process.Start(start);
        }

        public static bool TryHandleApplyCommand(string[] args)
        {
            if (args == null || args.Length == 0 || !string.Equals(args[0], "--apply-update", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                if (args.Length != 4) throw new ArgumentException("Update helper arguments are invalid.");
                int parentPid;
                if (!int.TryParse(args[1], out parentPid) || parentPid <= 0) throw new ArgumentException("Update parent PID is invalid.");
                string install = Path.GetFullPath(args[2]);
                string payload = Path.GetFullPath(args[3]);
                WaitForExit(parentPid, 60000);
                VanillaUpdateTransaction.Apply(payload, install, destinationExe =>
                {
                    // Persistent profiles live outside the installation. Do not migrate or
                    // delete user data inside a managed-file transaction.
                    using (var started = Process.Start(new ProcessStartInfo { FileName = destinationExe, WorkingDirectory = install }))
                    {
                        if (started == null) throw new IOException("Windows did not start the updated application.");
                        if (started.WaitForExit(1500)) throw new IOException("Updated application exited during its startup check; restoring the previous files.");
                    }
                });
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(VanillaAppData.LogsDirectory);
                    VanillaLogRotation.Append(Path.Combine(VanillaAppData.LogsDirectory, "update-error.log"),
                        "update-error", DateTimeOffset.Now.ToString("u") + " " + ex + Environment.NewLine);
                }
                catch { }
                MessageBox.Show(ex.Message, "4RTools Vanilla update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return true;
        }

        private static void WaitForExit(int pid, int timeoutMs)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    if (!process.WaitForExit(timeoutMs)) throw new TimeoutException("The previous 4RTools process did not exit in time.");
            }
            catch (ArgumentException) { }
        }

        internal static void SafeExtract(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                const long maximumExpandedBytes = 512L * 1024 * 1024;
                if (archive.Entries.Count > 10000) throw new InvalidDataException("Update ZIP contains too many entries.");
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    if (Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(":")) throw new InvalidDataException("Update ZIP contains an unsafe path.");
                    if (entry.Length < 0 || entry.Length > maximumExpandedBytes - total) throw new InvalidDataException("Expanded update exceeds the supported size.");
                    total += entry.Length;
                    string path = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update ZIP contains an unsafe path.");
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open())
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long written = 0; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            if (written + count > entry.Length) throw new InvalidDataException("Update ZIP entry exceeds its declared size.");
                            output.Write(buffer, 0, count); written += count;
                        }
                        if (written != entry.Length) throw new InvalidDataException("Update ZIP entry is incomplete.");
                    }
                }
            }
        }

        internal static void VerifyPayloadManifest(string payload)
        {
            VanillaUpdateTransaction.Verify(payload);
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

        private static string Quote(string value) { return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\""; }
    }
}
