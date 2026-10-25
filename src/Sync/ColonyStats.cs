using System.Reflection;
using Module = Planetbase.Module;
using HarmonyLib;
using Planetbase;

namespace PlanetbaseCoop.Sync
{
    // Colony-wide numbers the game only refreshes in its simulation tick (Colony.tick), which
    // joiners don't run: colonist count (top-left panel), welfare, prestige, interception chance.
    // Without this the joiner's panel shows the placeholder int.MaxValue ("2147483647", which wraps
    // onto several lines). All of them are computed from the joiner's own mirror, which matches
    // the host's; the colony clock comes from the host (Globals).
    public static class ColonyStats
    {
        private static readonly FieldInfo fColonistCount = AccessTools.Field(typeof(Colony), "mColonistCount");
        private static readonly FieldInfo fInterception = AccessTools.Field(typeof(Colony), "mDisasterInterceptionChance");
        private static readonly FieldInfo fGameTime = AccessTools.Field(typeof(Colony), "mGameTime");
        private static readonly MethodInfo mWelfare = AccessTools.Method(typeof(Colony), "calculateWelfare");
        private static readonly MethodInfo mPrestige = AccessTools.Method(typeof(Colony), "calculatePrestige");
        private static readonly MethodInfo mStormIntensity = AccessTools.Method(typeof(ThunderstormManager), "updateStormIntensity");
        private static float sTimer = 10f;

        public static void Init()
        {
            if (fColonistCount == null || fInterception == null || fGameTime == null || mWelfare == null || mPrestige == null || mStormIntensity == null)
                throw new System.MissingMemberException("Colony stats fields");
        }

        public static double GameTime => (double)fGameTime.GetValue(Singleton<Colony>.getInstance());

        public static void SetGameTime(double t) => fGameTime.SetValue(Singleton<Colony>.getInstance(), t);

        public static void JoinerTick(float dt)
        {
            sTimer += dt;
            if (sTimer < 1f) return;
            sTimer = 0f;
            Refresh();
        }

        public static void Refresh()
        {
            var colony = Singleton<Colony>.getInstance();
            fColonistCount.SetValue(colony, Character.getCountOfType<Colonist>());
            fInterception.SetValue(colony, Module.calculateDisasterInterceptionChance());
            mWelfare.Invoke(colony, null);
            mPrestige.Invoke(colony, null);
            mStormIntensity.Invoke(Singleton<ThunderstormManager>.getInstance(), null);   // follows the colony clock
        }
    }
}
