using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Planetbase;
using UnityEngine;
using Module = Planetbase.Module;

namespace PlanetbaseCoop
{
    // Says why Module.canPlaceModule (1.4.2) refuses a spot, check by check, for the log.
    // A joiner's placement is checked against its mirrored world; when it disagrees with the host's,
    // this tells which object was in the way.
    public static class PlacementDiag
    {
        private static readonly MethodInfo mAnyLinks = AccessTools.Method(typeof(Module), "anyPotentialLinks", new[] { typeof(Vector3) });
        private static readonly FieldInfo fDict = AccessTools.Field(typeof(Construction), "mConstructionDictionary");

        // a collider, where it is and how close it comes to a point
        public static string Describe(Collider col, Vector3 point)
        {
            var b = col.bounds;
            var root = col.transform.root;
            var cp = col.ClosestPoint(point);
            return $"collider '{col.name}' {col.GetType().Name} enabled={col.enabled} bounds {b.center.x:F1},{b.center.y:F1},{b.center.z:F1} size {b.size.x:F1}x{b.size.y:F1}x{b.size.z:F1}, closest {(cp - point).magnitude:F1} m; root at {root.position.x:F1},{root.position.y:F1},{root.position.z:F1} rot {root.rotation.eulerAngles.y:F0}";
        }

        // every ship collider within 30 m of a point, nearest first
        public static string Ships(Vector3 point)
        {
            var parts = new List<string>();
            foreach (var col in Physics.OverlapSphere(point, 30f, 65536)) parts.Add(Describe(col, point));
            return parts.Count == 0 ? "none within 30 m" : string.Join(" | ", parts);
        }

        public static string Why(Module m, Vector3 position)
        {
            var why = new List<string>();
            try
            {
                float floor = Singleton<TerrainGenerator>.getInstance().getFloorHeight();
                float size = Module.ValidSizes[m.getSizeIndex()];
                float dy = position.y - floor;
                if (Mathf.Abs(dy) > 0.1f) why.Add($"height {dy:F2} above floor");
                position.y = floor;
                var center = new Vector2(1000f, 1000f);
                if ((center - new Vector2(position.x, position.z)).magnitude > 375f) why.Add("outside the buildable area");
                var dict = (Dictionary<GameObject, Construction>)fDict.GetValue(null);
                if (Sync.Registry.Constructions.Count > 1 && mAnyLinks != null && !(bool)mAnyLinks.Invoke(m, new object[] { position }))
                    why.Add("no module in link range");
                var hits = Physics.SphereCastAll(position + Vector3.up * 20f, size * 0.5f + 9f, Vector3.down, 40f, 4198400);
                foreach (var h in hits)
                {
                    var root = h.collider.gameObject.transform.root.gameObject;
                    dict.TryGetValue(root, out Construction c);
                    if (c == null || c == m) continue;
                    float gap = (position - c.getPosition()).magnitude - m.getRadius() - c.getRadius();
                    if (c is Connection) why.Add($"connection {c.getId()} in the way");
                    else if (gap < 9f) why.Add($"{c.GetType().Name} {c.getId()} too close ({gap:F1} m gap)");
                }
                foreach (var col in Physics.OverlapSphere(position, size * 0.5f + 10f, 65536))
                    why.Add($"ship '{col.transform.root.name}' within {size * 0.5f + 10f:F0} m ({Describe(col, position)})");
                foreach (var col in Physics.OverlapSphere(position, size * 0.5f + 2f, 1024))
                    why.Add($"resource '{col.transform.root.name}' at {col.transform.position.x:F1},{col.transform.position.z:F1}");
                if (why.Count == 0) why.Add("terrain slope (or a check this list does not cover)");
            }
            catch (System.Exception e)
            {
                why.Add("diag failed: " + e.Message);
            }
            return string.Join("; ", why);
        }
    }
}
