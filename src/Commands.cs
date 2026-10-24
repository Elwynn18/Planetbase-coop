using System;
using System.IO;
using System.Reflection;
using Module = Planetbase.Module;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Net;
using PlanetbaseCoop.Sync;
using UnityEngine;

namespace PlanetbaseCoop
{
    public enum Cmd : byte
    {
        PlaceModule = 1, Link = 2, PlaceComponent = 3, Recycle = 4, SetEnabled = 5, SetPriority = 6,
        Speed = 7, Alert = 8, ProducedItem = 9,
    }

    // Joiner: the game's commit points are intercepted and sent to the host.
    // Host: the same commit is replayed on the real objects, found by id.
    public static class Commands
    {
        public static int Sent, Executed, Refused;
        public static string LastResult;

        // true while the joiner applies host data (so game calls made by the mirror are not sent back)
        public static bool ApplyingRemote;

        private static readonly FieldInfo fActiveModule = AccessTools.Field(typeof(GameStateGame), "mActiveModule");
        private static readonly FieldInfo fPlacedComponent = AccessTools.Field(typeof(GameStateGame), "mPlacedComponent");
        private static readonly MethodInfo mModuleEnd = AccessTools.Method(typeof(GameStateGame), "onModulePlacementEnd");
        private static readonly MethodInfo mComponentEnd = AccessTools.Method(typeof(GameStateGame), "onComponentPlacementEnd");
        private static readonly MethodInfo mAddToast = AccessTools.Method(typeof(GameStateGame), "addToast");

        public static void Patch(Harmony h)
        {
            Pre(h, AccessTools.Method(typeof(GameStateGame), "placeModule"), nameof(PlaceModulePrefix));
            Pre(h, AccessTools.Method(typeof(Module), nameof(Module.linkModules)), nameof(LinkPrefix));
            Pre(h, AccessTools.Method(typeof(GameStateGame), "placeComponent"), nameof(PlaceComponentPrefix));
            Pre(h, AccessTools.Method(typeof(Selection), nameof(Selection.deleteSelected)), nameof(DeletePrefix));
            Pre(h, AccessTools.Method(typeof(GuiMenuSystem), "onButtonDisable"), nameof(DisablePrefix));
            Pre(h, AccessTools.Method(typeof(GuiMenuSystem), "onButtonEnable"), nameof(EnablePrefix));
            Pre(h, AccessTools.Method(typeof(GuiMenuSystem), "onButtonPriorityUp"), nameof(PriorityUpPrefix));
            Pre(h, AccessTools.Method(typeof(GuiMenuSystem), "onButtonPriorityDown"), nameof(PriorityDownPrefix));
            Pre(h, AccessTools.Method(typeof(GameStateGame), "increaseSpeed"), nameof(FasterPrefix));
            Pre(h, AccessTools.Method(typeof(GameStateGame), "decreaseSpeed"), nameof(SlowerPrefix));
            Pre(h, AccessTools.Method(typeof(SecurityManager), nameof(SecurityManager.setAlertState)), nameof(AlertPrefix));
            Pre(h, AccessTools.Method(typeof(ConstructionComponent), nameof(ConstructionComponent.setProducedItemIndex)), nameof(ProducedPrefix));
        }

        private static void Pre(Harmony h, MethodInfo m, string prefix)
        {
            if (m == null) throw new MissingMethodException("command hook missing: " + prefix);
            h.Patch(m, prefix: new HarmonyMethod(typeof(Commands), prefix));
        }

        private static bool Joiner => Mirror.Active && !ApplyingRemote;

        public static void Send(Cmd cmd, Action<BinaryWriter> body)
        {
            Sent++;
            Session.SendCommand(Wire.Build(Msg.Command, w => { w.Write((byte)cmd); body?.Invoke(w); }));
            Log.Info("command sent: " + cmd);
        }

        // ---------------------------------------------------------------- joiner hooks
        private static bool PlaceModulePrefix(GameStateGame __instance)
        {
            if (!Joiner) return true;
            var m = fActiveModule.GetValue(__instance) as Module;
            if (m == null) return false;
            if (m.isValidPosition())
            {
                var type = m.getModuleType().GetType().Name;
                int size = m.getSizeIndex();
                Vector3 pos = m.getPosition();
                Quaternion rot = m.getTransform().rotation;
                Send(Cmd.PlaceModule, w => { w.Write(type); w.Write(size); w.WriteVec(pos); w.WriteQuat(rot); });
                m.playSound(SoundList.getInstance().ConstructionPlace);
            }
            else
            {
                Singleton<AudioPlayer>.getInstance().play(SoundList.getInstance().ButtonClickWrong);
            }
            m.destroy();   // the real module comes back from the host with the host's id
            mModuleEnd.Invoke(__instance, null);
            return false;
        }

