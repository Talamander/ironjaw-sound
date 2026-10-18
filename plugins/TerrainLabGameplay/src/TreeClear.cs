using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Effects;
using UnityEngine;

namespace TerrainLab.Gameplay
{
    /// <summary>
    /// Keeps the baked trees and rocks off mission-placed buildings and landing points on our maps.
    ///
    /// Trees (TreePositions in the map bundle) and TerrainLabScatter rocks are baked at build time. The build keeps
    /// them off our own buildings, roads and clearings, but it cannot know where a mission puts a helipad, a FARP,
    /// an LZ camp or a supply dump, and stock NO only removes trees for blasts. So a mission (ours or anyone's) could
    /// stand a spruce clump on a helipad (Discord report 2026-10-17).
    ///
    /// Every poll on our map, each Building unit and airbase landing/service point not yet handled gets a footprint:
    /// the unit's mesh bounds in its own axes (so a turned building gets a turned rectangle), grown by a margin.
    /// Every tree whose trunk stands inside is removed with a stock clear: one tiny DetailBlast exactly on that
    /// tree, queued on the DetailRenderer's BlastManager. The DetailRenderer then runs TreeRenderer.ClearTrees
    /// for it in its next LateUpdate. The radius stays far below the blast map's draw threshold
    /// (BlastManager.DrawBlast skips radius &lt; worldSizeToResolution / 2), so nothing gets scorched, and an exact
    /// position clears only that tree whatever distance the clear shader uses. Rocks inside a footprint are
    /// removed through TerrainLabScatter's Runner.ClearWhere, if that plugin is loaded.
    ///
    /// Purely local and deterministic (every peer runs the same clears from the same units); the headless server
    /// has no DetailRenderer and does nothing.
    /// </summary>
    static class TreeClear
    {
        static BepInEx.Logging.ManualLogSource Log => Plugin.Log;

        struct Footprint
        {
            public Vector3 Center;      // world
            public Vector3 Right, Fwd;  // world, horizontal unit axes
            public float HalfW, HalfL;  // m, without margin
            public string What;
            public float Margin;        // m round the rectangle for trees
        }

        static int rendererId;                       // DetailRenderer instance the state below belongs to
        static readonly HashSet<int> done = new HashSet<int>();
        static readonly HashSet<int> donePoints = new HashSet<int>();
        static Vector3[] trees;                      // global positions of every tree of every TreeRenderer
        static bool[] gone;
        static Dictionary<long, List<int>> grid;
        const float Cell = 64f;
        static int totalTrees, totalRocks, totalUnits, errors;
        static MethodInfo clearRocks;
        static bool rocksLooked;

        static readonly FieldInfo blastManagerField = AccessTools.Field(typeof(DetailRenderer), "blastManager");
        static readonly FieldInfo treeRenderersField = AccessTools.Field(typeof(DetailRenderer), "treeRenderers");
        static readonly FieldInfo clearMulField = AccessTools.Field(typeof(TreeRenderer), "clearTreeRadiusMultiplier");
        static readonly FieldInfo servicePointsField = AccessTools.Field(typeof(Airbase), "servicePoints");

