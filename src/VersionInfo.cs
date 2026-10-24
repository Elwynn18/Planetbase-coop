using System;
using System.IO;
using System.Security.Cryptography;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    // What must match between host and joiner. The game code hash covers "same Windows build"
    // whether the player runs it on Windows or through Proton (it is the same file).
    public class VersionInfo
    {
        public int Protocol;
        public string Mod;
        public string ModHash;
        public string Game;
        public string GameCodeHash;
        public string Unity;

        private static VersionInfo sLocal;

        public static VersionInfo Local
        {
            get
            {
                if (sLocal == null)
                {
                    sLocal = new VersionInfo
                    {
                        Protocol = Net.Wire.ProtocolVersion,
                        Mod = Plugin.Version,
                        ModHash = HashFile(typeof(Plugin).Assembly.Location),
                        Game = Definitions.VersionNumber.ToString(),
                        GameCodeHash = HashFile(Path.Combine(Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll")),
                        Unity = Application.unityVersion,
                    };
                }
                return sLocal;
            }
        }

        public static string HashFile(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var f = File.OpenRead(path))
                {
                    return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception e)
            {
                Log.Warn("hash failed for " + path + ": " + e.Message);
                return "unknown";
            }
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Protocol); w.Write(Mod); w.Write(ModHash); w.Write(Game); w.Write(GameCodeHash); w.Write(Unity);
        }

        public static VersionInfo Read(BinaryReader r)
        {
            return new VersionInfo { Protocol = r.ReadInt32(), Mod = r.ReadString(), ModHash = r.ReadString(), Game = r.ReadString(), GameCodeHash = r.ReadString(), Unity = r.ReadString() };
        }

        public static string Short(string hash) => hash == null || hash.Length < 12 ? hash : hash.Substring(0, 12);

        // null when compatible, else a message both players can act on.
        public string Mismatch(VersionInfo other)
        {
            if (other.Protocol != Protocol)
                return $"Different co-op protocol (host {Protocol}, you {other.Protocol}). Install the same Planetbase Co-op version as the host (host has {Mod}).";
            if (other.Game != Game || other.GameCodeHash != GameCodeHash)
                return $"Different game build. Host: Planetbase {Game} ({Short(GameCodeHash)}), you: {other.Game} ({Short(other.GameCodeHash)}). Update both games on Steam (and turn off any beta branch) so you run the same version.";
            if (other.Unity != Unity)
                return $"Different Unity runtime (host {Unity}, you {other.Unity}). Update both games on Steam.";
            if (other.Mod != Mod || other.ModHash != ModHash)
                return $"Different Planetbase Co-op version. Host: {Mod} ({Short(ModHash)}), you: {other.Mod} ({Short(other.ModHash)}). Install the host's exact mod version.";
            return null;
        }

        public override string ToString() => $"mod {Mod} ({Short(ModHash)}), game {Game} ({Short(GameCodeHash)}), unity {Unity}, protocol {Protocol}";
    }
}
