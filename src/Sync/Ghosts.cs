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
    // Placement previews ("ghost" buildings). While a player places a module, the others see its
    // preview (type, size, place, green/red) with the player's name above it.
    //
    // A remote ghost is a Module made by the game's own Module.create, then taken out of every game
    // list (constructions, modules, categories, collider lookup) with its colliders off: the game
    // never sees it, so it can't block a placement, count as a link target, get selected or saved.
    public static class Ghosts
    {
        private static readonly FieldInfo fActiveModule = AccessTools.Field(typeof(GameStateGame), "mActiveModule");
        private static readonly FieldInfo fConstructions = AccessTools.Field(typeof(Construction), "mConstructions");
        private static readonly FieldInfo fConstructionDict = AccessTools.Field(typeof(Construction), "mConstructionDictionary");
        private static readonly FieldInfo fModules = AccessTools.Field(typeof(Module), "mModules");
        private static readonly FieldInfo fCategories = AccessTools.Field(typeof(Module), "mModuleCategories");
        private static readonly FieldInfo fObject = AccessTools.Field(typeof(Construction), "mObject");
        private static readonly Action<Construction, float, bool> dTopRender = AccessTools.MethodDelegate<Action<Construction, float, bool>>(AccessTools.Method(typeof(Construction), "updateTopRender"));

        public const int HostPlayer = 0;

        public class Preview
        {
            public bool Present;
            public string Type = "";
            public int Size;
            public Vector3 Pos;
            public Quaternion Rot = Quaternion.identity;
            public bool Valid;

            public bool SameAs(Preview o) =>
                o != null && o.Present == Present && (!Present || (o.Type == Type && o.Size == Size && o.Valid == Valid &&
                (o.Pos - Pos).sqrMagnitude < 0.0025f && Quaternion.Angle(o.Rot, Rot) < 0.5f));

            public void Write(BinaryWriter w)
            {
                w.Write(Present);
                if (!Present) return;
                w.Write(Type); w.Write(Size); w.WriteVec(Pos); w.WriteQuat(Rot); w.Write(Valid);
            }

            public static Preview Read(BinaryReader r)
            {
                var p = new Preview { Present = r.ReadBoolean() };
                if (p.Present) { p.Type = r.ReadString(); p.Size = r.ReadInt32(); p.Pos = r.ReadVec(); p.Rot = r.ReadQuat(); p.Valid = r.ReadBoolean(); }
                return p;
            }
        }

        private class Ghost
        {
            public int Player;
            public string Name = "";
            public Module Model;
            public string Type;
            public int Size;
            public float LastHeard;
        }

        private static readonly Dictionary<int, Ghost> sGhosts = new Dictionary<int, Ghost>();
        private static Preview sLastSent;
        private static float sSendTimer, sKeyTimer;
        private static GameStateGame sGame;   // ghosts belong to this game state
        public static int Sent, Shown;

        // ---------------------------------------------------------------- local preview -> others
        public static Preview ReadLocal()
        {
            var game = GameAccess.Game();
            var m = game == null ? null : fActiveModule.GetValue(game) as Module;
            // mActiveModule is also the module being edited (components); only a preview is Placing
            if (m == null || m.isDestroyed() || Registry.StateOf(m) != BuildableState.Placing) return new Preview();
            var go = (GameObject)fObject.GetValue(m);
            if (go == null) return new Preview();
            return new Preview
            {
                Present = true,
                Type = m.getModuleType().GetType().Name,
                Size = m.getSizeIndex(),
                Pos = go.transform.position,
                Rot = go.transform.rotation,
                Valid = m.isValidPosition(),
            };
        }

        // both sides, every frame while in a session
        public static void Tick(float dt)
        {
            var game = GameAccess.Game();
            if (game != sGame) { Clear(); sGame = game; }
            if (game == null) return;
            if (Session.IsClient && !Mirror.Active) return;

            sSendTimer += dt; sKeyTimer += dt;
            if (sSendTimer >= 0.1f)
            {
                sSendTimer = 0f;
                var p = ReadLocal();
                bool key = sKeyTimer >= 1f && p.Present;   // resend now and then: a lost message heals
                if (!p.SameAs(sLastSent) || key)
                {
                    if (key) sKeyTimer = 0f;
                    sLastSent = p;
                    Send(p);
                }
            }

            // a player who stopped sending (crash, lost link) loses their ghost after 3 s
            float now = Time.realtimeSinceStartup;
            var stale = new List<int>();
            foreach (var g in sGhosts.Values) if (g.Model != null && now - g.LastHeard > 3f) stale.Add(g.Player);
            foreach (var id in stale) Remove(id);

            foreach (var g in sGhosts.Values)
            {
                if (g.Model == null) continue;
                try { dTopRender(g.Model, dt, false); } catch { }
            }
        }

        private static void Send(Preview p)
        {
            if (TestEnv.Mut("no-ghosts")) return;
            Sent++;
            if (Session.IsHost)
            {
                var msg = Build(HostPlayer, Session.PlayerName, p);
                foreach (var peer in Session.ReadyPeers) Session.Transport.Send(peer, msg);
            }
            else Session.SendCommand(Build(Session.MyPlayerId, "", p));
        }

        private static byte[] Build(int player, string name, Preview p) =>
            Wire.Build(Msg.Ghost, w => { w.Write(player); w.Write(name ?? ""); p.Write(w); });

        // ---------------------------------------------------------------- others' previews
        // host: from a joiner; shown here and relayed to the other joiners under that joiner's id
        public static void OnHost(int peer, string peerName, BinaryReader r)
        {
            r.ReadInt32(); r.ReadString();   // the host sets id and name itself
            var p = Preview.Read(r);
            Show(peer, peerName, p);
            var msg = Build(peer, peerName, p);
            foreach (var other in Session.ReadyPeers) if (other != peer) Session.Transport.Send(other, msg);
        }

        public static void OnClient(BinaryReader r)
        {
            int player = r.ReadInt32();
            string name = r.ReadString();
            var p = Preview.Read(r);
            if (player == Session.MyPlayerId) return;
            Show(player, name, p);
        }

        // host: a joiner left
        public static void OnPeerLeft(int peer)
        {
            Remove(peer);
            if (Session.Transport == null) return;
            var msg = Build(peer, "", new Preview());
            foreach (var other in Session.ReadyPeers) Session.Transport.Send(other, msg);
        }

        private static void Show(int player, string name, Preview p)
        {
            if (GameAccess.Game() == null) return;
            if (!sGhosts.TryGetValue(player, out var g)) sGhosts[player] = g = new Ghost { Player = player };
            g.Name = name;
            g.LastHeard = Time.realtimeSinceStartup;
            if (!p.Present) { DestroyModel(g); return; }
            if (g.Model != null && (g.Type != p.Type || g.Size != p.Size)) DestroyModel(g);
            if (g.Model == null)
            {
                var type = TypeList<ModuleType, ModuleTypeList>.find(p.Type);
                if (type == null) return;
                bool prev = Commands.ApplyingRemote;
                Commands.ApplyingRemote = true;
                try { g.Model = Module.create(p.Pos, Mathf.Clamp(p.Size, type.getMinSize(), type.getMaxSize()), type); }
                finally { Commands.ApplyingRemote = prev; }
                Detach(g.Model);
                g.Type = p.Type;
                g.Size = p.Size;
                Shown++;
            }
            var go = (GameObject)fObject.GetValue(g.Model);
            go.transform.position = p.Pos;
            go.transform.rotation = p.Rot;
            if (g.Model.isValidPosition() != p.Valid) g.Model.setValidPosition(p.Valid);
        }

        // take a fresh module out of every game list and turn its colliders off
        private static void Detach(Module m)
        {
            var go = (GameObject)fObject.GetValue(m);
            ((List<Construction>)fConstructions.GetValue(null)).Remove(m);
            var dict = fConstructionDict.GetValue(null) as System.Collections.IDictionary;
            if (dict != null && go != null && dict.Contains(go)) dict.Remove(go);
            ((List<Module>)fModules.GetValue(null)).Remove(m);
            foreach (var list in (List<Module>[])fCategories.GetValue(null)) list?.Remove(m);
            if (go != null) foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            m.setValidPosition(false);
        }

        private static void DestroyModel(Ghost g)
        {
            if (g.Model == null) return;
            try
            {
                var go = (GameObject)fObject.GetValue(g.Model);
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            catch (Exception e) { Log.Warn("ghost destroy: " + e.Message); }
            g.Model = null;
        }

        private static void Remove(int player)
        {
            if (sGhosts.TryGetValue(player, out var g)) { DestroyModel(g); sGhosts.Remove(player); }
        }

        public static void Clear()
        {
            foreach (var g in sGhosts.Values) DestroyModel(g);
            sGhosts.Clear();
            sLastSent = null;
        }

        // ---------------------------------------------------------------- name labels
        private static GUIStyle sLabel;

        public static void OnGUI()
        {
            if (sGhosts.Count == 0 || Camera.main == null) return;
            if (sLabel == null)
            {
                sLabel = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
                sLabel.normal.textColor = Color.white;
            }
            foreach (var g in sGhosts.Values)
            {
                if (g.Model == null || string.IsNullOrEmpty(g.Name)) continue;
                var go = (GameObject)fObject.GetValue(g.Model);
                if (go == null) continue;
                Vector3 sp = Camera.main.WorldToScreenPoint(go.transform.position + Vector3.up * 8f);
                if (sp.z <= 0f) continue;
                var size = sLabel.CalcSize(new GUIContent(g.Name));
                GUI.Box(new Rect(sp.x - size.x / 2 - 6, Screen.height - sp.y - size.y / 2, size.x + 12, size.y + 4), g.Name, sLabel);
            }
        }

        // tests: ghosts shown here, "player:type:size:x:z:valid"
        public static string Digest()
        {
            var parts = new List<string>();
            foreach (var g in sGhosts.Values)
            {
                if (g.Model == null) continue;
                var p = ((GameObject)fObject.GetValue(g.Model)).transform.position;
                parts.Add($"{g.Player}:{g.Type}:{g.Size}:{p.x:F1}:{p.z:F1}:{(g.Model.isValidPosition() ? 1 : 0)}");
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }
    }
}
