using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// Validate the complete staged payload, back up every destination before the first
    /// replacement, and roll back on copy/verification/start failure. User data is never
    /// part of an update payload. Backups are retained as recovery evidence.
    /// </summary>
    internal static class VanillaUpdateTransaction
    {
        internal static Dictionary<string, string> Verify(string payload, bool rejectExtras = true)
        {
            string root = Root(payload);
            CheckLinks(root);
            string manifest = Path.Combine(root, "SHA256SUMS.txt");
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Update checksum manifest is missing or oversized.");
            CheckLinks(manifest);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(manifest))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                Match match = Regex.Match(raw, @"^([0-9a-fA-F]{64})  (.+)$");
                if (!match.Success) throw new InvalidDataException("Malformed update checksum manifest.");
                string name = Relative(match.Groups[2].Value);
                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) || files.ContainsKey(name))
                    throw new InvalidDataException("Update manifest contains a duplicate/self-referential entry.");
                string path = Under(root, name);
                CheckLinks(path);
                if (!File.Exists(path) || !string.Equals(Hash(path), match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update checksum verification failed for " + name + ".");
                files.Add(name, match.Groups[1].Value.ToLowerInvariant());
                if (files.Count > 10000) throw new InvalidDataException("Update manifest is oversized.");
            }
            if (!files.ContainsKey("4RTools-Vanilla.exe")) throw new InvalidDataException("Update manifest does not cover the executable.");
            if (rejectExtras)
            {
                foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)) CheckLinks(directory);
                foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string name = path.Substring(root.Length).Replace('\\', '/');
                    if (!name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) && !files.ContainsKey(name))
                        throw new InvalidDataException("Update payload contains an unverified extra file.");
                }
            }
            return files;
        }

        internal static string Apply(string payload, string install, Action<string> start,
            Action<string, string> copy = null)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            string mutexKey;
            using (var digest = SHA256.Create()) mutexKey = string.Concat(digest.ComputeHash(Encoding.UTF8.GetBytes(Root(install).ToUpperInvariant())).Take(16).Select(b => b.ToString("x2")));
            using (var mutex = new Mutex(false, "Local\\4RTools.Update." + mutexKey))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new IOException("Another update owns this installation.");
                    return ApplyOwned(payload, install, start, copy);
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }

        private static string ApplyOwned(string payload, string install, Action<string> start, Action<string, string> copy)
        {
            string sourceRoot = Root(payload), targetRoot = Root(install);
            if (sourceRoot.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)
                || targetRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update staging and installation must be separate directories.");
            if (targetRoot == Root(Path.GetPathRoot(targetRoot))) throw new InvalidDataException("Cannot update a drive root.");
            CheckLinks(targetRoot);
            var files = Verify(sourceRoot);
            string[] names = files.Keys.Concat(new[] { "SHA256SUMS.txt" }).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            var existed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string backup = Path.Combine(Path.GetDirectoryName(targetRoot.TrimEnd(Path.DirectorySeparatorChar)),
                ".4RTools-update-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "UPDATE-BACKUP.txt"), "4RTools managed update backup\n", new UTF8Encoding(false));
            var replaced = new List<string>();
            var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string> write = copy ?? CopyWithRetry;
            foreach (string name in names)
            {
                string destination = Under(targetRoot, name);
                CheckLinks(destination);
                if (Directory.Exists(destination)) throw new IOException("An update destination is a directory.");
                if (File.Exists(destination))
                {
                    string old = Under(Root(backup), name);
                    Directory.CreateDirectory(Path.GetDirectoryName(old));
                    File.Copy(destination, old, false);
                    existed.Add(name);
                }
            }
            File.WriteAllLines(Path.Combine(backup, "JOURNAL.txt"), names.Select(n => (existed.Contains(n) ? "replace " : "new ") + n));
            try
            {
                foreach (string name in names)
                {
                    string destination = Under(targetRoot, name);
                    string directory = Path.GetDirectoryName(destination);
                    for (string ancestor = directory; !Directory.Exists(ancestor); ancestor = Path.GetDirectoryName(ancestor))
                        createdDirectories.Add(ancestor);
                    Directory.CreateDirectory(directory);
                    CheckLinks(destination);
                    replaced.Add(name);
                    write(Under(sourceRoot, name), destination);
                }
                Verify(targetRoot, rejectExtras: false);
                start(Path.Combine(targetRoot, "4RTools-Vanilla.exe"));
                WriteResult(backup, "Applied; previous managed files retained.");
                return backup;
            }
            catch (Exception failure)
            {
                var rollbackFailures = new List<Exception>();
                foreach (string name in replaced.AsEnumerable().Reverse())
                {
                    try
                    {
                        string destination = Under(targetRoot, name);
                        if (existed.Contains(name)) CopyWithRetry(Under(Root(backup), name), destination);
                        else if (File.Exists(destination)) File.Delete(destination);
                    }
                    catch (Exception ex) { rollbackFailures.Add(ex); }
                }
                foreach (string directory in createdDirectories.OrderByDescending(d => d.Length))
                    try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                    catch { }
                WriteResult(backup, rollbackFailures.Count == 0 ? "Rolled back." : "Rollback incomplete; backup retained.");
                if (rollbackFailures.Count != 0)
                    throw new IOException("Update failed and rollback could not restore every file. Previous files are preserved at " + backup + ".",
                        new AggregateException(new[] { failure }.Concat(rollbackFailures)));
                throw new IOException("Update failed; previous application files were restored. Backup: " + backup + ".", failure);
            }
        }

        private static void WriteResult(string backup, string text)
        { try { File.WriteAllText(Path.Combine(backup, "RESULT.txt"), text + Environment.NewLine); } catch { } }

        private static void CopyWithRetry(string from, string to)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { if (attempt >= 4) throw; Thread.Sleep(250); }
            }
        }

        private static string Root(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Update directory is missing.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        private static string Relative(string value)
        {
            string name = (value ?? "").Replace('\\', '/');
            string[] pieces = name.Split('/');
            if (Path.IsPathRooted(name) || name.Contains(":") || pieces.Any(p => p.Length == 0 || p == "." || p == ".."
                || p.TrimEnd(' ', '.') != p || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(p.Split('.')[0], @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("Unsafe update manifest path.");
            if (new[] { "Profiles", "Profile", "VanillaReconnect", "Logs", "UpdateAccess", "Updates", ".git" }
                .Any(p => pieces[0].Equals(p, StringComparison.OrdinalIgnoreCase))
                || name.Equals("temporary-actions.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("supported_servers.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update payload must not replace persistent user data.");
            return name;
        }

        private static string Under(string root, string name)
        {
            string path = Path.GetFullPath(Path.Combine(root, Relative(name).Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escaped its directory.");
            return path;
        }

        private static void CheckLinks(string path)
        {
            for (string p = path.TrimEnd(Path.DirectorySeparatorChar); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Update paths must not contain links or junctions.");
        }

        private static string Hash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(input).Select(b => b.ToString("x2")));
        }
    }
}
