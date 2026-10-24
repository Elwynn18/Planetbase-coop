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
    public enum Holder : byte { None = 0, Character = 1, Storage = 2, Container = 3 }

    public struct ResRec
    {
        public int Id;
        public byte State;
        public Vector3 Pos;
        public Quaternion Rot;
        public byte Location;
        public Holder Holder;
        public int HolderId;
        public int Slot;

        public static ResRec From(Resource r)
        {
            var rec = new ResRec { Id = r.getId(), State = (byte)r.getState(), Location = (byte)r.getLocation(), HolderId = -1, Slot = -1 };
            var go = r.getGameObject();
            rec.Pos = go != null ? go.transform.position : Vector3.zero;
            rec.Rot = go != null ? go.transform.rotation : Quaternion.identity;
            ResourcePlaces.Where(r, out rec.Holder, out rec.HolderId, out rec.Slot);
            return rec;
        }

        public bool SameAs(ResRec o) =>
            o.Id == Id && o.State == State && o.Location == Location && o.Holder == Holder && o.HolderId == HolderId && o.Slot == Slot &&
            (Holder != Holder.None || ((o.Pos - Pos).sqrMagnitude < 0.0004f && Quaternion.Angle(o.Rot, Rot) < 1f));

        public void Write(BinaryWriter w)
        {
            w.Write(Id); w.Write(State); w.WriteVec(Pos); w.WriteQuat(Rot); w.Write(Location); w.Write((byte)Holder); w.Write(HolderId); w.Write(Slot);
        }

        public static ResRec Read(BinaryReader r) => new ResRec
        {
            Id = r.ReadInt32(), State = r.ReadByte(), Pos = r.ReadVec(), Rot = r.ReadQuat(), Location = r.ReadByte(),
            Holder = (Holder)r.ReadByte(), HolderId = r.ReadInt32(), Slot = r.ReadInt32(),
        };
    }

    // Where a resource is and how to move it, using the game's own calls.
    public static class ResourcePlaces
    {
        private static readonly FieldInfo fContainer = AccessTools.Field(typeof(Resource), "mContainer");
        private static readonly FieldInfo fSlot = AccessTools.Field(typeof(Resource), "mStorageSlot");
        private static readonly FieldInfo fState = AccessTools.Field(typeof(Resource), "mState");
        private static readonly FieldInfo fModuleStorage = AccessTools.Field(typeof(Module), "mResourceStorage");
        private static readonly FieldInfo fStorageSlots = AccessTools.Field(typeof(ResourceStorage), "mSlots");
        private static readonly FieldInfo fContainerList = AccessTools.Field(typeof(ResourceContainer), "mResources");

        // rebuilt once per frame by whoever asks first
        private static int sFrame = -1;
        private static readonly Dictionary<StorageSlot, (int module, int index)> sSlotIndex = new Dictionary<StorageSlot, (int, int)>();
        private static readonly Dictionary<Resource, int> sCarrier = new Dictionary<Resource, int>();

        private static void Index()
        {
            if (sFrame == Time.frameCount) return;
            sFrame = Time.frameCount;
            sSlotIndex.Clear();
            sCarrier.Clear();
            foreach (var c in Registry.Constructions)
            {
                if (!(c is Module m) || m.isDestroyed()) continue;
                var storage = fModuleStorage.GetValue(m) as ResourceStorage;
                if (storage == null) continue;
                var slots = (List<StorageSlot>)fStorageSlots.GetValue(storage);
                for (int i = 0; i < slots.Count; i++) sSlotIndex[slots[i]] = (m.getId(), i);
            }
            foreach (var ch in Registry.Characters)
            {
                var res = ch?.getLoadedResource();
                if (res != null && !ch.isDestroyed()) sCarrier[res] = ch.getId();
            }
        }

        public static void Where(Resource r, out Holder holder, out int holderId, out int slot)
        {
            Index();
            holderId = -1;
            slot = -1;
            if (fContainer.GetValue(r) != null) { holder = Holder.Container; return; }
            var s = fSlot.GetValue(r) as StorageSlot;
            if (s != null && sSlotIndex.TryGetValue(s, out var at)) { holder = Holder.Storage; holderId = at.module; slot = at.index; return; }
            if (sCarrier.TryGetValue(r, out int cid)) { holder = Holder.Character; holderId = cid; return; }
            holder = Holder.None;
        }

        public static StorageSlot SlotOf(int moduleId, int index)
        {
            var m = Registry.Find(Kind.Module, moduleId) as Module;
            var storage = m == null ? null : fModuleStorage.GetValue(m) as ResourceStorage;
            if (storage == null) return null;
            var slots = (List<StorageSlot>)fStorageSlots.GetValue(storage);
            return index >= 0 && index < slots.Count ? slots[index] : null;
        }

        // Joiner: take a resource out of wherever it is locally.
        public static void Release(Resource r)
        {
            sFrame = -1;
            var container = fContainer.GetValue(r) as ResourceContainer;
            if (container != null)
            {
                var list = fContainerList?.GetValue(container) as List<Resource>;
                list?.Remove(r);
                r.onExtract();
            }
            var slot = fSlot.GetValue(r) as StorageSlot;
            if (slot != null)
            {
                slot.removeResource(r);
                r.removeFromStorage();
            }
            foreach (var ch in Registry.Characters)
            {
                if (ch != null && ch.getLoadedResource() == r)
                {
                    ch.unloadResource(Resource.State.Idle);
                    break;
                }
            }
            var go = r.getGameObject();
            if (go != null && !go.activeSelf) go.SetActive(true);
        }

        // Joiner: put the resource where the host has it.
        public static void Apply(Resource r, ResRec rec)
        {
            Where(r, out var holder, out int holderId, out int slot);
            bool moved = holder != rec.Holder || holderId != rec.HolderId || slot != rec.Slot;
            if (moved)
            {
                Release(r);
                switch (rec.Holder)
                {
                    case Holder.Character:
                        var ch = Registry.Find(Kind.Character, rec.HolderId) as Character;
                        if (ch != null)
                        {
                            var other = ch.getLoadedResource();
                            if (other != null && other != r) Release(other);
                            ch.loadResource(r);
                        }
                        break;
                    case Holder.Storage:
                        var s = SlotOf(rec.HolderId, rec.Slot);
                        if (s != null) s.addResource(r);
                        break;
                    case Holder.Container:
                        var go = r.getGameObject();
                        if (go != null) go.SetActive(false);   // embedded resources are hidden in the game too
                        break;
                }
                sFrame = -1;
            }
            fState.SetValue(r, (Resource.State)rec.State);
            r.setLocation((Location)rec.Location);
            if (rec.Holder == Holder.None && r.getGameObject() != null)
            {
                r.setPosition(rec.Pos);
                r.setRotation(rec.Rot);
            }
            else if (rec.Holder == Holder.Storage && r.getGameObject() != null)
            {
                r.setPosition(rec.Pos);   // exact stack height from the host
                r.setRotation(rec.Rot);
            }
        }
    }
}
