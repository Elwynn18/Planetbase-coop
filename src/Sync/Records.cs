using System;
using System.IO;
using System.Reflection;
using Module = Planetbase.Module;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Net;
using UnityEngine;

namespace PlanetbaseCoop.Sync
{
    // Compact state records: what changes while the game runs, per kind.
    // Host: Read(obj) -> Write(w). Joiner: Read(r) -> Apply(obj).

    public struct CharRec
    {
        public int Id;
        public Vector3 Pos;
        public Quaternion Rot;
        public byte Location;
        public byte State;
        public int Anim;
        public float AnimSpeed;
        public byte Prop1, Prop2;
        public int Loaded;

        private static readonly FieldInfo fAnim = AccessTools.Field(typeof(Character), "mCurrentAnimation");
        private static readonly FieldInfo fState = AccessTools.Field(typeof(Character), "mState");
        private static readonly FieldInfo fLocation = AccessTools.Field(typeof(Character), "mLocation");

        public static CharRec From(Character c)
        {
            var rec = new CharRec
            {
                Id = c.getId(),
                Pos = c.getPosition(),
                Rot = c.getRotation(),
                Location = (byte)(int)fLocation.GetValue(c),
                State = (byte)(int)fState.GetValue(c),
                Loaded = c.getLoadedResource()?.getId() ?? -1,
                Anim = -1,
                Prop1 = (byte)CharacterProp.Count,
                Prop2 = (byte)CharacterProp.Count,
            };
            var anim = (CharacterAnimation)fAnim.GetValue(c);
            if (anim != null)
            {
                rec.Anim = (int)anim.getAnimationType();
                rec.AnimSpeed = anim.getSpeed();
                var props = anim.getProps();
                if (props != null && props.Length > 0) rec.Prop1 = (byte)props[0];
                if (props != null && props.Length > 1) rec.Prop2 = (byte)props[1];
            }
            return rec;
        }

        public bool SameAs(CharRec o) =>
            o.Id == Id && (o.Pos - Pos).sqrMagnitude < 0.0004f && Quaternion.Angle(o.Rot, Rot) < 1f && o.Location == Location &&
            o.State == State && o.Anim == Anim && Mathf.Abs(o.AnimSpeed - AnimSpeed) < 0.01f && o.Prop1 == Prop1 && o.Prop2 == Prop2 && o.Loaded == Loaded;

        public void Write(BinaryWriter w)
        {
            w.Write(Id); w.WriteVec(Pos); w.WriteQuat(Rot); w.Write(Location); w.Write(State);
            w.Write(Anim); w.Write(AnimSpeed); w.Write(Prop1); w.Write(Prop2); w.Write(Loaded);
        }

        public static CharRec Read(BinaryReader r) => new CharRec
        {
            Id = r.ReadInt32(), Pos = r.ReadVec(), Rot = r.ReadQuat(), Location = r.ReadByte(), State = r.ReadByte(),
            Anim = r.ReadInt32(), AnimSpeed = r.ReadSingle(), Prop1 = r.ReadByte(), Prop2 = r.ReadByte(), Loaded = r.ReadInt32(),
        };
    }

    public struct BuildRec
    {
        public int Id;
        public byte State;
        public float Progress;
        public bool Enabled;
        public bool HighPriority;
        public float Condition;
        public float Oxygen;      // constructions only
        public bool Powered;      // constructions only
        public bool Water;        // constructions only
        public int ProducedItem;  // components only

        private static readonly FieldInfo fProgress = AccessTools.Field(typeof(Buildable), "mBuildProgress");
        private static readonly FieldInfo fEnabled = AccessTools.Field(typeof(Buildable), "mEnabled");
        private static readonly FieldInfo fConditionC = AccessTools.Field(typeof(Construction), "mConditionIndicator");
        private static readonly FieldInfo fConditionCC = AccessTools.Field(typeof(ConstructionComponent), "mConditionIndicator");
        private static readonly FieldInfo fOxygen = AccessTools.Field(typeof(Construction), "mOxygenIndicator");
        private static readonly FieldInfo fProduced = AccessTools.Field(typeof(ConstructionComponent), "mProducedItemIndex");

        public static float ProgressOf(Buildable b) => ((Indicator)fProgress.GetValue(b)).getValue();
        public static Indicator ProgressIndicator(Buildable b) => (Indicator)fProgress.GetValue(b);
        public static Indicator ConditionIndicator(Selectable s) =>
            s is Construction ? fConditionC.GetValue(s) as Indicator : s is ConstructionComponent ? fConditionCC.GetValue(s) as Indicator : null;

        public static BuildRec From(Buildable b)
        {
            var rec = new BuildRec
            {
                Id = b.getId(),
                State = (byte)Registry.StateOf(b),
                Progress = ProgressOf(b),
                Enabled = (bool)fEnabled.GetValue(b),
                HighPriority = b.isHighPriority(),
                Condition = ConditionIndicator(b)?.getValue() ?? 1f,
                ProducedItem = -1,
            };
            if (b is Construction c)
            {
                rec.Oxygen = ((Indicator)fOxygen.GetValue(c)).getValue();
                rec.Powered = c.isPowered();
                rec.Water = c.hasWater();
            }
            else if (b is ConstructionComponent cc && fProduced != null)
            {
                rec.ProducedItem = (int)fProduced.GetValue(cc);
            }
            return rec;
        }

