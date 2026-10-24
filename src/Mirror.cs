using System;
using System.Reflection;
using Module = Planetbase.Module;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Sync;

namespace PlanetbaseCoop
{
    // On a joiner the game's simulation never runs: GameStateGame.fixedUpdate is replaced by a
    // step that only renders, runs the GUI and applies what the host sends.
    public static class Mirror
    {
        public static bool Active;   // joiner, snapshot loaded

        private static readonly FieldInfo fGameGui = AccessTools.Field(typeof(GameStateGame), "mGameGui");
        private static readonly FieldInfo fResourcePanel = AccessTools.Field(typeof(GameStateGame), "mGuiResourcePanel");
        private static readonly FieldInfo fGeneralPanel = AccessTools.Field(typeof(GameStateGame), "mGeneralPanel");
        private static readonly FieldInfo fAlertRenderer = AccessTools.Field(typeof(GameStateGame), "mGuiAlertRenderer");
        private static readonly FieldInfo fNextId = AccessTools.Field(typeof(IdGenerator), "mNextId");
        private static readonly FieldInfo fGmState = AccessTools.Field(typeof(GameManager), "mState");

        public const int JoinerIdBase = 1000000000;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameStateGame), nameof(GameStateGame.fixedUpdate)),
                prefix: new HarmonyMethod(typeof(Mirror), nameof(FixedUpdatePrefix)));
            h.Patch(AccessTools.Method(typeof(GameStateGame), nameof(GameStateGame.saveGame)),
                prefix: new HarmonyMethod(typeof(Mirror), nameof(SavePrefix)));
            h.Patch(AccessTools.Method(typeof(GameStateGame), "updateAutoSave"),
                prefix: new HarmonyMethod(typeof(Mirror), nameof(SkipOnJoiner)));
        }

        private static bool SkipOnJoiner() => !Active;

        private static bool SavePrefix(GameStateGame __instance)
        {
            if (!Active) return true;
            Ui.Toast("Only the host can save. The host's save keeps everyone's colony.");
            return false;
        }

        private static bool FixedUpdatePrefix(GameStateGame __instance, float timeStep, int frameIndex)
        {
            if (!Active) return true;
            try
            {
                Singleton<MusicManager>.getInstance().update(timeStep);
                Commands.ApplyingRemote = true;
                try { WorldApply.FixedTick(timeStep); }
                finally { Commands.ApplyingRemote = false; }
                Singleton<TerrainGenerator>.getInstance().update();
                ((GameGui)fGameGui.GetValue(__instance))?.update(frameIndex);
                ((GuiResourcePanel)fResourcePanel.GetValue(__instance))?.update(timeStep);
                ((GuiGeneralPanel)fGeneralPanel.GetValue(__instance))?.update(timeStep);
                ((GuiAlertRenderer)fAlertRenderer.GetValue(__instance))?.update(Singleton<SecurityManager>.getInstance().getAlertState(), timeStep);
            }
            catch (Exception e)
            {
                Log.Error("mirror fixedUpdate: " + e);
            }
            return false;
        }

        // Called once the joiner's GameStateGame finished loading the snapshot.
        public static void Begin()
        {
            Active = true;
            // previews (module/component placement) take ids on the joiner; keep them away from the host's
            fNextId.SetValue(Singleton<IdGenerator>.getInstance(), JoinerIdBase);
            Log.Info("Mirror active: joiner simulation frozen");
        }

        public static void End()
        {
            Active = false;
        }

        // GameManager waits for a key press after loading (State.Waiting). Skip it for joiners
        // and in tests.
        public static bool SkipLoadWait()
        {
            var gm = GameAccess.Manager;
            if (gm == null) return false;
            var st = fGmState.GetValue(gm);
            if ((int)st != (int)GameManager.State.Waiting) return false;
            var game = gm.getGameState();
            game.onStartUpdating();
            fGmState.SetValue(gm, GameManager.State.Updating);
            return true;
        }

        public static bool IsUpdating()
        {
            var gm = GameAccess.Manager;
            return gm != null && (int)fGmState.GetValue(gm) == (int)GameManager.State.Updating;
        }
    }
}
