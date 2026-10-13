using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Patch 1. The game decides "is this terrain" with reference equality against
    /// GameAssets.i.terrainMaterial (LandingGear.FixedUpdate, Bullet/FragTrace impacts, rotor CheckCollisions,
    /// TerrainScatter.GenerateScatter, Downwash, GroundVehicle jobs and more). Our bundle carries its own
    /// "Terrain" PhysicMaterial, a different object. Swapping the stock object onto our MeshColliders fixes every
    /// call site at once without patching any of them.
    /// </summary>
    static class TerrainMaterialPatch
    {
        internal static void Apply(MapContext ctx)
        {
            var assets = GameAssets.i;
            var stock = assets != null ? assets.terrainMaterial : null;
            if (stock == null) { Plugin.Log.LogError("[1] GameAssets.i.terrainMaterial is null; nothing swapped."); return; }

            var names = new HashSet<string>(Plugin.ColliderMaterialNames.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
            var counts = new Dictionary<string, int>();
            int swapped = 0, already = 0, skipped = 0;
            foreach (var mc in ctx.Root.GetComponentsInChildren<MeshCollider>(true))
            {
                var pm = mc.sharedMaterial;
                if (pm == stock) { already++; continue; }
                bool underTerrain = ctx.TerrainRoot != null && mc.transform.IsChildOf(ctx.TerrainRoot);
                bool match = pm != null ? names.Contains(Clean(pm.name)) : underTerrain;
                if (!match) { skipped++; continue; }
                string key = pm != null ? pm.name : "<none>";
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                mc.sharedMaterial = stock;
                swapped++;
            }
            Plugin.Log.LogInfo($"[1] Stock terrain PhysicMaterial '{stock.name}' (dyn {stock.dynamicFriction}, static {stock.staticFriction}, bounce {stock.bounciness}). " +
                               $"Swapped {swapped} MeshColliders [{string.Join(", ", counts.Select(kv => kv.Key + " x" + kv.Value))}], already stock {already}, left alone {skipped}.");
            if (swapped == 0 && already == 0) Plugin.Log.LogWarning("[1] No terrain colliders found. Check ColliderMaterialNames and the 'Terrain' child.");
        }

        static string Clean(string n) => n.Replace("(Instance)", "").Trim();

        // ------------------------------------------------------------------ runway pads

        static readonly Regex widthRx = new Regex(@",\s*([0-9]+(?:\.[0-9]+)?)\s*m\)", RegexOptions.Compiled);

        /// <summary>
        /// Side effect of patch 1: our runways are part of the terrain mesh, so after the swap LandingGear sees
        /// soft ground on them (sink, drag, dust). Stock airbase pavement MeshColliders carry no PhysicMaterial.
        /// For every "Runway ... (layout, W m)" marker with Start/End children, add a thin BoxCollider with no
        /// material whose top sits just above the terrain along the runway.
        /// </summary>
        internal static void AddRunwayPads(MapContext ctx)
        {
            Physics.SyncTransforms();
            var terrainCols = new HashSet<Collider>(ctx.TileColliders);
            int layer = ctx.Tiles.Count > 0 ? ctx.Tiles[0].gameObject.layer : 6;
            int made = 0, seen = 0;
            foreach (var t in ctx.Root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("Runway ")) continue;
                var s = t.Find("Start"); var e = t.Find("End");
                if (s == null || e == null) continue;
                seen++;
                try { if (MakePad(ctx, t, s.position, e.position, terrainCols, layer)) made++; }
                catch (Exception ex) { Plugin.Log.LogError($"[1b] Pad for '{t.name}' failed: {ex.Message}"); }
            }
            Plugin.Log.LogInfo($"[1b] Runway markers {seen}, pads added {made}.");
        }

        static bool MakePad(MapContext ctx, Transform rw, Vector3 a, Vector3 b, HashSet<Collider> terrainCols, int layer)
        {
            var m = widthRx.Match(rw.name);
            float width = m.Success ? float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : Plugin.RunwayPadDefaultWidth.Value;
            Vector3 flat = b - a; flat.y = 0;
            float len = flat.magnitude;
            if (len < 1f) { Plugin.Log.LogWarning($"[1b] '{rw.name}' has zero length, skipped."); return false; }
            Vector3 dir = flat / len, side = Vector3.Cross(Vector3.up, dir);
            float over = Plugin.RunwayPadOverrun.Value;
            float total = len + 2f * over;
            Vector3 start = a - dir * over;

            // Sample terrain on a grid along the pad, fit height = h0 + slope * along, track residuals.
            var along = new List<float>(); var h = new List<float>();
            int nl = Mathf.Max(2, Mathf.CeilToInt(total / 25f) + 1);
            float[] across = { -0.5f * width, -0.25f * width, 0f, 0.25f * width, 0.5f * width };
            for (int i = 0; i < nl; i++)
            {
                float d = total * i / (nl - 1);
                foreach (float x in across)
                {
                    Vector3 p = start + dir * d + side * x;
                    var hits = Physics.RaycastAll(p + Vector3.up * 1000f, Vector3.down, 3000f, ~0, QueryTriggerInteraction.Ignore);
                    float best = float.NegativeInfinity;
                    foreach (var hit in hits) if (terrainCols.Contains(hit.collider) && hit.point.y > best) best = hit.point.y;
                    if (!float.IsNegativeInfinity(best)) { along.Add(d); h.Add(best); }
                }
            }
            if (h.Count < nl) { Plugin.Log.LogWarning($"[1b] '{rw.name}': only {h.Count} terrain hits, skipped."); return false; }

            float ma = along.Average(), mh = h.Average(), sxy = 0, sxx = 0;
            for (int i = 0; i < h.Count; i++) { sxy += (along[i] - ma) * (h[i] - mh); sxx += (along[i] - ma) * (along[i] - ma); }
            float slope = sxx > 0 ? sxy / sxx : 0f;
            float rMin = float.MaxValue, rMax = float.MinValue;
            for (int i = 0; i < h.Count; i++)
            {
                float r = h[i] - (mh + slope * (along[i] - ma));
                rMin = Mathf.Min(rMin, r); rMax = Mathf.Max(rMax, r);
            }
            float spread = rMax - rMin;
            if (spread > Plugin.RunwayPadTolerance.Value)
            {
                Plugin.Log.LogWarning($"[1b] '{rw.name}': terrain spread {spread:F2} m > tolerance {Plugin.RunwayPadTolerance.Value} m, no pad (runway stays soft ground).");
                return false;
            }

            const float thickness = 2f;
            float lift = rMax + 0.03f;
            float hMid = mh + slope * (total * 0.5f - ma) + lift;
            Vector3 centreTop = start + dir * (total * 0.5f); centreTop.y = hMid;
            Vector3 fwd = (dir * total + Vector3.up * slope * total).normalized;
            var rot = Quaternion.LookRotation(fwd, Vector3.up);   // pitched by the fitted slope, no roll

            var go = new GameObject($"TerrainLabRunwayPad {rw.name}");
            go.layer = layer;
            go.transform.SetParent(rw, true);
            go.transform.SetPositionAndRotation(centreTop - (rot * Vector3.up) * (thickness * 0.5f), rot);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(width, thickness, total);
            box.sharedMaterial = null;
            Plugin.Log.LogInfo($"[1b] Pad '{rw.name}': {width:F0} x {total:F0} m, slope {slope * 100f:F2} %, terrain spread {spread:F2} m, top {lift:F2} m above fit, layer {layer}.");
            return true;
        }
    }
}
