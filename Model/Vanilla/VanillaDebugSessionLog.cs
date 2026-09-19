using System;
using System.IO;
using System.Linq;
using System.Text;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// One process-owned debug session. The live file is timestamp/PID named from birth,
    /// so overlapping/updater launches can never append to another process's debug file.
    /// Individual parts are hard-capped and history is bounded.
    /// </summary>
    internal sealed class VanillaDebugSessionLog
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly object gate = new object();
        private readonly string directory;
        private readonly string stem;
        private readonly long maxFileBytes;
        private readonly long maxFamilyBytes;
        private readonly int maxFamilyFiles;
        private int part = 1;
        private string currentPath;

        internal VanillaDebugSessionLog(string logsDirectory, string sessionToken)
            : this(logsDirectory, sessionToken, VanillaLogRotation.DefaultMaxFileBytes,
                  VanillaLogRotation.DefaultMaxFamilyBytes, VanillaLogRotation.DefaultMaxFamilyFiles) { }

        internal VanillaDebugSessionLog(string logsDirectory, string sessionToken,
            long maxFileBytes, long maxFamilyBytes, int maxFamilyFiles)
        {
            if (string.IsNullOrWhiteSpace(logsDirectory)) throw new ArgumentNullException(nameof(logsDirectory));
            if (string.IsNullOrWhiteSpace(sessionToken)) throw new ArgumentNullException(nameof(sessionToken));
            if (maxFileBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
            if (maxFamilyBytes < maxFileBytes) throw new ArgumentOutOfRangeException(nameof(maxFamilyBytes));
            if (maxFamilyFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFamilyFiles));

            directory = Path.GetFullPath(logsDirectory);
            Directory.CreateDirectory(directory);
            this.maxFileBytes = maxFileBytes;
            this.maxFamilyBytes = maxFamilyBytes;
            this.maxFamilyFiles = maxFamilyFiles;

            string baseStem = "debug-" + Sanitize(sessionToken);
            stem = UniqueStem(baseStem);
            currentPath = PartPath(part);
            CreateEmpty(currentPath);
            Prune();
        }

        internal string CurrentPath
        {
            get { lock (gate) return currentPath; }
        }

        internal void WriteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (gate)
            {
                string remaining = text;
                while (remaining.Length > 0)
                {
                    long currentLength = File.Exists(currentPath) ? new FileInfo(currentPath).Length : 0L;
                    if (currentLength >= maxFileBytes)
                    {
                        RotatePart();
                        currentLength = 0L;
                    }

                    long available = maxFileBytes - currentLength;
                    int chars = VanillaLogRotation.PrefixLengthWithinBytes(remaining, available);
                    if (chars <= 0)
                    {
                        RotatePart();
                        continue;
                    }

                    string piece = remaining.Substring(0, chars);
                    File.AppendAllText(currentPath, piece, Utf8);
                    remaining = remaining.Substring(chars);
                    if (remaining.Length > 0) RotatePart();
                }
                Prune();
            }
        }

        private void RotatePart()
        {
            part++;
            currentPath = PartPath(part);
            CreateEmpty(currentPath);
        }

        private string PartPath(int value)
        {
            return Path.Combine(directory, stem + (value <= 1 ? "" : "-part" + value.ToString("00")) + ".log");
        }

        private string UniqueStem(string requested)
        {
            string candidate = requested;
            int suffix = 1;
            while (File.Exists(Path.Combine(directory, candidate + ".log"))
                || Directory.GetFiles(directory, candidate + "-part*.log", SearchOption.TopDirectoryOnly).Length > 0)
                candidate = requested + "-" + (++suffix).ToString("00");
            return candidate;
        }

        private static void CreateEmpty(string path)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
        }

        private void Prune()
        {
            try
            {
                var files = new DirectoryInfo(directory).GetFiles("debug-*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                long total = files.Sum(f => f.Length);
                for (int i = files.Count - 1;
                    i >= 0 && (files.Count > maxFamilyFiles || total > maxFamilyBytes);
                    i--)
                {
                    FileInfo file = files[i];
                    if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                    long length = file.Length;
                    try
                    {
                        file.Delete();
                        total -= length;
                        files.RemoveAt(i);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string Sanitize(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '-');
            return value.Trim();
        }
    }
}
