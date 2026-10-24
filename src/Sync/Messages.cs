using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Net;
using UnityEngine;

namespace PlanetbaseCoop.Sync
{
    // Game notifications ("A ship is arriving", "Low oxygen") come from the host's simulation;
    // the host forwards each new one and joiners add it to their own message log.
    public static class Messages
    {
        private static readonly FieldInfo fDesc = AccessTools.Field(typeof(Message), "mDescription");
        private static readonly FieldInfo fIcon = AccessTools.Field(typeof(Message), "mIcon");
        private static readonly FieldInfo fTarget = AccessTools.Field(typeof(Message), "mTargetSelectable");
        private static readonly FieldInfo fTargetPos = AccessTools.Field(typeof(Message), "mTargetPosition");
        private static readonly FieldInfo fFlags = AccessTools.Field(typeof(Message), "mFlags");
        private static Dictionary<string, Texture2D> sIcons;
        public static int Forwarded, Shown;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(MessageLog), nameof(MessageLog.addMessage)),
                postfix: new HarmonyMethod(typeof(Messages), nameof(AddPostfix)));
        }

        private static void AddPostfix(Message message)
        {
            if (!Session.IsHost || Session.Transport == null || message == null) return;
            try
            {
                string desc = (string)fDesc.GetValue(message);
                var icon = fIcon.GetValue(message) as Texture2D;
                var target = fTarget.GetValue(message) as Selectable;
                var pos = (Vector3)fTargetPos.GetValue(message);
                int flags = (int)fFlags.GetValue(message);
                int targetId = -1;
                byte kind = 255;
                if (target != null && Commands.KindOf(target, out var k)) { kind = (byte)k; targetId = target.getId(); }
                var msg = Wire.Build(Msg.GameMessage, w =>
                {
                    w.Write(desc ?? ""); w.Write(icon != null ? icon.name : ""); w.Write(kind); w.Write(targetId); w.WriteVec(pos); w.Write(flags);
                });
                foreach (var p in Session.ReadyPeers) Session.Transport.Send(p, msg);
                Forwarded++;
            }
            catch (Exception e) { Log.Warn("message forward: " + e.Message); }
        }

        public static void OnMessage(BinaryReader r)
        {
            string desc = r.ReadString(), iconName = r.ReadString();
            byte kind = r.ReadByte();
            int targetId = r.ReadInt32();
            Vector3 pos = r.ReadVec();
            int flags = r.ReadInt32();
            var icon = FindIcon(iconName) ?? ResourceList.StaticIcons.MessageAlert;
            Message m;
            var target = kind != 255 ? Registry.Find((Kind)kind, targetId) as Selectable : null;
            if (target != null) m = new Message(desc, icon, target, flags);
            else m = new Message(desc, icon, icon, pos, flags);
            Singleton<MessageLog>.getInstance().addMessage(m);
            Shown++;
        }

        // StaticIcons is a class of static Texture2D fields; match by texture name
        private static Texture2D FindIcon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (sIcons == null)
            {
                sIcons = new Dictionary<string, Texture2D>();
                var icons = ResourceList.StaticIcons;
                if (icons != null)
                    foreach (var f in icons.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (f.GetValue(icons) is Texture2D t && t != null && !sIcons.ContainsKey(t.name)) sIcons[t.name] = t;
                    }
            }
            return sIcons.TryGetValue(name, out var tex) ? tex : null;
        }
    }
}
