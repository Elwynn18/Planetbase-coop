using System;
using BepInEx;
using HarmonyLib;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "planetbase.coop";
        public const string Name = "Planetbase Co-op";
        public const string Version = "0.1.0";

        public static Plugin Instance;
        public static Harmony Harmony;

        private void Awake()
        {
            Instance = this;
            // GameManager's constructor destroys every root GameObject not tagged "RootObject",
            // which would take the BepInEx manager (and this plugin's Update) with it.
            gameObject.tag = "RootObject";
            DontDestroyOnLoad(gameObject);
            Log.Init(Logger);
            Log.Info($"{Name} {Version} loading. Unity {Application.unityVersion}, test mode: {TestEnv.Mode ?? "off"}");
            Harmony = new Harmony(Guid);
            try
            {
                if (TestEnv.IsTest)
                {
                    SteamBlock.Apply(Harmony);
                    TestSetup.Apply(Harmony);
                }
                Log.Info("Plugin ready");
                if (TestEnv.Mode == "boot") TestEnv.WriteStatus("BOOT: plugin loaded");
            }
            catch (Exception e)
            {
                Log.Error("Setup failed: " + e);
                TestEnv.Fail("setup: " + e.Message);
            }
        }

        private float mBootTimer;
        private bool mBootDone;

        private void Update()
        {
            if (!TestEnv.IsTest) return;
            TestSetup.Update();
            if (TestEnv.Mode == "boot")
            {
                mBootTimer += Time.unscaledDeltaTime;
                var state = GameAccess.CurrentState();
                if (!mBootDone && state != null && state.isTitleState() && mBootTimer > 3f)
                {
                    mBootDone = true;
                    TestEnv.WriteStatus($"BOOT: title screen reached, state={state.GetType().Name}, steamInitBlocked={SteamBlock.InitBlocked}, screen={Screen.width}x{Screen.height} fullscreen={Screen.fullScreen}");
                    TestEnv.WriteStatus("RESULT: PASS boot");
                    TestEnv.Quit();
                }
            }
        }
    }
}
