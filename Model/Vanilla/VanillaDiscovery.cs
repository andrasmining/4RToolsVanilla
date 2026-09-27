using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using _4RTools.Utils;

namespace _4RTools.Model.Vanilla
{
    public sealed class VanillaExecutableIdentity
    {
        public string Sha256 { get; set; }
        public long FileSize { get; set; }
        public uint ImageTimestamp { get; set; }
        public ushort Machine { get; set; }
        public uint ImageSize { get; set; }
        public string Version { get; set; }
        public List<VanillaImageSection> Sections { get; set; } = new List<VanillaImageSection>();

        public static VanillaExecutableIdentity Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(file))
            using (var sha = SHA256.Create())
            {
                var identity = new VanillaExecutableIdentity { FileSize = file.Length };
                identity.Sha256 = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                file.Position = 0;
                if (reader.ReadUInt16() != 0x5A4D || file.Length < 64) throw new InvalidDataException("Invalid executable DOS header.");
                file.Position = 0x3C;
                uint pe = reader.ReadUInt32();
                if (pe > file.Length - 88) throw new InvalidDataException("Invalid PE header location.");
                file.Position = pe;
                if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException("Invalid executable PE header.");
                identity.Machine = reader.ReadUInt16();
                ushort sections = reader.ReadUInt16();
                identity.ImageTimestamp = reader.ReadUInt32();
                file.Position = pe + 20;
                ushort optionalSize = reader.ReadUInt16();
                file.Position = pe + 24 + 56;
                identity.ImageSize = reader.ReadUInt32();
                long sectionStart = pe + 24L + optionalSize;
                if (sections > 96 || sectionStart + sections * 40L > file.Length) throw new InvalidDataException("Invalid executable section table.");
                for (int i = 0; i < sections; i++)
                {
                    file.Position = sectionStart + i * 40;
                    string name = Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0');
                    uint size = reader.ReadUInt32(), offset = reader.ReadUInt32();
                    file.Position = sectionStart + i * 40 + 36;
                    uint flags = reader.ReadUInt32();
                    if ((ulong)offset + size > identity.ImageSize) throw new InvalidDataException("Section exceeds image bounds.");
                    identity.Sections.Add(new VanillaImageSection { Name = name, Offset = offset, Size = size, Flags = flags });
                }
                identity.Version = FileVersionInfo.GetVersionInfo(path).FileVersion;
                return identity;
            }
        }
    }

    public sealed class VanillaImageSection
    {
        public string Name { get; set; }
        public uint Offset { get; set; }
        public uint Size { get; set; }
        public uint Flags { get; set; }
    }

    public sealed class VanillaDiscoveryResult
    {
        public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
        public int ProcessId { get; set; }
        public string ExecutablePath { get; set; }
        public ulong ModuleBase { get; set; }
        public VanillaExecutableIdentity Identity { get; set; }
        public List<VanillaVitalCandidate> Candidates { get; set; } = new List<VanillaVitalCandidate>();
        public long BytesRead { get; set; }
        public string Error { get; set; }
        public string CaptureStatus { get; set; }
    }

    public sealed class VanillaVitalCandidate
    {
        public uint ModuleOffset { get; set; }
        public uint HP { get; set; }
        public uint MaxHP { get; set; }
        public uint SP { get; set; }
        public uint MaxSP { get; set; }
    }

    // Developer discovery of player vitals only. Never scans actors, security modules,
    // executable code, heap allocations, packets, or process memory outside the main image.
    public static class VanillaDiscovery
    {
        public static VanillaDiscoveryResult Inspect(int processId, bool scanVitals, string capturePath, uint[] expected = null, bool restoreWindow = false)
        {
            var result = new VanillaDiscoveryResult { ProcessId = processId };
            try
            {
                using (var memory = new ReadOnlyProcessMemory(processId))
                {
                    if (!string.Equals(memory.ProcessName, "Vanilla MMO", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Discovery only supports the selected Vanilla MMO executable.");
                    result.ExecutablePath = memory.ExecutablePath;
                    result.ModuleBase = memory.MainModuleBaseAddress;
                    result.Identity = VanillaExecutableIdentity.Read(memory.ExecutablePath);
                    if (result.Identity.Machine != 0x14C || memory.PointerSize != 4)
                        throw new InvalidOperationException("This discovery pass requires a verified x86 Vanilla image.");
                    if (!string.IsNullOrEmpty(capturePath)) result.CaptureStatus = Capture(processId, capturePath, restoreWindow);
                    if (!scanVitals) return result;
                    if (expected != null && expected.Length != 4) throw new ArgumentException("Expected four displayed values: HP, MaxHP, SP, MaxSP.");
                    const long maximumRead = 16 * 1024 * 1024;
                    foreach (var section in result.Identity.Sections.Where(s =>
                        (s.Name == ".data" || s.Name == ".bss") && (s.Flags & 0x80000000) != 0 && (s.Flags & 0x20000000) == 0))
                    {
                        if (section.Size > maximumRead - result.BytesRead) throw new InvalidOperationException("Writable image data exceeds the 16 MiB discovery bound.");
                        byte[] bytes = new byte[section.Size];
                        for (int offset = 0; offset < bytes.Length; offset += 256)
                        {
                            int count = Math.Min(256, bytes.Length - offset);
                            byte[] chunk = memory.ReadBytes(checked(memory.MainModuleBaseAddress + section.Offset + (uint)offset), count);
                            Buffer.BlockCopy(chunk, 0, bytes, offset, count);
                            result.BytesRead += count;
                        }
                        for (int offset = 0; offset <= bytes.Length - 16; offset += 4)
                        {
                            uint hp = BitConverter.ToUInt32(bytes, offset), maxHp = BitConverter.ToUInt32(bytes, offset + 4);
                            uint sp = BitConverter.ToUInt32(bytes, offset + 8), maxSp = BitConverter.ToUInt32(bytes, offset + 12);
                            bool match = expected != null ? hp == expected[0] && maxHp == expected[1] && sp == expected[2] && maxSp == expected[3]
                                : hp > 0 && maxHp >= hp && maxHp <= 1000000 && maxHp >= 40 && maxSp >= sp && maxSp >= 10 && maxSp <= 100000 && maxHp >= maxSp;
                            if (match && result.Candidates.Count < 512) result.Candidates.Add(new VanillaVitalCandidate
                            { ModuleOffset = checked(section.Offset + (uint)offset), HP = hp, MaxHP = maxHp, SP = sp, MaxSP = maxSp });
                        }
                    }
                }
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }

        private static string Capture(int processId, string path, bool restoreWindow)
        {
            IntPtr foreground = GetForegroundWindow();
            uint owner;
            bool ownsForeground = foreground != IntPtr.Zero
                && GetWindowThreadProcessId(foreground, out owner) != 0 && owner == (uint)processId;
            if (!restoreWindow && !ownsForeground)
                return "Screenshot skipped: selected Vanilla client is in the background. Memory discovery continues.";
            Func<bool> cancelled = () => !restoreWindow && GetForegroundWindow() != foreground;
            using (var input = new VanillaForegroundInput(processId, ownsForeground ? foreground : IntPtr.Zero, cancelled))
            using (var bitmap = input.CaptureClientBitmapForObservation())
            {
                bitmap.Save(path, ImageFormat.Png);
                return "Captured the verified foreground Vanilla client.";
            }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