        private static bool LinkPrefix(Module m1, Module m2)
        {
            if (!Joiner) return true;
            if (Connection.canLink(m1, m2))
            {
                int a = m1.getId(), b = m2.getId();
                Send(Cmd.Link, w => { w.Write(a); w.Write(b); });
            }
            return false;
        }

        private static bool PlaceComponentPrefix(GameStateGame __instance)
        {
            if (!Joiner) return true;
            var cc = fPlacedComponent.GetValue(__instance) as ConstructionComponent;
            if (cc == null) return false;
            int module = cc.getParentConstruction().getId();
            string type = cc.getComponentType().GetType().Name;
            Vector3 pos = cc.getPosition();
            Quaternion rot = cc.getRotation();
            Send(Cmd.PlaceComponent, w => { w.Write(module); w.Write(type); w.WriteVec(pos); w.WriteQuat(rot); });
            cc.playSound(SoundList.getInstance().ComponentPlace);
            cc.destroy();
            mComponentEnd.Invoke(__instance, null);
            return false;
        }

        private static bool DeletePrefix()
        {
            if (!Joiner) return true;
            var sel = Selection.getSelected();
            if (sel != null && KindOf(sel, out var kind))
            {
                int id = sel.getId();
                Send(Cmd.Recycle, w => { w.Write((byte)kind); w.Write(id); });
            }
            Selection.clear();
            return false;
        }

        private static void SendEnabled(bool on)
        {
            var b = Selection.getSelectedBuildable();
            if (b != null && KindOf(b, out var kind)) Send(Cmd.SetEnabled, w => { w.Write((byte)kind); w.Write(b.getId()); w.Write(on); });
        }

        // enable/priority: applied locally too (menus show the new state at once), the host confirms
        private static bool DisablePrefix() { if (Joiner) SendEnabled(false); return true; }
        private static bool EnablePrefix() { if (Joiner) SendEnabled(true); return true; }

        private static void SendPriority(bool high)
        {
            var c = Selection.getSelectedConstruction();
            if (c != null) Send(Cmd.SetPriority, w => { w.Write(c.getId()); w.Write(high); });
        }

        private static bool PriorityUpPrefix() { if (Joiner) SendPriority(true); return true; }
        private static bool PriorityDownPrefix() { if (Joiner) SendPriority(false); return true; }

        private static bool FasterPrefix() { if (!Joiner) return true; Send(Cmd.Speed, w => w.Write(1)); return false; }
        private static bool SlowerPrefix() { if (!Joiner) return true; Send(Cmd.Speed, w => w.Write(-1)); return false; }

        private static bool AlertPrefix(AlertState alertState)
        {
            if (!Joiner) return true;
            Send(Cmd.Alert, w => w.Write((int)alertState));
            return false;
        }

        private static bool ProducedPrefix(ConstructionComponent __instance, int index)
        {
            if (!Joiner) return true;
            int id = __instance.getId();
            Send(Cmd.ProducedItem, w => { w.Write(id); w.Write(index); });
            return true;   // show it at once; the host's record confirms
        }

