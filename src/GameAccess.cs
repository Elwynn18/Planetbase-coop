using System.Reflection;
using HarmonyLib;
using Planetbase;

namespace PlanetbaseCoop
{
    // Side-effect-free access to game singletons. GameManager.getInstance() constructs the
    // manager when it does not exist yet (and that constructor destroys scene objects), so
    // never call it before the game does.
    public static class GameAccess
    {
        private static readonly FieldInfo sGameManagerInstance = AccessTools.Field(typeof(GameManager), "mInstance");

        public static GameManager Manager => sGameManagerInstance.GetValue(null) as GameManager;

        public static GameState CurrentState() => Manager?.getGameState();

        public static GameStateGame Game() => CurrentState() as GameStateGame;
    }
}
