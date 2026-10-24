using HarmonyLib;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    // Test copies run in a small window, muted, and keep running unfocused.
    // The game otherwise forces full screen at the desktop resolution on start
    // (GameStateLogo.load -> GameStateSettings.trySetResolution -> Screen.SetResolution(w, h, true)).
    public static class TestSetup
    {
        public static int Width = 800;
        public static int Height = 600;

        public static void Apply(Harmony harmony)
        {
            int.TryParse(TestEnv.Get("COOP_WIDTH") ?? "", out int w);
            int.TryParse(TestEnv.Get("COOP_HEIGHT") ?? "", out int h);
            if (w > 0) Width = w;
            if (h > 0) Height = h;
            harmony.Patch(AccessTools.Method(typeof(GameStateSettings), nameof(GameStateSettings.trySetResolution)),
                prefix: new HarmonyMethod(typeof(TestSetup), nameof(Windowed)));
            Screen.SetResolution(Width, Height, FullScreenMode.Windowed);
            Application.runInBackground = true;
            AudioListener.volume = 0f;
            Log.Info($"Test mode: windowed {Width}x{Height}, muted, runInBackground");
        }

        private static bool Windowed(ref bool __result)
        {
            Screen.SetResolution(Width, Height, FullScreenMode.Windowed);
            __result = true;
            return false;
        }

        public static void Update()
        {
            if (AudioListener.volume != 0f) AudioListener.volume = 0f;
        }
    }
}
