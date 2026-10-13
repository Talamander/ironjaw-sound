using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerrainLab.Gameplay
{
    /// <summary>
    /// Sits stock building copies on our terrain.
    ///
    /// The stock maps tilt some MapBuildings to their own hillsides, and a suburb block is a row of separate
    /// houses that the stock map fitted to its ground. Copied onto our map with that tilt, one end of a block
    /// digs into the terrain and the other floats (seen in game on opt-1-v6, 2026-10-07).
    ///
    /// Clone() now turns the copy upright (KeepStockTilt=false). Then each house unit (each direct child
    /// that has meshes, or the whole copy if it is one piece) moves up or down by the difference between the
    /// ground under its own footprint (lowest corner) and the ground under the copy's pivot. A unit keeps its
    /// height above ground from the stock design, but follows our slope. Ground comes from raycasts against our
    /// map's colliders, ignoring the copy itself and every other building. Diagnostics go to the log.
    /// </summary>
    static class Grounding
    {
        static BepInEx.Logging.ManualLogSource Log => Plugin.Log;
        static readonly HashSet<string> censused = new HashSet<string>();
        static readonly RaycastHit[] hits = new RaycastHit[32];

        public static void Apply(GameObject copy, string source, string label, float pivotY, Vector3 stockTilt)
        {
            Physics.SyncTransforms();
            Transform root = copy.transform;
            var units = Units(root);
            bool havePivot = GroundAt(root.position, root, out float pivotGround);
            if (!havePivot) pivotGround = root.position.y;

            bool census = Plugin.GroundingLog.Value && censused.Add(source);
            if (census)
            {
                var b0 = WorldBounds(root);
                Log.LogInfo($"[TLGameplay] ground census {source}: stock euler ({stockTilt.x:F1}, {stockTilt.z:F1}) deg, {units.Count} unit(s), " +
                            $"mesh bottom {b0.min.y - root.position.y:F2} m and top {b0.max.y - root.position.y:F2} m from pivot, " +
                            $"size {b0.size.x:F0} x {b0.size.z:F0} m");
            }

            float worst = 0f;
            foreach (var u in units)
            {
                var b = WorldBounds(u);
                if (b.size == Vector3.zero) continue;
                if (!Footprint(b, root, out float gMin, out float gMax)) continue;
                float bottomBefore = b.min.y - gMin;
                if (Plugin.GroundUnits.Value && havePivot)
                {
                    float shift = gMin - pivotGround;
                    if (Mathf.Abs(shift) > 0.02f) u.position += new Vector3(0f, shift, 0f);
                    b.center += new Vector3(0f, shift, 0f);
                }
                float relief = gMax - gMin;
                worst = Mathf.Max(worst, relief);
                if (census)
                    Log.LogInfo($"[TLGameplay]   unit {u.name}: bottom-ground before {bottomBefore:F2} m, after {b.min.y - gMin:F2} m, relief {relief:F2} m");
            }
            if (!havePivot)
                Log.LogWarning($"[TLGameplay] ground: no terrain hit under {source} ({label}) at {root.position}; left at manifest height");
            else if (Plugin.GroundingLog.Value && Mathf.Abs(pivotGround - root.position.y) > 0.5f)
                Log.LogInfo($"[TLGameplay] ground: {source} ({label}) pivot {root.position.y - pivotGround:F2} m from terrain (manifest y {pivotY:F2})");
            if (Plugin.GroundingLog.Value && worst > 2f)
                Log.LogInfo($"[TLGameplay] ground: {source} ({label}) has a unit on {worst:F1} m of relief");
        }

        /// House units: direct children holding meshes. A copy whose meshes sit on the root, or that has only one
        /// such child, moves as one piece.
        static List<Transform> Units(Transform root)
        {
            if (root.GetComponent<MeshFilter>() != null) return new List<Transform> { root };
            var kids = new List<Transform>();
            foreach (Transform c in root)
                if (c.GetComponentsInChildren<MeshFilter>(true).Any(f => f.sharedMesh != null)) kids.Add(c);
            return kids.Count > 1 ? kids : new List<Transform> { root };
        }

        /// World bounds from mesh bounds, so it also works while the copy is still inactive.
        static Bounds WorldBounds(Transform t)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var f in t.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null) continue;
                var mb = f.sharedMesh.bounds;
                var m = f.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }

        static bool Footprint(Bounds b, Transform ignore, out float gMin, out float gMax)
        {
            gMin = float.MaxValue; gMax = float.MinValue;
            int n = 0;
            float[] fx = { 0.1f, 0.5f, 0.9f };
            foreach (float ax in fx)
                foreach (float az in fx)
                {
                    var p = new Vector3(Mathf.Lerp(b.min.x, b.max.x, ax), b.max.y, Mathf.Lerp(b.min.z, b.max.z, az));
                    if (!GroundAt(p, ignore, out float g)) continue;
                    gMin = Mathf.Min(gMin, g); gMax = Mathf.Max(gMax, g); n++;
                }
            return n > 0;
        }

        /// Highest collider under p that is not part of `ignore` or of any building.
        static bool GroundAt(Vector3 p, Transform ignore, out float y)
        {
            y = 0f;
            var origin = new Vector3(p.x, p.y + 1500f, p.z);
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, hits, 4000f, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var c = hits[i].collider;
                if (c == null || c.transform.IsChildOf(ignore)) continue;
                if (c.GetComponentInParent<MapBuilding>() != null || UnderBuildings(c.transform)) continue;
                if (!found || hits[i].point.y > y) { y = hits[i].point.y; found = true; }
            }
            return found;
        }

        /// Our original buildings live under Surfaces/Buildings.
        static bool UnderBuildings(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name == "Buildings" && t.parent != null && t.parent.name == "Surfaces") return true;
            return false;
        }
    }
}
