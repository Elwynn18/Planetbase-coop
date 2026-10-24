using System.Collections.Generic;
using System.IO;
using Planetbase;
using PlanetbaseCoop.Net;

namespace PlanetbaseCoop.Sync
{
    // Landing permissions and manufacture limits. The game's windows write RefBool/RefInt values
    // directly (no method to hook), so: the host broadcasts the values; a joiner whose values drift
    // from what the host last sent has edited them, and sends just the changed keys.
    public static class Settings
    {
        private static Dictionary<string, int> sLastSent;       // host
        private static Dictionary<string, int> sLastApplied;    // joiner
        private static float sTimer, sKeyTimer;

        public static void Reset() { sLastSent = null; sLastApplied = null; }

        public static Dictionary<string, int> Read()
        {
            var d = new Dictionary<string, int>();
            var lp = Singleton<LandingShipManager>.getInstance().getLandingPermissions();
            d["land.colonists"] = lp.getColonistRefBool().get() ? 1 : 0;
            d["land.visitors"] = lp.getVisitorRefBool().get() ? 1 : 0;
            d["land.merchants"] = lp.getMerchantRefBool().get() ? 1 : 0;
            foreach (var s in SpecializationList.getColonistSpecializations())
                d["land.pct." + s.GetType().Name] = lp.getSpecializationPercentage(s).get();
            var ml = Singleton<ManufactureLimits>.getInstance();
            foreach (var s in SpecializationList.getBotSpecializations())
                d["limit.bot." + s.GetType().Name] = ml.getBotLimit(s).get();
            foreach (var t in TypeList<ResourceType, ResourceTypeList>.get())
            {
                var r = ml.getResourceLimit(t);
                if (r != null) d["limit.res." + t.GetType().Name] = r.get();
            }
            return d;
        }

        public static void Apply(string key, int value)
        {
            var lp = Singleton<LandingShipManager>.getInstance().getLandingPermissions();
            var ml = Singleton<ManufactureLimits>.getInstance();
            if (key == "land.colonists") lp.getColonistRefBool().set(value != 0);
            else if (key == "land.visitors") lp.getVisitorRefBool().set(value != 0);
            else if (key == "land.merchants") lp.getMerchantRefBool().set(value != 0);
            else if (key.StartsWith("land.pct."))
            {
                var s = TypeList<Specialization, SpecializationList>.find(key.Substring(9));
                if (s != null) lp.getSpecializationPercentage(s).set(value);
            }
            else if (key.StartsWith("limit.bot."))
            {
                var s = TypeList<Specialization, SpecializationList>.find(key.Substring(10));
                if (s != null) ml.getBotLimit(s).set(value);
            }
            else if (key.StartsWith("limit.res."))
            {
                var t = TypeList<ResourceType, ResourceTypeList>.find(key.Substring(10));
                var r = t == null ? null : ml.getResourceLimit(t);
                r?.set(value);
            }
        }

        private static void Write(BinaryWriter w, Dictionary<string, int> d)
        {
            w.Write(d.Count);
            foreach (var kv in d) { w.Write(kv.Key); w.Write(kv.Value); }
        }

        private static Dictionary<string, int> ReadMap(BinaryReader r)
        {
            int n = r.ReadInt32();
            var d = new Dictionary<string, int>();
            for (int i = 0; i < n && i < 1000; i++) d[r.ReadString()] = r.ReadInt32();
            return d;
        }

        // host, every frame
        public static void HostTick(float dt, ITransport t, IEnumerable<int> peers)
        {
            sTimer += dt; sKeyTimer += dt;
            if (sTimer < 1f) return;
            sTimer = 0f;
            var now = Read();
            bool key = sKeyTimer >= 5f;
            if (!key && sLastSent != null && Same(now, sLastSent)) return;
            if (key) sKeyTimer = 0f;
            sLastSent = now;
            var msg = Wire.Build(Msg.Settings, w => Write(w, now));
            foreach (var p in peers) t.Send(p, msg);
        }

        // joiner: host values arrived
        public static void OnSettings(BinaryReader r)
        {
            var d = ReadMap(r);
            foreach (var kv in d) Apply(kv.Key, kv.Value);
            sLastApplied = Read();
        }

        // joiner, every frame: did the player edit something?
        public static void ClientTick(float dt)
        {
            if (sLastApplied == null) return;
            sTimer += dt;
            if (sTimer < 0.5f) return;
            sTimer = 0f;
            var now = Read();
            var changed = new Dictionary<string, int>();
            foreach (var kv in now)
                if (!sLastApplied.TryGetValue(kv.Key, out int old) || old != kv.Value) changed[kv.Key] = kv.Value;
            if (changed.Count == 0) return;
            sLastApplied = now;
            Commands.Send(Cmd.Settings, w => Write(w, changed));
        }

        // host: a joiner changed settings
        public static string OnCommand(BinaryReader r)
        {
            foreach (var kv in ReadMap(r)) Apply(kv.Key, kv.Value);
            return null;
        }

        private static bool Same(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a) if (!b.TryGetValue(kv.Key, out int v) || v != kv.Value) return false;
            return true;
        }
    }
}
