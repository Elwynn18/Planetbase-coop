using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Module = Planetbase.Module;
using HarmonyLib;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop.Sync
{
    // Joiner side: applies what the host sends to the frozen local world.
    public static class WorldApply
    {
        private class Pending { public Kind Kind; public int Id; public int Parent; public string Xml; public float Age; }

        private class CharTarget { public Character Obj; public CharRec Rec; public int AppliedAnim = int.MinValue; public float AppliedSpeed; public byte P1, P2; }

        private static readonly List<Pending> sPending = new List<Pending>();
        private static readonly Dictionary<int, CharTarget> sChars = new Dictionary<int, CharTarget>();
        private static readonly Dictionary<int, (Ship ship, ShipRec rec)> sShips = new Dictionary<int, (Ship, ShipRec)>();
        public static int Created, Destroyed, CreateFailures, StatesApplied;

        private static readonly FieldInfo fCharState = AccessTools.Field(typeof(Character), "mState");
        private static readonly FieldInfo fBuildStages = AccessTools.Field(typeof(Buildable), "mBuildStages");
        private static readonly MethodInfo mSetLocation = AccessTools.Method(typeof(Character), "setLocation", new[] { typeof(Location) });
        private static readonly Action<Construction, float, bool> dTopRender = AccessTools.MethodDelegate<Action<Construction, float, bool>>(AccessTools.Method(typeof(Construction), "updateTopRender"));
        private static readonly Action<Construction, float> dTargetColor = AccessTools.MethodDelegate<Action<Construction, float>>(AccessTools.Method(typeof(Construction), "updateTargetColor"));
        private static readonly Action<Module, float> dBlink = AccessTools.MethodDelegate<Action<Module, float>>(AccessTools.Method(typeof(Module), "updateBlinkingLights"));
        private static readonly Action<Module, float> dNacelle = AccessTools.MethodDelegate<Action<Module, float>>(AccessTools.Method(typeof(Module), "updateNacelle"));
        private static readonly Action<Module, float> dPanels = AccessTools.MethodDelegate<Action<Module, float>>(AccessTools.Method(typeof(Module), "updatePanels"));
        private static readonly Action<Module, float> dScan = AccessTools.MethodDelegate<Action<Module, float>>(AccessTools.Method(typeof(Module), "updateScanAnimation"));
        private static readonly Action<ConstructionComponent, float> dCompColor = AccessTools.MethodDelegate<Action<ConstructionComponent, float>>(AccessTools.Method(typeof(ConstructionComponent), "updateTargetColor"));

        public static void Reset()
        {
            sPending.Clear();
            sChars.Clear();
            sShips.Clear();
            Created = Destroyed = CreateFailures = StatesApplied = 0;
        }

        // ---------------------------------------------------------------- lifecycle
        public static void OnCreated(BinaryReader r)
        {
            var p = new Pending { Kind = (Kind)r.ReadByte(), Id = r.ReadInt32(), Parent = r.ReadInt32(), Xml = r.ReadString() };
            if (!TryCreate(p)) sPending.Add(p);
        }

        private static bool TryCreate(Pending p)
        {
            try
            {
                var obj = Registry.Create(p.Kind, p.Id, p.Xml, p.Parent, out string missing);
                if (obj == null && missing != null) return false;   // parent not here yet: retry
                if (obj != null)
                {
                    Created++;
                    if (obj is Character c) Track(c);
                }
                return true;
            }
            catch (Exception e)
            {
                CreateFailures++;
                Log.Warn($"create {p.Kind} {p.Id} failed: {e.Message}");
                return true;   // drop; the census will try again
            }
        }

        public static void OnDestroyed(BinaryReader r)
        {
            var k = (Kind)r.ReadByte();
            int id = r.ReadInt32();
            sPending.RemoveAll(p => p.Kind == k && p.Id == id);
            var obj = Registry.Find(k, id);
            if (obj == null) return;
            try
            {
                if (k == Kind.Resource) ResourcePlaces.Release((Resource)obj);
                Registry.Destroy(k, obj);
                Destroyed++;
            }
            catch (Exception e)
            {
                Log.Warn($"destroy {k} {id} failed: {e.Message}");
            }
            if (k == Kind.Character) sChars.Remove(id);
        }

        private static CharTarget Track(Character c)
        {
            if (!sChars.TryGetValue(c.getId(), out var t))
            {
                t = new CharTarget { Obj = c, Rec = CharRec.From(c) };
                sChars[c.getId()] = t;
            }
            t.Obj = c;
            return t;
        }

        // ---------------------------------------------------------------- states
        public static void OnStates(BinaryReader r)
        {
            var kind = (StateKind)r.ReadByte();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                try
                {
                    switch (kind)
                    {
                        case StateKind.Character: ApplyChar(CharRec.Read(r)); break;
                        case StateKind.Buildable: ApplyBuild(BuildRec.Read(r)); break;
                        case StateKind.Resource: ApplyRes(ResRec.Read(r)); break;
                        case StateKind.Ship: ApplyShip(ShipRec.Read(r)); break;
                        case StateKind.CharStats:
                        {
                            int id = r.ReadInt32();
                            var v = new float[8];
                            for (int j = 0; j < 8; j++) v[j] = r.ReadSingle();
                            if (Registry.Find(Kind.Character, id) is Character ch) CharStats.Apply(ch, v);
                            break;
                        }
                    }
                    StatesApplied++;
                }
                catch (Exception e)
                {
                    Log.Warn($"apply {kind} record failed: {e.Message}");
                }
            }
        }

        private static void ApplyChar(CharRec rec)
        {
            if (!sChars.TryGetValue(rec.Id, out var t) || t.Obj == null || t.Obj.isDestroyed())
            {
                var c = Registry.Find(Kind.Character, rec.Id) as Character;
                if (c == null) return;
                t = Track(c);
            }
            var ch = t.Obj;
            t.Rec = rec;
            if ((byte)ch.getLocation() != rec.Location) mSetLocation.Invoke(ch, new object[] { (Location)rec.Location });
            fCharState.SetValue(ch, (Character.State)rec.State);
            if (rec.Anim >= 0 && (rec.Anim != t.AppliedAnim || rec.Prop1 != t.P1 || rec.Prop2 != t.P2 || Mathf.Abs(rec.AnimSpeed - t.AppliedSpeed) > 0.01f))
            {
                var anim = new CharacterAnimation((CharacterAnimationType)rec.Anim, (CharacterProp)rec.Prop1, (CharacterProp)rec.Prop2);
                anim.setSpeed(rec.AnimSpeed);
                ch.playAnimation(anim, WrapMode.Loop);
                t.AppliedAnim = rec.Anim; t.AppliedSpeed = rec.AnimSpeed; t.P1 = rec.Prop1; t.P2 = rec.Prop2;
            }
            int loaded = ch.getLoadedResource()?.getId() ?? -1;
            if (loaded != rec.Loaded)
            {
                if (rec.Loaded < 0) ResourcePlaces.Release(ch.getLoadedResource());
                else if (Registry.Find(Kind.Resource, rec.Loaded) is Resource res)
                {
                    ResourcePlaces.Release(res);
                    if (ch.getLoadedResource() != null) ResourcePlaces.Release(ch.getLoadedResource());
                    ch.loadResource(res);
                }
            }
            if ((ch.getPosition() - rec.Pos).sqrMagnitude > 64f) ch.setTransform(rec.Pos, rec.Rot);   // far: snap
        }

        private static void ApplyBuild(BuildRec rec)
        {
            var b = (Registry.Find(Kind.Module, rec.Id) ?? Registry.Find(Kind.Component, rec.Id)) as Buildable;
            if (b == null) return;
            var st = Registry.StateOf(b);
            var target = (BuildableState)rec.State;
            if (st == BuildableState.Placed && (target == BuildableState.Building || target == BuildableState.Built)) { b.startBuilding(); st = BuildableState.Building; }
            if (st == BuildableState.Building)
            {
                float cur = BuildRec.ProgressOf(b);
                float want = target == BuildableState.Built ? 1f : rec.Progress;
                if (want > cur + 0.0001f)
                {
                    int stages = Math.Max(1, (int)fBuildStages.GetValue(b));
                    // the game's own build step, so the dome assembles the same way it does on the host
                    b.updateBuild((want - cur) * stages / 0.4f + (target == BuildableState.Built ? 1f : 0f));
                }
            }
            if (BuildRec.ConditionIndicator(b) is Indicator cond) cond.setValue(rec.Condition);
            if (b.isEnabled() != rec.Enabled) b.setEnabled(rec.Enabled);
            if (b is Construction c)
            {
                if (c.isHighPriority() != rec.HighPriority) c.setHighPriority(rec.HighPriority);
                BuildRec.OxygenIndicator(c).setValue(rec.Oxygen);
                if (c.isPowered() != rec.Powered) c.setPower(rec.Powered);
                if (c.hasWater() != rec.Water) c.setHasWater(rec.Water);
            }
            else if (b is ConstructionComponent cc && rec.ProducedItem >= 0 && BuildRec.ProducedItemOf(cc) != rec.ProducedItem)
            {
                cc.setProducedItemIndex(rec.ProducedItem);
            }
        }

        private static void ApplyShip(ShipRec rec)
        {
            if (!(Registry.Find(Kind.Ship, rec.Id) is Ship s)) { sShips.Remove(rec.Id); return; }
            ShipRec.ApplyState(s, rec.State);
            if ((s.getTransform().position - rec.Pos).sqrMagnitude > 400f) { s.getTransform().position = rec.Pos; s.getTransform().rotation = rec.Rot; }
            sShips[rec.Id] = (s, rec);
        }

        private static void ApplyRes(ResRec rec)
        {
            if (TestEnv.Mut("no-res-states")) return;
            if (Registry.Find(Kind.Resource, rec.Id) is Resource r) ResourcePlaces.Apply(r, rec);
        }

        // ---------------------------------------------------------------- per frame (mirror FixedUpdate)
        public static void FixedTick(float dt)
        {
            for (int i = sPending.Count - 1; i >= 0; i--)
            {
                var p = sPending[i];
                p.Age += dt;
                if (TryCreate(p) || p.Age > 10f)
                {
                    if (p.Age > 10f) { CreateFailures++; Log.Warn($"gave up creating {p.Kind} {p.Id}"); }
                    sPending.RemoveAt(i);
                }
            }
            float k = 1f - Mathf.Exp(-dt * 12f);
            foreach (var t in sChars.Values)
            {
                var c = t.Obj;
                if (c == null || c.isDestroyed()) continue;
                var pos = Vector3.Lerp(c.getPosition(), t.Rec.Pos, k);
                var rot = Quaternion.Slerp(c.getRotation(), t.Rec.Rot, k);
                c.setTransform(pos, rot);
            }
            foreach (var kv in sShips)
            {
                var (ship, rec) = kv.Value;
                if (ship == null || ship.isDestroyed()) continue;
                var tr = ship.getTransform();
                tr.position = Vector3.Lerp(tr.position, rec.Pos, k);
                tr.rotation = Quaternion.Slerp(tr.rotation, rec.Rot, k);
            }
            var cons = Registry.Constructions;
            for (int i = 0; i < cons.Count; i++)
            {
                var c = cons[i];
                if (c == null || c.isDestroyed()) continue;
                try
                {
                    c.updateLight(dt);
                    dTopRender(c, dt, false);
                    dTargetColor(c, dt);
                    if (c is Module m && m.isBuilt())
                    {
                        dBlink(m, dt); dNacelle(m, dt); dPanels(m, dt); dScan(m, dt);
                    }
                    var comps = c.getComponents();
                    for (int j = 0; j < comps.Count; j++) dCompColor(comps[j], dt);
                }
                catch (Exception e)
                {
                    Log.Warn("visual update failed: " + e.Message);
                }
            }
            Singleton<EnvironmentManager>.getInstance().update(0f);
            Singleton<MessageLog>.getInstance().update(dt);   // the sim block that normally ticks it is skipped
        }
    }
}
