using System;
using System.Collections.Generic;
using System.IO;
using _4RTools.Model.Vanilla;

namespace _4RTools.Utils
{
    internal class AppConfig
    {
        public static string Name = "4RTools Vanilla";
        public static string ProfileFolder = InitializeProfileFolder();
        public static string Website = "https://www.4rtools.com.br";
        public static string GithubLink = "https://github.com/andrasmining/4RToolsVanilla";
        public static string DiscordLink = "https://discord.gg/AtZ2fJVtBz";
        public static string _4RClientsURL = "https://storage.googleapis.com/4rtools/supported_servers.json";
        public static string _4RAdvertiserUrl = "https://storage.googleapis.com/4rtools/advertisers.json";
        public static string _4RApiHost = "https://api.4rtools.com.br/api";
        public static string Version = "v0.6.1";

        private static string InitializeProfileFolder()
        {
            VanillaAppData.InitializeAndMigrateLegacy(AppDomain.CurrentDomain.BaseDirectory);
            return VanillaAppData.StockProfilesDirectory + Path.DirectorySeparatorChar;
        }
    }
}
