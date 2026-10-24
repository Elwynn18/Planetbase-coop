using System.IO;
using System.Reflection;
using Module = Planetbase.Module;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Net;
using UnityEngine;

namespace PlanetbaseCoop.Sync
{
    // Speed, pause, alert level, time of day and weather. Host -> joiners, twice a second.
    public static class Globals
    {
        private static readonly FieldInfo fScaleIndex = AccessTools.Field(typeof(TimeManager), "mScaleIndex");
        private static readonly FieldInfo fPaused = AccessTools.Field(typeof(TimeManager), "mPaused");
        private static readonly MethodInfo mScaleChanged = AccessTools.Method(typeof(TimeManager), "onTimeScaleChanged");
        private static readonly FieldInfo fWindDir = AccessTools.Field(typeof(EnvironmentManager), "mWindDirection");
        private static readonly FieldInfo fWindChange = AccessTools.Field(typeof(EnvironmentManager), "mTimeToWindChange");

        public static int LastScaleIndex = -1;
        public static bool LastPaused;

        public static byte[] Build()
        {
            var tm = Singleton<TimeManager>.getInstance();
            var env = Singleton<EnvironmentManager>.getInstance();
            return Wire.Build(Msg.Globals, w =>
            {
                w.Write((int)fScaleIndex.GetValue(tm));
                w.Write((bool)fPaused.GetValue(tm));
                w.Write((int)Singleton<SecurityManager>.getInstance().getAlertState());
                w.Write(env.getTimeIndicator().getValue());
                w.Write(env.getWindIndicator().getValue());
                w.WriteVec((Vector3)fWindDir.GetValue(env));
                w.Write(env.getCloudCoverIndicator().getValue());
            });
        }

        public static void Apply(BinaryReader r)
        {
            int scale = r.ReadInt32();
            bool paused = r.ReadBoolean();
            var alert = (AlertState)r.ReadInt32();
            float tod = r.ReadSingle(), wind = r.ReadSingle();
            Vector3 windDir = r.ReadVec();
            float cloud = r.ReadSingle();

            var tm = Singleton<TimeManager>.getInstance();
            if ((int)fScaleIndex.GetValue(tm) != scale || (bool)fPaused.GetValue(tm) != paused)
            {
                fScaleIndex.SetValue(tm, scale);
                fPaused.SetValue(tm, paused);
                mScaleChanged.Invoke(tm, null);   // animation speeds follow
            }
            LastScaleIndex = scale;
            LastPaused = paused;
            var sec = Singleton<SecurityManager>.getInstance();
            if (sec.getAlertState() != alert) sec.setAlertState(alert);
            var env = Singleton<EnvironmentManager>.getInstance();
            env.setTimeOfDay(tod);
            env.getWindIndicator().setValue(wind);
            fWindDir.SetValue(env, windDir);
            fWindChange.SetValue(env, 1e9f);
            env.getCloudCoverIndicator().setValue(cloud);
        }
    }

    // Joiner -> host every few seconds: every id it has, per kind. The host answers with what is
    // missing or extra (WorldSync.OnCensus). This repairs anything a lost or failed message left wrong.
    public static class Census
    {
        public static int Repairs;   // host: objects re-sent or removed because of a census
        private static readonly System.Collections.Generic.Dictionary<int, object> sScratch = new System.Collections.Generic.Dictionary<int, object>();

        public static byte[] Build()
        {
            return Wire.Build(Msg.Census, w =>
            {
                w.Write((byte)Registry.All.Length);
                foreach (var k in Registry.All)
                {
                    Registry.Collect(k, sScratch);
                    w.Write((byte)k);
                    w.Write(sScratch.Count);
                    foreach (var id in sScratch.Keys) w.Write(id);
                }
            });
        }
    }
}
