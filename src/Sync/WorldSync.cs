using System;
using System.Collections.Generic;
using System.IO;
using Planetbase;
using PlanetbaseCoop.Net;
using UnityEngine;

namespace PlanetbaseCoop.Sync
{
    // Host side: detects what changed in the real simulation and sends it.
    public static class WorldSync
    {
        private static readonly Dictionary<Kind, HashSet<int>> sPublished = new Dictionary<Kind, HashSet<int>>();
        private static readonly Dictionary<int, object> sScratch = new Dictionary<int, object>();
        private static readonly Dictionary<int, CharRec> sCharSent = new Dictionary<int, CharRec>();
        private static readonly Dictionary<int, BuildRec> sBuildSent = new Dictionary<int, BuildRec>();
        private static readonly Dictionary<int, ResRec> sResSent = new Dictionary<int, ResRec>();
        private static readonly Dictionary<int, ShipRec> sShipSent = new Dictionary<int, ShipRec>();
        private static float sLifeTimer, sCharTimer, sBuildTimer, sResTimer, sKeyTimer, sGlobalTimer;
        public static int CreatesSent, DestroysSent;

        public static void Reset()
        {
            sPublished.Clear();
            sCharSent.Clear();
            sBuildSent.Clear();
            sResSent.Clear();
            sShipSent.Clear();
            CreatesSent = DestroysSent = 0;
        }

        // Baseline = what the snapshot contains, so the first diff only sends real changes.
        public static void Baseline()
        {
            foreach (var k in Registry.All)
            {
                Registry.Collect(k, sScratch);
                sPublished[k] = new HashSet<int>(sScratch.Keys);
            }
        }

        public static void Tick(float dt, ITransport t, Func<int, bool> isReady, IEnumerable<int> readyPeers)
        {
            if (sPublished.Count == 0) Baseline();
            sLifeTimer += dt; sCharTimer += dt; sBuildTimer += dt; sResTimer += dt; sKeyTimer += dt; sGlobalTimer += dt;
            bool key = sKeyTimer >= 3f;
            if (key) sKeyTimer = 0f;
            if (sLifeTimer >= 0.25f) { sLifeTimer = 0f; Lifecycle(t, readyPeers); }
            if (sCharTimer >= 0.1f || key) { sCharTimer = 0f; SendChars(t, readyPeers, key); SendShips(t, readyPeers, key); }
            if (sBuildTimer >= 0.5f || key) { sBuildTimer = 0f; SendBuilds(t, readyPeers, key); }
            if (sResTimer >= 0.2f || key) { sResTimer = 0f; SendResources(t, readyPeers, key); }
            if (sGlobalTimer >= 0.5f) { sGlobalTimer = 0f; Send(t, readyPeers, Globals.Build()); }
        }

        private static void Send(ITransport t, IEnumerable<int> peers, byte[] msg)
        {
            foreach (var p in peers) t.Send(p, msg);
        }

        // ---------------------------------------------------------------- lifecycle
        private static void Lifecycle(ITransport t, IEnumerable<int> peers)
        {
            var created = new List<byte[]>();
            var destroyed = new List<byte[]>();
            foreach (var k in Registry.All)
            {
                Registry.Collect(k, sScratch);
                if (!sPublished.TryGetValue(k, out var pub)) sPublished[k] = pub = new HashSet<int>();
                foreach (var kv in sScratch)
                {
                    if (pub.Contains(kv.Key)) continue;
                    var msg = CreatedMsg(k, kv.Key, kv.Value);
                    if (msg != null) { created.Add(msg); pub.Add(kv.Key); }
                }
                var gone = new List<int>();
                foreach (var id in pub) if (!sScratch.ContainsKey(id)) gone.Add(id);
                foreach (var id in gone)
                {
                    pub.Remove(id);
                    destroyed.Add(DestroyedMsg(k, id));
                }
            }
            // destroy children first (reverse kind order), then create parents first
            destroyed.Reverse();
            foreach (var m in destroyed) { Send(t, peers, m); DestroysSent++; }
            foreach (var m in created) { Send(t, peers, m); CreatesSent++; }
        }

        public static byte[] CreatedMsg(Kind k, int id, object obj)
        {
            try
            {
                string xml = Registry.ToXml(k, obj, out int parent);
                return Wire.Build(Msg.Created, w => { w.Write((byte)k); w.Write(id); w.Write(parent); w.Write(xml); });
            }
            catch (Exception e)
            {
                Log.Warn($"could not serialize {k} {id}: {e.Message}");
                return null;
            }
        }

        public static byte[] DestroyedMsg(Kind k, int id) => Wire.Build(Msg.Destroyed, w => { w.Write((byte)k); w.Write(id); });

