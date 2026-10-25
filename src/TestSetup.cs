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
            if (TestEnv.Get("COOP_LOWGFX") == "1")
            {
                // cheapest settings: four copies share one PC in the test lab
                QualitySettings.SetQualityLevel(0, true);
                QualitySettings.vSyncCount = 0;
                QualitySettings.antiAliasing = 0;
                QualitySettings.shadows = ShadowQuality.Disable;
                Application.targetFrameRate = 30;
                harmony.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.initQualitySettings)),
                    postfix: new HarmonyMethod(typeof(TestSetup), nameof(LowQuality)));
            }
            Log.Info($"Test mode: windowed {Width}x{Height}, muted, runInBackground");
        }

        private static bool Windowed(ref bool __result)
        {
            Screen.SetResolution(Width, Height, FullScreenMode.Windowed);
            __result = true;
            return false;
        }

        private static void LowQuality()
        {
            QualitySettings.SetQualityLevel(0, true);
            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 0;
            QualitySettings.shadows = ShadowQuality.Disable;
        }

        private static float sFpsTimer;
        private static int sFpsFrames;

        private static readonly System.Collections.Generic.HashSet<int> sStripped = new System.Collections.Generic.HashSet<int>();

        // COOP_NORENDER=1 (optional, e.g. software GL): cameras draw nothing and their image effects
        // are off. The game's logic, including code that uses Camera.main, still runs.
        private static void StripCameras()
        {
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null) continue;
                cam.cullingMask = 0;
                cam.clearFlags = CameraClearFlags.SolidColor;
                if (sStripped.Add(cam.GetInstanceID()) || Time.frameCount % 120 == 0)
                {
                    foreach (var b in cam.GetComponents<Behaviour>())
                        if (b != cam && b.enabled) b.enabled = false;
                }
            }
        }

        public static void Update()
        {
            if (AudioListener.volume != 0f) AudioListener.volume = 0f;
            if (TestEnv.Get("COOP_NORENDER") == "1") StripCameras();
            sFpsFrames++;
            sFpsTimer += Time.unscaledDeltaTime;
            if (sFpsTimer >= 10f)
            {
                Log.Info($"fps {sFpsFrames / sFpsTimer:F1} state {GameAccess.CurrentState()?.GetType().Name}");
                sFpsTimer = 0f; sFpsFrames = 0;
            }
        }
    }
}
