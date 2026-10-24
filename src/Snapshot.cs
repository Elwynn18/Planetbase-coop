using System;
using System.Collections.Generic;
using System.Reflection;
using Module = Planetbase.Module;
using System.Text;
using System.Xml;
using HarmonyLib;
using Planetbase;

namespace PlanetbaseCoop
{
    // Join = the host's save, built in memory with the game's own serializers, loaded on the
    // joiner through the game's own loader (GameStateGame stages) from memory.
    public static class Snapshot
    {
        public const string MagicPath = "coop://snapshot.sav";

        private static readonly FieldInfo fDocument = AccessTools.Field(typeof(Serialization), "mDocument");
        private static readonly FieldInfo fPath = AccessTools.Field(typeof(Serialization), "mPath");
        private static readonly FieldInfo fIds = AccessTools.Field(typeof(Serialization), "mIds");

        private static string sPendingXml;

        // Same order as GameStateGame.saveGame (1.4.2), minus the screenshot and the file write.
        public static string Capture()
        {
            if (fDocument.GetValue(null) != null) throw new InvalidOperationException("the game is saving right now");
            XmlNode root = Serialization.beginSave("coop-memory", "save-game");
            try
            {
                Singleton<IdGenerator>.getInstance().serialize(root, "id-generator");
                Singleton<PlanetManager>.getInstance().serialize(root, "planet");
                Singleton<MilestoneManager>.getInstance().serialize(root, "milestones");
                Singleton<TechManager>.getInstance().serialize(root, "techs");
                Singleton<EnvironmentManager>.getInstance().serialize(root, "environment");
                Singleton<TerrainGenerator>.getInstance().serialize(root, "terrain");
                CameraManager.getInstance().serialize(root, "camera");
                Singleton<DisasterManager>.getInstance().serialize(root);
                Singleton<Colony>.getInstance().serialize(root, "colony");
                Singleton<LandingShipManager>.getInstance().serialize(root, "ship-manager");
                Singleton<StatsCollector>.getInstance().serialize(root, "stats");
                Singleton<VisitorEventManager>.getInstance().serialize(root, "visitor-events");
                Singleton<GameHintManager>.getInstance().serialize(root, "game-hints");
                Singleton<MeteorManager>.getInstance().serialize(root, "meteor-manager");
                Singleton<ThunderstormManager>.getInstance().serialize(root, "thunderstorm-manager");
                Singleton<ManufactureLimits>.getInstance().serialize(root, "manufacture-limits");
                Singleton<ChallengeManager>.getInstance().serialize(root, "challenge-manager");
                Construction.serializeAll(root, "constructions");
                Character.serializeAll(root, "characters");
                Resource.serializeAll(root, "resources");
                Ship.serializeAll(root, "ships");
                Interaction.serializeAll(root, "interactions");
                var doc = (XmlDocument)fDocument.GetValue(null);
                return doc.OuterXml;
            }
            finally
            {
                fDocument.SetValue(null, null);
                fPath.SetValue(null, null);
            }
        }

        public static void ReadMeta(string xml, out int planetIndex, out string challengeId)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var root = doc.DocumentElement;
            planetIndex = int.Parse(root["planet"]["planet-index"].GetAttribute("value"));
            challengeId = root["challenge-manager"]?["challenge-id"]?.GetAttribute("value");
            if (string.IsNullOrEmpty(challengeId)) challengeId = null;
        }

        // Starts loading the snapshot as a normal saved game. Returns null or an error.
        public static string BeginLoad(string xml)
        {
            ReadMeta(xml, out int planetIndex, out string challengeId);
            Challenge challenge = null;
            if (challengeId != null)
            {
                try
                {
                    challenge = new Challenge();
                    challenge.loadById(challengeId);
                }
                catch (Exception e)
                {
                    return "The host plays challenge '" + challengeId + "', which this game can't load (" + e.Message + "). Subscribe to the same challenge first.";
                }
            }
            sPendingXml = xml;
            var gm = GameAccess.Manager;
            if (gm == null) return "game not ready";
            gm.setGameStateGameLoad(MagicPath, planetIndex, challenge);
            Log.Info($"Snapshot load started: planet {planetIndex}, challenge {challengeId ?? "none"}, {xml.Length / 1024} KB");
            return null;
        }

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Serialization), nameof(Serialization.beginLoad)),
                prefix: new HarmonyMethod(typeof(Snapshot), nameof(BeginLoadPrefix)));
            // The load menu and "continue" read metadata/screenshots from files: never with our path.
        }

        private static bool BeginLoadPrefix(string path, ref XmlNode __result)
        {
            if (path != MagicPath) return true;
            if (sPendingXml == null)
            {
                Log.Error("snapshot path requested with no snapshot");
                __result = null;
                return false;
            }
            var doc = new XmlDocument();
            doc.LoadXml(sPendingXml);
            fPath.SetValue(null, path);
            fIds.SetValue(null, new HashSet<int>());
            fDocument.SetValue(null, doc);
            __result = doc.DocumentElement;
            sPendingXml = null;
            return false;
        }

        public static byte[] ToBytes(string xml) => Encoding.UTF8.GetBytes(xml);
        public static string FromBytes(byte[] b) => Encoding.UTF8.GetString(b);
    }
}
