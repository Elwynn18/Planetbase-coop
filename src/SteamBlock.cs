using HarmonyLib;
using Steamworks;

namespace PlanetbaseCoop
{
    // Test copies must never talk to the owner's Steam client: no "Playing Planetbase",
    // no cloud writes, no relaunch of the real install through Steam.
    // The launcher also leaves steamclient.dll out of the test prefix; this is the second lock.
    public static class SteamBlock
    {
        public static bool InitBlocked;

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(SteamAPI), nameof(SteamAPI.RestartAppIfNecessary), new[] { typeof(AppId_t) }),
                prefix: new HarmonyMethod(typeof(SteamBlock), nameof(NoRestart)));
            harmony.Patch(AccessTools.Method(typeof(SteamAPI), nameof(SteamAPI.Init), new System.Type[0]),
                prefix: new HarmonyMethod(typeof(SteamBlock), nameof(NoInit)));
            Log.Info("Test mode: Steam API blocked");
        }

        private static bool NoRestart(ref bool __result)
        {
            Log.Info("Test mode: RestartAppIfNecessary -> false");
            __result = false;
            return false;
        }

        private static bool NoInit(ref bool __result)
        {
            InitBlocked = true;
            Log.Info("Test mode: SteamAPI.Init -> false");
            __result = false;
            return false;
        }
    }
}