        // Joiner census: ids it has per kind. Answer with what it lacks and what it has too much of.
        public static void OnCensus(ITransport t, int peer, BinaryReader r)
        {
            int kinds = r.ReadByte();
            int sentC = 0, sentD = 0;
            var destroys = new List<byte[]>();
            var creates = new List<byte[]>();
            var have = new Dictionary<Kind, HashSet<int>>();
            for (int i = 0; i < kinds; i++)
            {
                var k = (Kind)r.ReadByte();
                int n = r.ReadInt32();
                var set = new HashSet<int>();
                for (int j = 0; j < n; j++) set.Add(r.ReadInt32());
                have[k] = set;
            }
            foreach (var k in Registry.All)
            {
                if (!have.TryGetValue(k, out var set)) continue;
                Registry.Collect(k, sScratch);
                foreach (var kv in sScratch)
                {
                    if (set.Contains(kv.Key)) continue;
                    var m = CreatedMsg(k, kv.Key, kv.Value);
                    if (m != null) { creates.Add(m); sentC++; }
                }
                foreach (var id in set)
                {
                    if (!sScratch.ContainsKey(id)) { destroys.Add(DestroyedMsg(k, id)); sentD++; }
                }
            }
            destroys.Reverse();
            foreach (var m in destroys) t.Send(peer, m);
            foreach (var m in creates) t.Send(peer, m);
            if (sentC + sentD > 0) Log.Info($"census from peer {peer}: sent {sentC} creates, {sentD} destroys");
            Census.Repairs += sentC + sentD;
        }

        // ---------------------------------------------------------------- states
        private static void SendChars(ITransport t, IEnumerable<int> peers, bool all)
        {
            Registry.Collect(Kind.Character, sScratch);
            var recs = new List<CharRec>();
            foreach (var kv in sScratch)
            {
                var rec = CharRec.From((Character)kv.Value);
                if (!all && sCharSent.TryGetValue(kv.Key, out var last) && last.SameAs(rec)) continue;
                sCharSent[kv.Key] = rec;
                recs.Add(rec);
            }
            if (recs.Count == 0) return;
            Send(t, peers, Wire.Build(Msg.States, w =>
            {
                w.Write((byte)StateKind.Character);
                w.Write(recs.Count);
                foreach (var rec in recs) rec.Write(w);
            }));
        }

        private static void SendBuilds(ITransport t, IEnumerable<int> peers, bool all)
        {
            var recs = new List<BuildRec>();
            foreach (var k in new[] { Kind.Module, Kind.Connection, Kind.Component })
            {
                Registry.Collect(k, sScratch);
                foreach (var kv in sScratch)
                {
                    var rec = BuildRec.From((Buildable)kv.Value);
                    if (!all && sBuildSent.TryGetValue(kv.Key, out var last) && last.SameAs(rec)) continue;
                    sBuildSent[kv.Key] = rec;
                    recs.Add(rec);
                }
            }
            if (recs.Count == 0) return;
            Send(t, peers, Wire.Build(Msg.States, w =>
            {
                w.Write((byte)StateKind.Buildable);
                w.Write(recs.Count);
                foreach (var rec in recs) rec.Write(w);
            }));
        }

        private static void SendShips(ITransport t, IEnumerable<int> peers, bool all)
        {
            Registry.Collect(Kind.Ship, sScratch);
            var recs = new List<ShipRec>();
            foreach (var kv in sScratch)
            {
                var rec = ShipRec.From((Ship)kv.Value);
                if (!all && sShipSent.TryGetValue(kv.Key, out var last) && last.SameAs(rec)) continue;
                sShipSent[kv.Key] = rec;
                recs.Add(rec);
            }
            if (recs.Count == 0) return;
            Send(t, peers, Wire.Build(Msg.States, w =>
            {
                w.Write((byte)StateKind.Ship);
                w.Write(recs.Count);
                foreach (var rec in recs) rec.Write(w);
            }));
        }

        private static void SendResources(ITransport t, IEnumerable<int> peers, bool all)
        {
            Registry.Collect(Kind.Resource, sScratch);
            var recs = new List<ResRec>();
            foreach (var kv in sScratch)
            {
                var rec = ResRec.From((Resource)kv.Value);
                if (!all && sResSent.TryGetValue(kv.Key, out var last) && last.SameAs(rec)) continue;
                sResSent[kv.Key] = rec;
                recs.Add(rec);
            }
            if (recs.Count == 0) return;
            Send(t, peers, Wire.Build(Msg.States, w =>
            {
                w.Write((byte)StateKind.Resource);
                w.Write(recs.Count);
                foreach (var rec in recs) rec.Write(w);
            }));
        }
    }

    public enum StateKind : byte { Character = 1, Buildable = 2, Resource = 3, Ship = 4 }
}
