using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Module = Planetbase.Module;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Net;
using UnityEngine;

namespace PlanetbaseCoop.Sync
{
    // Meteors, lightning and storms come from the host's simulation, which joiners don't run.
    //
    // Meteors and lightning: MeteorManager/ThunderstormManager spawn each one from the first seed
    // of a seed list. The host sends (seed, target, name); a joiner puts that seed first and calls
    // the same spawn, so the rock flies the same path. What an impact does to the world (damage,
    // injuries, the notification) happens on the host only and reaches joiners through the normal
    // sync; on a joiner the impact is just the explosion and the crater.
    //
    // Storms (sandstorm, blizzard, solar flare): the host sends each one's state once a second; a
    // joiner starts/ends its own copy to match and runs only its visual update.
    public static class Hazards
    {
        private static readonly FieldInfo fMeteorSeeds = AccessTools.Field(typeof(MeteorManager), "mSeeds");
        private static readonly FieldInfo fMeteors = AccessTools.Field(typeof(MeteorManager), "mMeteors");
        private static readonly FieldInfo fThunderSeeds = AccessTools.Field(typeof(ThunderstormManager), "mSeeds");
        private static readonly FieldInfo fThunders = AccessTools.Field(typeof(ThunderstormManager), "mThunders");

        public static int MeteorsSent, MeteorsShown, ThundersSent, ThundersShown, RemovesSent, RemovesApplied, StormStarts, StormEnds;
        private static bool sReplaying;

        private enum HazardKind : byte { Meteor = 1, MeteorRemoved = 2, Thunder = 3 }

        public static void Patch(Harmony h)
        {
            var meteorSpawn = AccessTools.Method(typeof(MeteorManager), nameof(MeteorManager.spawnMeteor));
            h.Patch(meteorSpawn, prefix: new HarmonyMethod(typeof(Hazards), nameof(MeteorSpawnPrefix)),
                postfix: new HarmonyMethod(typeof(Hazards), nameof(MeteorSpawnPostfix)));
            h.Patch(AccessTools.Method(typeof(MeteorManager), nameof(MeteorManager.remove)),
                prefix: new HarmonyMethod(typeof(Hazards), nameof(MeteorRemovePrefix)));
            h.Patch(AccessTools.Method(typeof(ThunderstormManager), nameof(ThunderstormManager.spawnThunder)),
                prefix: new HarmonyMethod(typeof(Hazards), nameof(ThunderSpawnPrefix)),
                postfix: new HarmonyMethod(typeof(Hazards), nameof(ThunderSpawnPostfix)));

            // impacts: world changes on the host only
            var skip = new HarmonyMethod(typeof(Hazards), nameof(SkipOnJoiner));
            h.Patch(AccessTools.Method(typeof(Construction), nameof(Construction.onImpact)), prefix: skip);
            h.Patch(AccessTools.Method(typeof(Module), nameof(Module.onImpact)), prefix: skip);
            h.Patch(AccessTools.Method(typeof(Connection), nameof(Connection.onImpact)), prefix: skip);
            h.Patch(AccessTools.Method(typeof(Character), nameof(Character.onImpact), new[] { typeof(Construction), typeof(ImpactType) }), prefix: skip);
            h.Patch(AccessTools.Method(typeof(Character), nameof(Character.onImpact), new[] { typeof(ImpactType), typeof(Vector3) }), prefix: skip);
            // the anti-meteor laser runs in the host's simulation; a joiner's copy must not lock on
            h.Patch(AccessTools.Method(typeof(Module), nameof(Module.setTargetMeteor)), prefix: skip);
        }

        private static bool SkipOnJoiner() => !Mirror.Active;

        // ---------------------------------------------------------------- host: meteors and lightning
        private static void MeteorSpawnPrefix(MeteorManager __instance, out int __state)
        {
            var seeds = (List<int>)fMeteorSeeds.GetValue(__instance);
            __state = seeds != null && seeds.Count > 0 ? seeds[0] : 0;
        }

        private static void MeteorSpawnPostfix(MeteorManager __instance, Vector3 target, int __state)
        {
            if (!Session.IsHost || sReplaying || Session.Transport == null) return;
            var list = (List<GameObject>)fMeteors.GetValue(__instance);
            string name = list != null && list.Count > 0 && list[list.Count - 1] != null ? list[list.Count - 1].name : "";
            Broadcast(Wire.Build(Msg.Hazard, w => { w.Write((byte)HazardKind.Meteor); w.Write(__state); w.WriteVec(target); w.Write(name); }));
            MeteorsSent++;
        }

