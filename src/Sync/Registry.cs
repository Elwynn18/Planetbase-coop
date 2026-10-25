using System;
using System.Collections.Generic;
using System.Reflection;
using Module = Planetbase.Module;
using System.Xml;
using HarmonyLib;
using Planetbase;

namespace PlanetbaseCoop.Sync
{
    // Kinds in creation order: a joiner creates modules before connections (links), constructions
    // before resources (storage and component contents are created inline with their building).
    public enum Kind : byte { Module = 0, Connection = 1, Component = 2, Ship = 3, Resource = 4, Character = 5 }

    // Object lists, lookup, XML out and XML in, all through the game's own code.
    public static class Registry
    {
        public static readonly Kind[] All = { Kind.Module, Kind.Connection, Kind.Component, Kind.Ship, Kind.Resource, Kind.Character };

        private static readonly FieldInfo fConstructions = AccessTools.Field(typeof(Construction), "mConstructions");
        private static readonly FieldInfo fComponents = AccessTools.Field(typeof(ConstructionComponent), "mComponents");
        private static readonly FieldInfo fCharacters = AccessTools.Field(typeof(Character), "mCharacters");
        private static readonly FieldInfo fResources = AccessTools.Field(typeof(Resource), "mResources");
        private static readonly FieldInfo fShips = AccessTools.Field(typeof(Ship), "mShips");
        private static readonly FieldInfo fBuildState = AccessTools.Field(typeof(Buildable), "mState");
        private static readonly FieldInfo fDocument = AccessTools.Field(typeof(Serialization), "mDocument");
        private static readonly FieldInfo fIds = AccessTools.Field(typeof(Serialization), "mIds");
        private static readonly MethodInfo mCharSerialize = AccessTools.Method(typeof(Character), "serialize", new[] { typeof(XmlNode), typeof(string) });
        private static readonly MethodInfo mShipSerialize = AccessTools.Method(typeof(Ship), "serialize", new[] { typeof(XmlNode), typeof(string) });

        public static List<Construction> Constructions => (List<Construction>)fConstructions.GetValue(null);
        public static List<ConstructionComponent> Components => (List<ConstructionComponent>)fComponents.GetValue(null);
        public static List<Character> Characters => (List<Character>)fCharacters.GetValue(null);
        public static List<Resource> Resources => (List<Resource>)fResources.GetValue(null);
        public static List<Ship> Ships => (List<Ship>)fShips.GetValue(null);

        public static BuildableState StateOf(Buildable b) => (BuildableState)fBuildState.GetValue(b);
        public static void SetState(Buildable b, BuildableState s) => fBuildState.SetValue(b, s);

        // Live, shareable objects of a kind (no placement previews, nothing destroyed).
        public static void Collect(Kind kind, Dictionary<int, object> into)
        {
            into.Clear();
            switch (kind)
            {
                case Kind.Module:
                case Kind.Connection:
                    foreach (var c in Constructions)
                    {
                        if (c == null || c.isDestroyed() || StateOf(c) == BuildableState.Placing) continue;
                        if (kind == Kind.Module ? c is Module : c is Connection) into[c.getId()] = c;
                    }
                    break;
                case Kind.Component:
                    foreach (var c in Components)
                    {
                        if (c == null || c.isDestroyed() || StateOf(c) == BuildableState.Placing) continue;
                        var parent = c.getParentConstruction();
                        if (parent == null || parent.isDestroyed() || StateOf(parent) == BuildableState.Placing) continue;
                        if (!parent.getComponents().Contains(c)) continue;   // a preview not yet added
                        into[c.getId()] = c;
                    }
                    break;
                case Kind.Character:
                    foreach (var c in Characters) if (c != null && !c.isDestroyed()) into[c.getId()] = c;
                    break;
                case Kind.Resource:
                    foreach (var r in Resources) if (r != null && !r.isDestroyed()) into[r.getId()] = r;
                    break;
                case Kind.Ship:
                    foreach (var s in Ships) if (s != null && !s.isDestroyed()) into[s.getId()] = s;
                    break;
            }
        }

        public static object Find(Kind kind, int id)
        {
            switch (kind)
            {
                case Kind.Module:
                case Kind.Connection:
                    foreach (var c in Constructions) if (c != null && c.getId() == id && !c.isDestroyed()) return c;
                    return null;
                case Kind.Component:
                    foreach (var c in Components) if (c != null && c.getId() == id && !c.isDestroyed()) return c;
                    return null;
                case Kind.Character:
                    foreach (var c in Characters) if (c != null && c.getId() == id && !c.isDestroyed()) return c;
                    return null;
                case Kind.Resource:
                    foreach (var r in Resources) if (r != null && r.getId() == id && !r.isDestroyed()) return r;
                    return null;
                case Kind.Ship:
                    foreach (var s in Ships) if (s != null && s.getId() == id && !s.isDestroyed()) return s;
                    return null;
            }
            return null;
        }