        /// Called from Runner.Poll while a mission runs on our map.
        internal static void Tick()
        {
            if (!Plugin.TreeClearEnabled.Value || GameManager.IsHeadless) return;
            try { Run(); }
            catch (Exception e)
            {
                if (errors++ < 5) Log.LogWarning($"[TLGameplay] tree clear failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        static void Run()
        {
            var dr = SceneSingleton<DetailRenderer>.i;
            if (dr == null || !dr.isActiveAndEnabled) return;
            if (dr.GetInstanceID() != rendererId)
            {
                if (!LoadTrees(dr)) return;
                rendererId = dr.GetInstanceID();
                done.Clear();
                donePoints.Clear();
                totalTrees = totalRocks = totalUnits = 0;
            }
            var blast = blastManagerField?.GetValue(dr) as BlastManager;
            if (blast == null) return;

            var feet = new List<Footprint>();
            foreach (var u in UnitRegistry.allUnits)
            {
                if (!(u is Building b) || b == null || !done.Add(b.GetInstanceID())) continue;
                if (BuildingFootprint(b, out var f)) feet.Add(f);
            }
            foreach (var ab in FactionRegistry.airbaseLookup.Values)
            {
                if (ab == null) continue;
                if (ab.verticalLandingPoints != null)
                    foreach (var v in ab.verticalLandingPoints)
                        if (v?.point != null) AddPoint(feet, v.point, 0.5f * v.size, $"{ab.name} landing point");
                if (servicePointsField?.GetValue(ab) is Transform[] sps)
                    foreach (var t in sps)
                        if (t != null) AddPoint(feet, t, Plugin.TreeClearServiceRadius.Value, $"{ab.name} service point");
            }
            if (feet.Count == 0) return;

            float r = ClearRadius(dr);
            int cleared = 0, hitUnits = 0;
            var origin = Datum.originPosition;
            foreach (var f in feet)
            {
                int n = 0;
                // Footprint in global coordinates: world minus the floating origin.
                var c = f.Center - origin;
                float margin = f.Margin;
                float reach = Mathf.Sqrt(f.HalfW * f.HalfW + f.HalfL * f.HalfL) + margin;
                int x0 = Mathf.FloorToInt((c.x - reach) / Cell), x1 = Mathf.FloorToInt((c.x + reach) / Cell);
                int z0 = Mathf.FloorToInt((c.z - reach) / Cell), z1 = Mathf.FloorToInt((c.z + reach) / Cell);
                for (int gx = x0; gx <= x1; gx++)
                    for (int gz = z0; gz <= z1; gz++)
                    {
                        if (!grid.TryGetValue(Key(gx, gz), out var list)) continue;
                        foreach (int i in list)
                        {
                            if (gone[i] || !Inside(f, c, trees[i], margin)) continue;
                            gone[i] = true;
                            var p = trees[i];
                            blast.Blasts.Enqueue(new BlastManager.DetailBlast(new GlobalPosition(p.x, p.y, p.z), r));
                            n++;
                        }
                    }
                if (n > 0)
                {
                    hitUnits++;
                    cleared += n;
                    if (Plugin.TreeClearLog.Value)
                        Log.LogInfo($"[TLGameplay] tree clear: {n} tree(s) off {f.What} at global ({c.x:F0}, {c.z:F0}), {2 * f.HalfW:F0} x {2 * f.HalfL:F0} m");
                }
            }

            int rocks = ClearRocks(feet, Plugin.TreeClearRockMargin.Value);
            totalTrees += cleared;
            totalRocks += rocks;
            totalUnits += feet.Count;
            if (cleared > 0 || rocks > 0)
                Log.LogInfo($"[TLGameplay] tree clear census: {feet.Count} new footprints, {cleared} trees on {hitUnits} of them and " +
                            $"{rocks} rocks removed; session total {totalUnits} footprints, {totalTrees} trees, {totalRocks} rocks " +
                            $"(blast radius {r:F2})");
        }

        static void AddPoint(List<Footprint> feet, Transform t, float radius, string what)
        {
            // Each point transform is handled once, where it stands at the time (a carrier's points are at sea anyway).
            if (!donePoints.Add(t.GetInstanceID())) return;
            feet.Add(new Footprint { Center = t.position, Right = Vector3.right, Fwd = Vector3.forward, HalfW = radius, HalfL = radius, What = what,
                                     Margin = Plugin.TreeClearMargin.Value });
        }

        static bool Inside(in Footprint f, Vector3 c, Vector3 p, float margin)
        {
            float dx = p.x - c.x, dz = p.z - c.z;
            float a = Mathf.Abs(dx * f.Right.x + dz * f.Right.z);
            float b = Mathf.Abs(dx * f.Fwd.x + dz * f.Fwd.z);
            return a <= f.HalfW + margin && b <= f.HalfL + margin;
        }

        /// Rectangle of the building's meshes in the building's own horizontal axes.
        static bool BuildingFootprint(Building b, out Footprint f)
        {
            f = default;
            var t = b.transform;
            var fwd = t.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { fwd = t.up; fwd.y = 0f; }
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = new Vector3(fwd.z, 0f, -fwd.x);
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            bool any = false;
            var o = t.position;
            foreach (var mf in b.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null) continue;
                var mb = mf.sharedMesh.bounds;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))) - o;
                    float a = p.x * right.x + p.z * right.z, c = p.x * fwd.x + p.z * fwd.z;
                    minA = Mathf.Min(minA, a); maxA = Mathf.Max(maxA, a);
                    minB = Mathf.Min(minB, c); maxB = Mathf.Max(maxB, c);
                    any = true;
                }
            }
            string type = b.definition != null ? b.definition.jsonKey : b.name;
            if (!any) return false;
            float hw = 0.5f * (maxA - minA), hl = 0.5f * (maxB - minB);
            float cap = Plugin.TreeClearMaxHalfSize.Value;
            if (hw > cap || hl > cap)
            {
                Log.LogInfo($"[TLGameplay] tree clear: {type} '{b.unitName}' mesh footprint {2 * hw:F0} x {2 * hl:F0} m is over the cap, skipped");
                return false;
            }
            f.Center = o + right * (0.5f * (minA + maxA)) + fwd * (0.5f * (minB + maxB));
            f.Right = right; f.Fwd = fwd; f.HalfW = hw; f.HalfL = hl;
            f.What = $"{type} '{b.unitName}'";
            f.Margin = type.IndexOf("helipad", StringComparison.OrdinalIgnoreCase) >= 0 ? Plugin.TreeClearHelipadMargin.Value : Plugin.TreeClearMargin.Value;
            return true;
        }

        /// DetailBlast radius that clears a tree only where it stands: TreeRenderer.ClearTrees multiplies the
        /// radius by clearTreeRadiusMultiplier (stock 0.5); aim for a 0.5 m clear on the renderer that clears most.
        static float ClearRadius(DetailRenderer dr)
        {
            float mul = 0.5f;
            if (treeRenderersField?.GetValue(dr) is TreeRenderer[] trs && trs.Length > 0 && clearMulField != null)
                mul = trs.Where(t => t != null).Select(t => (float)clearMulField.GetValue(t)).DefaultIfEmpty(0.5f).Max();
            return 0.5f / Mathf.Max(mul, 0.01f);
        }

        static bool LoadTrees(DetailRenderer dr)
        {
            if (!(treeRenderersField?.GetValue(dr) is TreeRenderer[] trs)) return false;
            var all = new List<Vector3>();
            foreach (var tr in trs)
            {
                if (tr == null || tr.PositionData == null) continue;
                var bytes = tr.PositionData.bytes;
                int n = bytes.Length / 12;
                for (int i = 0; i < n; i++)
                    all.Add(new Vector3(BitConverter.ToSingle(bytes, i * 12), BitConverter.ToSingle(bytes, i * 12 + 4),
                                        BitConverter.ToSingle(bytes, i * 12 + 8)));
            }
            trees = all.ToArray();
            gone = new bool[trees.Length];
            grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < trees.Length; i++)
            {
                long k = Key(Mathf.FloorToInt(trees[i].x / Cell), Mathf.FloorToInt(trees[i].z / Cell));
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                list.Add(i);
            }
            Log.LogInfo($"[TLGameplay] tree clear: {trees.Length} tree positions from {trs.Length} TreeRenderer(s)");
            return true;
        }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        static int ClearRocks(List<Footprint> feet, float margin)
        {
            if (!rocksLooked)
            {
                rocksLooked = true;
                var type = AccessTools.TypeByName("TerrainLab.Scatter.Runner");
                clearRocks = type != null ? AccessTools.Method(type, "ClearWhere") : null;
                Log.LogInfo($"[TLGameplay] tree clear: TerrainLabScatter rock clearing {(clearRocks != null ? "available" : "not available (older or no TerrainLabScatter)")}");
            }
            if (clearRocks == null) return 0;
            // Bucket the footprints by world cell so each rock tests only the few near it.
            const float maxRock = 30f;
            var cells = new Dictionary<long, List<int>>();
            for (int k = 0; k < feet.Count; k++)
            {
                var f = feet[k];
                float reach = Mathf.Sqrt(f.HalfW * f.HalfW + f.HalfL * f.HalfL) + margin + maxRock;
                for (int gx = Mathf.FloorToInt((f.Center.x - reach) / Cell); gx <= Mathf.FloorToInt((f.Center.x + reach) / Cell); gx++)
                    for (int gz = Mathf.FloorToInt((f.Center.z - reach) / Cell); gz <= Mathf.FloorToInt((f.Center.z + reach) / Cell); gz++)
                    {
                        long key = Key(gx, gz);
                        if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<int>();
                        l.Add(k);
                    }
            }
            Func<Vector3, float, bool> inside = (p, rad) =>
            {
                if (!cells.TryGetValue(Key(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell)), out var l)) return false;
                foreach (int k in l)
                    if (Inside(feet[k], feet[k].Center, p, margin + Mathf.Min(rad, maxRock))) return true;
                return false;
            };
            return (int)clearRocks.Invoke(null, new object[] { inside });
        }
    }
}