        public static bool KindOf(Selectable s, out Kind kind)
        {
            kind = Kind.Module;
            switch (s)
            {
                case Module _: kind = Kind.Module; return true;
                case Connection _: kind = Kind.Connection; return true;
                case ConstructionComponent _: kind = Kind.Component; return true;
                case Resource _: kind = Kind.Resource; return true;
                case Character _: kind = Kind.Character; return true;
                case Ship _: kind = Kind.Ship; return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- host side
        public static void OnHostCommand(int peer, string who, BinaryReader r)
        {
            var cmd = (Cmd)r.ReadByte();
            string refusal = null;
            try
            {
                refusal = Execute(cmd, r);
            }
            catch (Exception e)
            {
                refusal = "failed: " + e.Message;
                Log.Warn($"command {cmd} from {who}: {e}");
            }
            if (refusal == null) Executed++; else Refused++;
            Log.Info($"command {cmd} from {who}: {refusal ?? "ok"}");
            var result = refusal;
            Session.Transport?.Send(peer, Wire.Build(Msg.CommandResult, w => { w.Write((byte)cmd); w.Write(result == null); w.Write(result ?? ""); }));
            TestBot.OnHostCommand(cmd, result == null);
        }

        private static string Execute(Cmd cmd, BinaryReader r)
        {
            switch (cmd)
            {
                case Cmd.PlaceModule:
                {
                    string typeName = r.ReadString();
                    int size = r.ReadInt32();
                    Vector3 pos = r.ReadVec();
                    Quaternion rot = r.ReadQuat();
                    var type = TypeList<ModuleType, ModuleTypeList>.find(typeName);
                    if (type == null) return "unknown module " + typeName;
                    if (size < type.getMinSize() || size > type.getMaxSize()) return "bad size";
                    float floor = Singleton<TerrainGenerator>.getInstance().getFloorHeight();
                    var m = Module.create(new Vector3(pos.x, floor, pos.z), size, type);
                    m.getTransform().rotation = rot;
                    if (!type.hasFlag(2) && !m.canPlaceModule(new Vector3(pos.x, floor, pos.z), Vector3.up, Module.ValidSizes[size]))
                    {
                        m.destroy();
                        return "That spot is no longer free.";
                    }
                    m.getTransform().rotation = rot;
                    m.setPosition(new Vector3(pos.x, floor, pos.z));
                    m.setValidPosition(validPosition: true);
                    m.setPositionY(floor);
                    m.onUserPlaced();
                    return null;
                }
                case Cmd.Link:
                {
                    var a = Registry.Find(Kind.Module, r.ReadInt32()) as Module;
                    var b = Registry.Find(Kind.Module, r.ReadInt32()) as Module;
                    if (a == null || b == null) return "module not found";
                    if (!Connection.canLink(a, b)) return "These modules can't be linked.";
                    Module.linkModules(a, b, true);
                    return null;
                }
                case Cmd.PlaceComponent:
                {
                    var m = Registry.Find(Kind.Module, r.ReadInt32()) as Module;
                    string typeName = r.ReadString();
                    Vector3 pos = r.ReadVec();
                    Quaternion rot = r.ReadQuat();
                    if (m == null) return "module not found";
                    var type = TypeList<ComponentType, ComponentTypeList>.find(typeName);
                    if (type == null) return "unknown component " + typeName;
                    var cc = ConstructionComponent.create(m, type);
                    cc.setPosition(pos);
                    cc.setRotation(rot);
                    if (!m.canPlaceComponent(cc))
                    {
                        cc.destroy();
                        return "That spot is no longer free.";
                    }
                    cc.setPositionY(m.getFloorPosition().y);
                    m.addComponent(cc);
                    cc.onUserPlaced();
                    return null;
                }
                case Cmd.Recycle:
                {
                    var kind = (Kind)r.ReadByte();
                    var obj = Registry.Find(kind, r.ReadInt32()) as Selectable;
                    if (obj == null) return null;   // already gone
                    if (!obj.isDeleteable(out bool enabled) || !enabled) return "This can't be recycled right now.";
                    if (Selection.getSelected() == obj) Selection.clear();
                    obj.recycle();
                    obj.destroy();
                    return null;
                }
                case Cmd.SetEnabled:
                {
                    var kind = (Kind)r.ReadByte();
                    var b = Registry.Find(kind, r.ReadInt32()) as Buildable;
                    bool on = r.ReadBoolean();
                    if (b == null) return "not found";
                    if (b.isEnabled() != on) b.setEnabled(on);
                    return null;
                }
                case Cmd.SetPriority:
                {
                    var c = Registry.Find(Kind.Module, r.ReadInt32()) as Construction;
                    bool high = r.ReadBoolean();
                    if (c == null) return "not found";
                    c.setHighPriority(high);
                    return null;
                }
                case Cmd.Speed:
                {
                    int dir = r.ReadInt32();
                    var tm = Singleton<TimeManager>.getInstance();
                    if (dir > 0) tm.increaseSpeed(); else tm.decreaseSpeed();
                    var game = GameAccess.Game();
                    if (game != null) mAddToast.Invoke(game, new object[] { StringList.get("speed_set") + " x" + tm.getTimeScale(), 3f });
                    return null;
                }
                case Cmd.Alert:
                {
                    Singleton<SecurityManager>.getInstance().setAlertState((AlertState)r.ReadInt32());
                    return null;
                }
                case Cmd.ProducedItem:
                {
                    var cc = Registry.Find(Kind.Component, r.ReadInt32()) as ConstructionComponent;
                    int index = r.ReadInt32();
                    if (cc == null) return "not found";
                    cc.setProducedItemIndex(index);
                    return null;
                }
            }
            return "unknown command";
        }

        // ---------------------------------------------------------------- joiner: results
        public static void OnResult(BinaryReader r)
        {
            var cmd = (Cmd)r.ReadByte();
            bool ok = r.ReadBoolean();
            string msg = r.ReadString();
            LastResult = cmd + ": " + (ok ? "ok" : msg);
            if (!ok) Ui.Toast(msg);
            TestBot.OnCommandResult(cmd, ok, msg);
        }
    }
}