        // ------------------------------------------------------------------ XML out (host)
        public static string ToXml(Kind kind, object obj, out int parentId)
        {
            parentId = -1;
            if (fDocument.GetValue(null) != null) throw new InvalidOperationException("serializer busy");
            var doc = new XmlDocument();
            fDocument.SetValue(null, doc);
            try
            {
                XmlNode root = doc.AppendChild(doc.CreateElement("coop"));
                switch (kind)
                {
                    case Kind.Module:
                    case Kind.Connection:
                        ((Construction)obj).serialize(root, "construction");
                        break;
                    case Kind.Component:
                        var cc = (ConstructionComponent)obj;
                        parentId = cc.getParentConstruction().getId();
                        cc.serialize(root, "component");
                        break;
                    case Kind.Character:
                        mCharSerialize.Invoke(obj, new object[] { root, "character" });
                        break;
                    case Kind.Resource:
                        ((Resource)obj).serialize(root, "resource");
                        break;
                    case Kind.Ship:
                        mShipSerialize.Invoke(obj, new object[] { root, "ship" });
                        break;
                }
                return root.FirstChild.OuterXml;
            }
            finally
            {
                fDocument.SetValue(null, null);
            }
        }

        // Any game object that writes itself with Serialization.* -> XML string, and back.
        public static string XmlOut(Action<XmlNode> write)
        {
            if (fDocument.GetValue(null) != null) throw new InvalidOperationException("serializer busy");
            var doc = new XmlDocument();
            fDocument.SetValue(null, doc);
            try
            {
                XmlNode root = doc.AppendChild(doc.CreateElement("coop"));
                write(root);
                return root.OuterXml;
            }
            finally { fDocument.SetValue(null, null); }
        }

        public static void XmlIn(string xml, Action<XmlNode> read)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            object prevDoc = fDocument.GetValue(null), prevIds = fIds.GetValue(null);
            fDocument.SetValue(null, doc);
            fIds.SetValue(null, new HashSet<int>());
            try { read(doc.DocumentElement); }
            finally { fDocument.SetValue(null, prevDoc); fIds.SetValue(null, prevIds); }
        }

        // ------------------------------------------------------------------ XML in (joiner)
        // Returns the created object, or null when it already exists or can't be created yet
        // (missing parent or link); `missing` says what is missing.
        public static object Create(Kind kind, int id, string xml, int parentId, out string missing)
        {
            missing = null;
            if (Find(kind, id) != null) return null;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            XmlNode node = doc.DocumentElement;
            object prevDoc = fDocument.GetValue(null), prevIds = fIds.GetValue(null);
            fDocument.SetValue(null, doc);
            fIds.SetValue(null, new HashSet<int>());
            try
            {
                switch (kind)
                {
                    case Kind.Module:
                    {
                        var m = Module.create(node);
                        Construction.calculateGrid();
                        m.setColor(m.calculateColor(), immediate: true);
                        return m;
                    }
                    case Kind.Connection:
                    {
                        var links = Serialization.deserializeIntList(node["links"], "id");
                        var a = Find(Kind.Module, links[0]) as Module;
                        var b = Find(Kind.Module, links[1]) as Module;
                        if (a == null || b == null) { missing = "module " + (a == null ? links[0] : links[1]); return null; }
                        var c = Connection.create(a, b, node);
                        Construction.calculateGrid();
                        c.setColor(c.calculateColor(), immediate: true);
                        return c;
                    }
                    case Kind.Component:
                    {
                        var parent = Find(Kind.Module, parentId) as Construction ?? Find(Kind.Connection, parentId) as Construction;
                        if (parent == null) { missing = "construction " + parentId; return null; }
                        var cc = ConstructionComponent.create(parent, node);
                        parent.addComponent(cc);
                        return cc;
                    }
                    case Kind.Character:
                        return Character.create(Serialization.deserializeType(node), node);
                    case Kind.Resource:
                        return Resource.create(node);
                    case Kind.Ship:
                    {
                        string type = Serialization.deserializeType(node);
                        return type == "ColonyShip" ? (object)ColonyShip.create(node) : LandingShip.create(type, node);
                    }
                }
                return null;
            }
            finally
            {
                fDocument.SetValue(null, prevDoc);
                fIds.SetValue(null, prevIds);
            }
        }

        public static void Destroy(Kind kind, object obj)
        {
            if (Selection.getSelected() == obj) Selection.clear();
            switch (kind)
            {
                case Kind.Module:
                case Kind.Connection:
                    ((Construction)obj).destroy();
                    Construction.calculateGrid();
                    break;
                case Kind.Component:
                    var cc = (ConstructionComponent)obj;
                    cc.getParentConstruction()?.removeComponent(cc);
                    cc.destroy();
                    break;
                case Kind.Character:
                    ((Character)obj).destroy();
                    break;
                case Kind.Resource:
                    ((Resource)obj).destroy();
                    break;
                case Kind.Ship:
                    ((Ship)obj).destroy();
                    break;
            }
        }
    }
}
