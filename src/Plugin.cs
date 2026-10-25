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
        public const string Version = "0.2.0";

        public static Plugin Instance;
        public static Harmony Harmony;
        private static bool sReady;

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
                Snapshot.Patch(Harmony);
                Mirror.Patch(Harmony);
                Commands.Patch(Harmony);
                Sync.Messages.Patch(Harmony);
                Sync.Hazards.Patch(Harmony);
                Sync.Hazards.Init();
                Sync.ColonyStats.Init();
                Sync.TradeShare.Patch(Harmony);
                ErrorGuard.Patch(Harmony);
                TestBot.Init();
                sReady = true;
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
            if (!sReady) return;
            float dt = Time.unscaledDeltaTime;
            try
            {
                Ui.Update();
                SteamTransport.Update();
                Session.Update(dt);
                if (TestEnv.IsTest)
                {
                    TestSetup.Update();
                    TestBot.Update(dt);
                    BootCheck(dt);
                }
            }
            catch (Exception e)
            {
                Log.Error("Update: " + e);
            }
        }

        private void OnGUI()
        {
            if (!sReady) return;
            try { Ui.OnGUI(); Sync.Ghosts.OnGUI(); }
            catch (Exception e) { Log.Error("OnGUI: " + e); }
        }

        private void OnApplicationQuit()
        {
            Session.Stop("game closed");
        }

        private void BootCheck(float dt)
        {
            if (TestEnv.Mode != "boot" || mBootDone) return;
            mBootTimer += dt;
            var state = GameAccess.CurrentState();
            if (state != null && state.isTitleState() && mBootTimer > 3f)
            {
                mBootDone = true;
                TestEnv.WriteStatus($"BOOT: title screen reached, state={state.GetType().Name}, steamInitBlocked={SteamBlock.InitBlocked}, screen={Screen.width}x{Screen.height} fullscreen={Screen.fullScreen}");
                TestEnv.WriteStatus("BOOT: versions " + VersionInfo.Local);
                TestEnv.WriteStatus("RESULT: PASS boot");
                TestEnv.Quit();
            }
        }
    }
}