        private static void MeteorRemovePrefix(GameObject meteor)
        {
            if (!Session.IsHost || Session.Transport == null || meteor == null) return;
            string name = meteor.name;
            Broadcast(Wire.Build(Msg.Hazard, w => { w.Write((byte)HazardKind.MeteorRemoved); w.Write(name); }));
            RemovesSent++;
        }

        private static void ThunderSpawnPrefix(ThunderstormManager __instance, out int __state)
        {
            var seeds = (List<int>)fThunderSeeds.GetValue(__instance);
            __state = seeds != null && seeds.Count > 0 ? seeds[0] : 0;
        }

        private static void ThunderSpawnPostfix(ThunderstormManager __instance, Vector3 target, int __state)
        {
            if (!Session.IsHost || sReplaying || Session.Transport == null) return;
            var list = (List<GameObject>)fThunders.GetValue(__instance);
            string name = list != null && list.Count > 0 && list[list.Count - 1] != null ? list[list.Count - 1].name : "";
            Broadcast(Wire.Build(Msg.Hazard, w => { w.Write((byte)HazardKind.Thunder); w.Write(__state); w.WriteVec(target); w.Write(name); }));
            ThundersSent++;
        }

        private static void Broadcast(byte[] msg)
        {
            foreach (var p in Session.ReadyPeers) Session.Transport.Send(p, msg);
        }

        // ---------------------------------------------------------------- joiner
        public static void OnHazard(BinaryReader r)
        {
            var kind = (HazardKind)r.ReadByte();
            switch (kind)
            {
                case HazardKind.Meteor:
                {
                    int seed = r.ReadInt32();
                    Vector3 target = r.ReadVec();
                    string name = r.ReadString();
                    var mm = Singleton<MeteorManager>.getInstance();
                    var seeds = (List<int>)fMeteorSeeds.GetValue(mm);
                    seeds.Insert(0, seed);   // spawnMeteor uses and removes the first seed
                    sReplaying = true;
                    try { mm.spawnMeteor(target); }
                    finally { sReplaying = false; }
                    var list = (List<GameObject>)fMeteors.GetValue(mm);
                    if (list.Count > 0 && list[list.Count - 1] != null && name != "") list[list.Count - 1].name = name;
                    MeteorsShown++;
                    break;
                }
                case HazardKind.MeteorRemoved:
                {
                    string name = r.ReadString();
                    var mm = Singleton<MeteorManager>.getInstance();
                    var list = (List<GameObject>)fMeteors.GetValue(mm);
                    var go = list.Find(g => g != null && g.name == name);
                    if (go != null) { mm.remove(go); RemovesApplied++; }   // e.g. shot down by the host's laser
                    break;
                }
                case HazardKind.Thunder:
                {
                    int seed = r.ReadInt32();
                    Vector3 target = r.ReadVec();
                    string name = r.ReadString();
                    var tm = Singleton<ThunderstormManager>.getInstance();
                    var seeds = (List<int>)fThunderSeeds.GetValue(tm);
                    seeds.Insert(0, seed);
                    sReplaying = true;
                    try { tm.spawnThunder(target); }
                    finally { sReplaying = false; }
                    var list = (List<GameObject>)fThunders.GetValue(tm);
                    if (list.Count > 0 && list[list.Count - 1] != null && name != "") list[list.Count - 1].name = name;
                    ThundersShown++;
                    break;
                }
            }
        }

        public static int MeteorCount() => ((List<GameObject>)fMeteors.GetValue(Singleton<MeteorManager>.getInstance()))?.Count ?? 0;

        public static GameObject FindMeteor(string name) =>
            ((List<GameObject>)fMeteors.GetValue(Singleton<MeteorManager>.getInstance()))?.Find(g => g != null && g.name == name);

        // ---------------------------------------------------------------- storms
        // One entry per disaster type: its fields differ only by name.
        private class Storm
        {
            public Func<DisasterManager, Disaster> Get;
            public FieldInfo InProgress, Time, Duration, TimeToNext, FirstUpdate;
            public MethodInfo OnStart, OnEnd;

