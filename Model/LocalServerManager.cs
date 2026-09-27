using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace _4RTools.Model
{
    internal class LocalServerManager
    {
        private static readonly LocalServerStore store = new LocalServerStore(Vanilla.VanillaAppData.LocalServersPath);

        static LocalServerManager()
        {
            Vanilla.VanillaAppData.InitializeAndMigrateLegacy(AppDomain.CurrentDomain.BaseDirectory);
        }

        public static ClientDTO AddServer(string hpAddress, string nameAddress, string processName)
        {
            ClientDTO added = store.Add(hpAddress, nameAddress, processName);
            ClientListSingleton.AddClient(new Client(added));
            return added;
        }

        public static ClientDTO UpdateServer(ClientDTO original, string hpAddress, string nameAddress, string processName)
        {
            ClientDTO updated = store.Update(original, hpAddress, nameAddress, processName);
            ClientListSingleton.RemoveClient(Client.FromDTO(original));
            ClientListSingleton.AddClient(new Client(updated));
            return updated;
        }

        public static void RemoveClient(ClientDTO original)
        {
            store.Remove(original);
            ClientListSingleton.RemoveClient(Client.FromDTO(original));
        }

        public static List<ClientDTO> GetLocalClients()
        {
            // Startup can still operate without a broken optional stock server list.
            // Mutation uses the strict store and never overwrites that broken file.
            try { return store.Read(); }
            catch (JsonException) { return new List<ClientDTO>(); }
            catch (InvalidDataException) { return new List<ClientDTO>(); }
        }

        public static bool IsHex(IEnumerable<char> chars)
        {
            return LocalServerStore.IsHex(chars);
        }
    }

    // Isolated file store lets edits validate and commit before touching the live catalog.
    internal sealed class LocalServerStore
    {
        private static readonly object gate = new object();
        private readonly string path;
        internal LocalServerStore(string path) { this.path = Path.GetFullPath(path); }

        internal List<ClientDTO> Read()
        {
            lock (gate)
            {
                if (!File.Exists(path)) return new List<ClientDTO>();
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The local server list exceeds 1 MiB and was preserved.");
                var clients = JsonConvert.DeserializeObject<List<ClientDTO>>(File.ReadAllText(path));
                if (clients == null || clients.Any(client => client == null))
                    throw new InvalidDataException("The local server list is invalid and was preserved.");
                return clients;
            }
        }

        internal ClientDTO Add(string hpAddress, string nameAddress, string processName)
        {
            ClientDTO candidate = Validate(hpAddress, nameAddress, processName);
            lock (gate)
            {
                var clients = Read();
                if (clients.Any(client => Matches(client, candidate))) throw new InvalidOperationException("This server already exists.");
                clients.Add(candidate);
                Write(clients);
                return candidate;
            }
        }

        internal ClientDTO Update(ClientDTO original, string hpAddress, string nameAddress, string processName)
        {
            ClientDTO candidate = Validate(hpAddress, nameAddress, processName);
            lock (gate)
            {
                var clients = Read();
                int index = FindOriginal(clients, original);
                if (clients.Where((client, position) => position != index).Any(client => Matches(client, candidate)))
                    throw new InvalidOperationException("This server already exists.");
                candidate.description = clients[index].description;
                clients[index] = candidate;
                Write(clients);
                return candidate;
            }
        }

        internal void Remove(ClientDTO original)
        {
            lock (gate)
            {
                var clients = Read();
                clients.RemoveAt(FindOriginal(clients, original));
                Write(clients);
            }
        }

        private static int FindOriginal(List<ClientDTO> clients, ClientDTO original)
        {
            var matches = clients.Select((client, index) => new { client, index }).Where(entry => Matches(entry.client, original)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("This server changed in another window. Reopen it before editing.");
            return matches[0].index;
        }

        private static bool Matches(ClientDTO first, ClientDTO second)
        {
            return first != null && second != null && string.Equals(first.name, second.name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.hpAddress, second.hpAddress, StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.nameAddress, second.nameAddress, StringComparison.OrdinalIgnoreCase);
        }

        internal static ClientDTO Validate(string hpAddress, string nameAddress, string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) throw new ArgumentException("Choose a process name.");
            processName = processName.Trim();
            if (Client.IsVanillaProcessName(processName))
                throw new ArgumentException("Use the Vanilla workspace to connect Vanilla MMO through its read-only state layer.");
            if (hpAddress == null || hpAddress.Length != 8 || !IsHex(hpAddress))
                throw new ArgumentException("HP address needs exactly eight hexadecimal digits.");
            if (nameAddress == null || nameAddress.Length != 8 || !IsHex(nameAddress))
                throw new ArgumentException("Name address needs exactly eight hexadecimal digits.");
            return new ClientDTO(processName, null, hpAddress, nameAddress);
        }

        internal static bool IsHex(IEnumerable<char> chars)
        {
            return chars != null && chars.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        private void Write(List<ClientDTO> clients)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(clients, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