        public bool SameAs(BuildRec o) =>
            o.Id == Id && o.State == State && Mathf.Abs(o.Progress - Progress) < 0.002f && o.Enabled == Enabled &&
            o.HighPriority == HighPriority && Mathf.Abs(o.Condition - Condition) < 0.005f && Mathf.Abs(o.Oxygen - Oxygen) < 0.005f &&
            o.Powered == Powered && o.Water == Water && o.ProducedItem == ProducedItem;

        public void Write(BinaryWriter w)
        {
            w.Write(Id); w.Write(State); w.Write(Progress); w.Write(Enabled); w.Write(HighPriority);
            w.Write(Condition); w.Write(Oxygen); w.Write(Powered); w.Write(Water); w.Write(ProducedItem);
        }

        public static BuildRec Read(BinaryReader r) => new BuildRec
        {
            Id = r.ReadInt32(), State = r.ReadByte(), Progress = r.ReadSingle(), Enabled = r.ReadBoolean(), HighPriority = r.ReadBoolean(),
            Condition = r.ReadSingle(), Oxygen = r.ReadSingle(), Powered = r.ReadBoolean(), Water = r.ReadBoolean(), ProducedItem = r.ReadInt32(),
        };

        public static int ProducedItemOf(ConstructionComponent cc) => fProduced == null ? -1 : (int)fProduced.GetValue(cc);
        public static void SetEnabledField(Buildable b, bool v) => fEnabled.SetValue(b, v);
        public static Indicator OxygenIndicator(Construction c) => (Indicator)fOxygen.GetValue(c);
    }
}

namespace PlanetbaseCoop.Sync
{
    // Ships: position and state. Joiners replay only the visual side of a state change; the
    // game's onLanded spawns colonists/visitors/merchants, which must happen on the host only.
    public struct ShipRec
    {
        public int Id;
        public Vector3 Pos;
        public Quaternion Rot;
        public int State;

        private static readonly FieldInfo fLandingState = AccessTools.Field(typeof(LandingShip), "mState");
        private static readonly FieldInfo fColonyState = AccessTools.Field(typeof(ColonyShip), "mState");
        private static readonly MethodInfo mEngines = AccessTools.Method(typeof(LandingShip), "setEnginesActive");
        private static readonly MethodInfo mColonyOpenDoor = AccessTools.Method(typeof(ColonyShip), "onOpenDoor");
        private static readonly MethodInfo mColonyStopParticles = AccessTools.Method(typeof(ColonyShip), "stopParticles");

        public static ShipRec From(Ship s)
        {
            var t = s.getTransform();
            var rec = new ShipRec { Id = s.getId(), Pos = t.position, Rot = t.rotation, State = -1 };
            if (s is LandingShip) rec.State = (int)fLandingState.GetValue(s);
            else if (s is ColonyShip) rec.State = (int)fColonyState.GetValue(s);
            return rec;
        }

        public bool SameAs(ShipRec o) => o.Id == Id && o.State == State && (o.Pos - Pos).sqrMagnitude < 0.0004f && Quaternion.Angle(o.Rot, Rot) < 0.5f;

        public void Write(BinaryWriter w) { w.Write(Id); w.WriteVec(Pos); w.WriteQuat(Rot); w.Write(State); }

        public static ShipRec Read(BinaryReader r) => new ShipRec { Id = r.ReadInt32(), Pos = r.ReadVec(), Rot = r.ReadQuat(), State = r.ReadInt32() };

        public static void ApplyState(Ship s, int state)
        {
            if (s is LandingShip ls)
            {
                int cur = (int)fLandingState.GetValue(ls);
                if (cur == state) return;
                fLandingState.SetValue(ls, (LandingShip.State)state);
                switch ((LandingShip.State)state)
                {
                    case LandingShip.State.OpeningDoor: ls.onOpeningDoor(); break;
                    case LandingShip.State.Landed: mEngines.Invoke(ls, new object[] { false }); break;
                    case LandingShip.State.ClosingDoor: ls.onClosingDoor(); break;
                    case LandingShip.State.TakingOff: mEngines.Invoke(ls, new object[] { true }); break;
                }
            }
            else if (s is ColonyShip cs)
            {
                int cur = (int)fColonyState.GetValue(cs);
                if (cur == state) return;
                var stateType = fColonyState.FieldType;
                fColonyState.SetValue(cs, Enum.ToObject(stateType, state));
                string name = Enum.GetName(stateType, state);
                if (name == "Landed") mColonyStopParticles.Invoke(cs, null);
                else if (name == "OpenDoor") mColonyOpenDoor.Invoke(cs, null);
            }
        }
    }
}