            public Storm(Type t, string word, Func<DisasterManager, Disaster> get)
            {
                Get = get;
                InProgress = AccessTools.Field(t, "m" + word + "InProgress");
                Time = AccessTools.Field(t, "mTime");
                Duration = AccessTools.Field(t, "m" + word + "Time");
                TimeToNext = AccessTools.Field(t, "mTimeToNext" + word);
                FirstUpdate = AccessTools.Field(t, "mFirstUpdate");
                OnStart = AccessTools.Method(t, "onStart");
                OnEnd = AccessTools.Method(t, "onEnd");
                if (InProgress == null || Time == null || Duration == null || TimeToNext == null || OnStart == null || OnEnd == null)
                    throw new MissingFieldException("storm fields of " + t.Name);
            }
        }

        private static Storm[] sStorms;
        private static Storm[] Storms => sStorms ?? (sStorms = new[]
        {
            new Storm(typeof(Sandstorm), "Sandstorm", d => d.getSandstorm()),
            new Storm(typeof(Blizzard), "Blizzard", d => d.getBlizzard()),
            new Storm(typeof(SolarFlare), "SolarFlare", d => d.getSolarFlare()),
        });

        public static void Init() { _ = Storms; }   // fail at load time, not mid-game, if a field moved

        private static float sStormTimer;

        // host, once a second
        public static void HostTick(float dt, ITransport t, IEnumerable<int> peers)
        {
            sStormTimer += dt;
            if (sStormTimer < 1f) return;
            sStormTimer = 0f;
            var dm = Singleton<DisasterManager>.getInstance();
            var msg = Wire.Build(Msg.Disasters, w =>
            {
                w.Write((byte)Storms.Length);
                foreach (var s in Storms)
                {
                    var d = s.Get(dm);
                    w.Write((bool)s.InProgress.GetValue(d));
                    w.Write((float)s.Time.GetValue(d));
                    w.Write((float)s.Duration.GetValue(d));
                }
            });
            foreach (var p in peers) t.Send(p, msg);
        }

        // joiner: match the host's storms
        public static void OnDisasters(BinaryReader r)
        {
            int n = r.ReadByte();
            var dm = Singleton<DisasterManager>.getInstance();
            for (int i = 0; i < n; i++)
            {
                bool on = r.ReadBoolean();
                float time = r.ReadSingle(), duration = r.ReadSingle();
                if (i >= Storms.Length) continue;
                var s = Storms[i];
                var d = s.Get(dm);
                s.TimeToNext.SetValue(d, 1e9f);
                bool localOn = (bool)s.InProgress.GetValue(d);
                s.Duration.SetValue(d, duration);
                if (on && !localOn)
                {
                    s.InProgress.SetValue(d, true);
                    s.Time.SetValue(d, time);
                    s.FirstUpdate?.SetValue(d, false);
                    Quiet(() => s.OnStart.Invoke(d, null));   // particles, ambient sound
                    StormStarts++;
                    Log.Info($"{d.GetType().Name} started (host's storm, {time:F0}/{duration:F0} s)");
                }
                else if (!on && localOn)
                {
                    s.InProgress.SetValue(d, false);
                    Quiet(() => s.OnEnd.Invoke(d, null));
                    StormEnds++;
                    Log.Info($"{d.GetType().Name} ended (host's storm)");
                }
                else if (on && Mathf.Abs((float)s.Time.GetValue(d) - time) > 1f)
                {
                    s.Time.SetValue(d, time);
                }
            }
        }

        // joiner, every mirror frame: the storms' own update moves particles and fades intensity.
        // Their countdown to the next storm is parked, so a joiner never starts one by itself.
        public static void JoinerTick(float scaledDt)
        {
            if (scaledDt <= 0f) return;
            var dm = Singleton<DisasterManager>.getInstance();
            foreach (var s in Storms)
            {
                var d = s.Get(dm);
                s.TimeToNext.SetValue(d, 1e9f);
                Quiet(() => d.update(scaledDt));
            }
        }

        // the storm code posts "Sandstorm!" etc.; joiners get the host's copy of that notification
        private static void Quiet(Action a)
        {
            bool prev = Messages.SuppressLocal;
            Messages.SuppressLocal = true;
            try { a(); }
            finally { Messages.SuppressLocal = prev; }
        }

        public static string StormDigest()
        {
            var dm = Singleton<DisasterManager>.getInstance();
            var parts = new List<string>();
            foreach (var s in Storms)
            {
                var d = s.Get(dm);
                parts.Add(((bool)s.InProgress.GetValue(d) ? "1" : "0") + ":" + ((float)s.Time.GetValue(d)).ToString("F0"));
            }
            return string.Join(";", parts);
        }
    }
}
