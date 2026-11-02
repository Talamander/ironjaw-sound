using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using RoadPathfinding;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Compatibility fixes for the shipped Ironjaw Sound bridge meshes and AI road graph.
    /// Decks clear the original terrain; short added approaches blend back to the existing land roads.
    /// The map bundle and terrain meshes remain unchanged. Mesh signatures protect rebuilt maps.
    /// </summary>
    static class BridgeFix
    {
        const string DataResource = "ironjaw_bridges_1_0_3.txt";
        static readonly string[] HiddenDecks = { "Bridge_52", "Bridge_53" };
        static MapSettings patchedMap;
        static readonly Dictionary<Collider, StationGrid> deckColliders = new Dictionary<Collider, StationGrid>();
        static Collider approachCollider;

        // Road end points (map X, Z) in the 1.0.3 bundle, either direction.
        struct RoadSig { public string Label; public Vector2 A, B; }
        static readonly RoadSig[] RemoveRoads =
        {
            new RoadSig { Label = "road 61 (Fenwick islet duplicate causeway, Bridge_52)", A = new Vector2(-120f, -3000f), B = new Vector2(200f, -3000f) },
            new RoadSig { Label = "road 62 (Fenwick islet duplicate causeway, Bridge_53)", A = new Vector2(1400f, -1720f), B = new Vector2(1560f, -1880f) },
            new RoadSig { Label = "road 60 (Halvard Sound east end, the 90-degree Z-bend on the deck)", A = new Vector2(-566.7f, -3364.6f), B = new Vector2(-440.0f, -3400.0f) },
        };
        static readonly Vector2 Road145Start = new Vector2(-566.7f, -3364.6f);
        static readonly Vector2 Road145End = new Vector2(-425.2f, -3315.7f);
        // Bridge_11's corner parapet at the Halvard fork stands across the lifted Bridge_46: flatten Bridge_11 vertices
        // that are more than 0.3 m above its deck top inside Bridge_46's lanes over its first metres.
        const string HalvardDeck = "Bridge_11";
        const string HalvardRamp = "Bridge_46";
        const float HalvardClearLength = 40f, HalvardClearHalfWidth = 5.8f;

        static bool Ours(MapSettings ms) =>
            Plugin.BridgesEnabled != null && Plugin.BridgesEnabled.Value && ms != null && MapPatcher.PrefixOf(ms) == Plugin.MapPrefix.Value;

        static bool Near(GlobalPosition p, Vector2 q, float tol) => (new Vector2(p.x, p.z) - q).sqrMagnitude <= tol * tol;

        static bool Matches(Road r, Vector2 a, Vector2 b, float tol)
        {
            if (r == null || r.points == null || r.points.Count < 2) return false;
            var s = r.points[0];
            var e = r.points[r.points.Count - 1];
            return (Near(s, a, tol) && Near(e, b, tol)) || (Near(s, b, tol) && Near(e, a, tol));
        }

        /// <summary>Called on the road network copy LevelInfo.ApplyMapSettings is about to build the graph from.</summary>
        internal static void PatchRoads(MapSettings ms, RoadNetwork net)
        {
            if (!Ours(ms) || ms != patchedMap || net == null || net.roads == null) return;
            SyncRoadHeights(net);
            int before = net.roads.Count;
            int removed = 0;
            foreach (var sig in RemoveRoads)
            {
                int i = net.roads.FindIndex(r => Matches(r, sig.A, sig.B, 3f));
                if (i < 0) { Plugin.Log.LogWarning($"[bridges] {sig.Label} not found; road graph left as it is for this one."); continue; }
                net.roads.RemoveAt(i);
                removed++;
                Plugin.Log.LogInfo($"[bridges] removed {sig.Label} from the AI road graph.");
            }
            if (!net.roads.Exists(r => Matches(r, Road145Start, Road145End, 3f)))
                Plugin.Log.LogWarning("[bridges] road 145 (Halvard Sound east end) not found; the Halvard bridge now has no way east.");
            Plugin.Log.LogInfo($"[bridges] road graph: {before} roads in, {net.roads.Count} out ({removed} removed).");
        }

        // ------------------------------------------------------------------ decks
        class Deck
        {
            public string Name;
            public int VisualVerts, ColliderVerts;
            public bool MoveAll;   // lifted deck: every vertex moves, no pier feet kept
            public Vector2[] P;
            public float[] Old, Dy;
        }

        static List<Deck> LoadDecks()
        {
            var asm = typeof(BridgeFix).Assembly;
            string res = null;
            foreach (var n in asm.GetManifestResourceNames())
                if (n.EndsWith(DataResource, StringComparison.Ordinal)) { res = n; break; }
            if (res == null) { Plugin.Log.LogError($"[bridges] embedded {DataResource} missing from the DLL."); return null; }
            var decks = new List<Deck>();
            using (var st = asm.GetManifestResourceStream(res))
            using (var rd = new StreamReader(st))
            {
                Deck cur = null;
                int k = 0;
                string line;
                var ci = CultureInfo.InvariantCulture;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Split(' ');
                    if (f[0] == "bridge")
                    {
                        int n = int.Parse(f[2], ci);
                        cur = new Deck { Name = f[1], VisualVerts = int.Parse(f[3], ci), ColliderVerts = int.Parse(f[4], ci),
                                         MoveAll = f.Length > 5 && f[5] == "all",
                                         P = new Vector2[n], Old = new float[n], Dy = new float[n] };
                        decks.Add(cur);
                        k = 0;
                        continue;
                    }
                    if (cur == null || k >= cur.P.Length) continue;
                    cur.P[k] = new Vector2(float.Parse(f[0], ci), float.Parse(f[1], ci));
                    cur.Old[k] = float.Parse(f[2], ci);
                    cur.Dy[k] = float.Parse(f[3], ci);
                    k++;
                }
            }
            return decks;
        }

        internal static void ApplyDecks(MapContext ctx)
        {
            patchedMap = null;
            deckColliders.Clear();
            approachCollider = null;
            var root = ctx.Root;
            var bridges = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.parent != null && t.parent.name == "Bridges" && t.name.StartsWith("Bridge_", StringComparison.Ordinal))
                    bridges[t.name] = t;
            if (bridges.Count == 0) { Plugin.Log.LogWarning("[bridges] no Bridges/Bridge_NN objects under the map; nothing to do."); return; }

            var decks = LoadDecks();
            if (decks == null) return;
            // Validate the whole shipped layout before changing either geometry or graph.
            foreach (var d in decks)
            {
                if (!bridges.TryGetValue(d.Name, out var t)) return;
                var visual = t.GetComponent<MeshFilter>()?.sharedMesh;
                var collider = t.GetComponent<MeshCollider>()?.sharedMesh;
                if (visual == null || collider == null || !visual.isReadable || !collider.isReadable ||
                    visual.vertexCount != d.VisualVerts || collider.vertexCount != d.ColliderVerts)
                {
                    Plugin.Log.LogWarning($"[bridges] {d.Name}: shipped mesh signature does not match; bridge fixes skipped.");
                    return;
                }
            }

            foreach (var name in HiddenDecks)
            {
                if (!bridges.TryGetValue(name, out var t)) { Plugin.Log.LogWarning($"[bridges] {name} not found, not hidden."); continue; }
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (var c in t.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                Plugin.Log.LogInfo($"[bridges] hid {name} (renderer and collider off).");
            }

            var copies = new List<Mesh>();
            var applied = new List<KeyValuePair<Deck, StationGrid>>();
            int done = 0, skipped = 0;
            foreach (var d in decks)
            {
                try
                {
                    if (!bridges.TryGetValue(d.Name, out var t)) { Plugin.Log.LogWarning($"[bridges] {d.Name} not in the map, skipped."); skipped++; continue; }
                    var mf = t.GetComponent<MeshFilter>();
                    var mc = t.GetComponent<MeshCollider>();
                    Mesh vis = mf != null ? mf.sharedMesh : null;
                    Mesh col = mc != null ? mc.sharedMesh : null;
                    if (vis == null || vis.vertexCount != d.VisualVerts || (col != null && col != vis && col.vertexCount != d.ColliderVerts))
                    {
                        Plugin.Log.LogWarning($"[bridges] {d.Name}: mesh is not the 1.0.3 one (visual {vis?.vertexCount} want {d.VisualVerts}, " +
                                              $"collider {col?.vertexCount} want {d.ColliderVerts}); left as it is.");
                        skipped++;
                        continue;
                    }
                    if (!vis.isReadable || (col != null && !col.isReadable))
                    {
                        Plugin.Log.LogWarning($"[bridges] {d.Name}: mesh not readable; left as it is.");
                        skipped++;
                        continue;
                    }
                    var toMap = root.worldToLocalMatrix * t.localToWorldMatrix;
                    var fromMap = toMap.inverse;
                    var grid = new StationGrid(d);
                    var newVis = Regrade(vis, d, grid, toMap, fromMap, out float vMin, out float vMax, out int vMoved, out int vFeet);
                    copies.Add(newVis);
                    mf.sharedMesh = newVis;
                    string colNote = "no collider";
                    if (col != null)
                    {
                        Mesh newCol = col == vis ? newVis : Regrade(col, d, grid, toMap, fromMap, out _, out _, out int cMoved, out _);
                        if (newCol != newVis) copies.Add(newCol);
                        mc.sharedMesh = null;      // re-cook the PhysX mesh
                        mc.sharedMesh = newCol;
                        deckColliders[mc] = grid;
                        colNote = "collider re-cooked";
                    }
                    done++;
                    applied.Add(new KeyValuePair<Deck, StationGrid>(d, grid));
                    Plugin.Log.LogInfo($"[bridges] {d.Name}: moved {vMoved} vertices by {vMin:F2}..{vMax:F2} m, {vFeet} pier-foot vertices kept, {colNote}.");
                }
                catch (Exception e)
                {
                    skipped++;
                    Plugin.Log.LogError($"[bridges] {d.Name} failed: {e}");
                }
            }
            Plugin.Log.LogInfo($"[bridges] decks re-graded: {done}, skipped: {skipped}.");
            try { ClearHalvardParapet(root, bridges, applied); }
            catch (Exception e) { Plugin.Log.LogError($"[bridges] Halvard parapet step failed: {e}"); }
            if (skipped == 0)
            {
                try { AddApproaches(ctx, bridges, copies); Physics.SyncTransforms(); patchedMap = ctx.Map; }
                catch (Exception e) { Plugin.Log.LogError($"[bridges] approach step failed: {e}"); }
            }
            else Plugin.Log.LogWarning("[bridges] approach meshes skipped because the shipped deck signatures did not all match.");
            if (copies.Count > 0)
                ctx.Map.BeforeDestroy += () => { foreach (var m in copies) if (m != null) UnityEngine.Object.Destroy(m); };
        }

        /// <summary>Flatten the part of Bridge_11's corner parapet that stands inside the lifted Bridge_46's lanes.</summary>
        static void ClearHalvardParapet(Transform root, Dictionary<string, Transform> bridges, List<KeyValuePair<Deck, StationGrid>> applied)
        {
            StationGrid g11 = null, g46 = null;
            foreach (var kv in applied)
            {
                if (kv.Key.Name == HalvardDeck) g11 = kv.Value;
                if (kv.Key.Name == HalvardRamp) g46 = kv.Value;
            }
            if (g11 == null || g46 == null || !bridges.TryGetValue(HalvardDeck, out var t))
            {
                Plugin.Log.LogWarning($"[bridges] Halvard parapet left as it is ({HalvardDeck} regraded: {g11 != null}, {HalvardRamp} lifted: {g46 != null}).");
                return;
            }
            var toMap = root.worldToLocalMatrix * t.localToWorldMatrix;
            var fromMap = toMap.inverse;
            var mf = t.GetComponent<MeshFilter>();
            var mc = t.GetComponent<MeshCollider>();
            int total = 0;
            var seen = new HashSet<Mesh>();
            foreach (var m in new[] { mf != null ? mf.sharedMesh : null, mc != null ? mc.sharedMesh : null })
            {
                if (m == null || !seen.Add(m)) continue;
                var v = m.vertices;
                int n = 0;
                for (int i = 0; i < v.Length; i++)
                {
                    var w = toMap.MultiplyPoint3x4(v[i]);
                    var q = new Vector2(w.x, w.z);
                    if (!g46.Project(q, out float s, out float lat, out _, out bool inRange) || !inRange) continue;
                    if (s > HalvardClearLength || lat > HalvardClearHalfWidth) continue;
                    if (!g11.Lookup(q, out float old, out float dy)) continue;
                    float top = old + dy;
                    if (w.y <= top + 0.3f || w.y > top + 2f) continue;
                    w.y = top;
                    v[i] = fromMap.MultiplyPoint3x4(w);
                    n++;
                }
                if (n == 0) continue;
                m.vertices = v;
                m.RecalculateNormals();
                m.RecalculateBounds();
                total += n;
            }
            if (mc != null && total > 0) { var cm = mc.sharedMesh; mc.sharedMesh = null; mc.sharedMesh = cm; }
            Plugin.Log.LogInfo($"[bridges] Halvard fork: flattened {total} {HalvardDeck} parapet vertices standing across {HalvardRamp}.");
        }

        // ------------------------------------------------------------------ approaches and matching AI heights
        class ApproachRoad
        {
            public Vector2 A, B;
            public List<GlobalPosition> Points = new List<GlobalPosition>();
        }
        class Approaches
        {
            public Vector3[] Vertices;
            public Vector2[] UV;
            public int[] Triangles;
            public List<ApproachRoad> Roads = new List<ApproachRoad>();
        }

        static Approaches LoadApproaches()
        {
            var result = new Approaches();
            using (var stream = typeof(BridgeFix).Assembly.GetManifestResourceStream("ironjaw_approaches_1_1_1.txt"))
            using (var reader = new StreamReader(stream ?? throw new InvalidDataException("Missing bridge approaches")))
            {
                string line;
                var ci = CultureInfo.InvariantCulture;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Split(' ');
                    if (f[0] == "mesh")
                    {
                        result.Vertices = new Vector3[int.Parse(f[1], ci)];
                        result.UV = new Vector2[result.Vertices.Length];
                        result.Triangles = new int[int.Parse(f[2], ci) * 3];
                        for (int i = 0; i < result.Vertices.Length; i++)
                        {
                            var v = reader.ReadLine().Split(' ');
                            result.Vertices[i] = new Vector3(float.Parse(v[0], ci), float.Parse(v[1], ci), float.Parse(v[2], ci));
                            result.UV[i] = new Vector2(float.Parse(v[3], ci), float.Parse(v[4], ci));
                        }
                        for (int i = 0; i < result.Triangles.Length; i += 3)
                        {
                            var t = reader.ReadLine().Split(' ');
                            for (int j = 0; j < 3; j++) result.Triangles[i + j] = int.Parse(t[j], ci);
                        }
                    }
                    else if (f[0] == "road")
                    {
                        var road = new ApproachRoad {
                            A = new Vector2(float.Parse(f[2], ci), float.Parse(f[3], ci)),
                            B = new Vector2(float.Parse(f[4], ci), float.Parse(f[5], ci)) };
                        for (int i = 0, n = int.Parse(f[1], ci); i < n; i++)
                        {
                            var v = reader.ReadLine().Split(' ');
                            road.Points.Add(new GlobalPosition(float.Parse(v[0], ci), float.Parse(v[1], ci), float.Parse(v[2], ci)));
                        }
                        result.Roads.Add(road);
                    }
                }
            }
            return result;
        }

        static void SyncRoadHeights(RoadNetwork net)
        {
            var decks = LoadDecks();
            if (decks == null) return;
            var grids = new List<StationGrid>();
            foreach (var deck in decks) grids.Add(new StationGrid(deck));
            var approaches = LoadApproaches();
            int bridges = 0, land = 0;
            foreach (var road in net.roads)
            {
                if (road.IsBridge())
                {
                    for (int i = 0; i < road.points.Count; i++)
                    {
                        var p = road.points[i];
                        float nearest = 12f, height = p.y;
                        foreach (var grid in grids)
                            if (grid.Project(new Vector2(p.x, p.z), out _, out float lat, out float top, out _) && lat < nearest)
                            { nearest = lat; height = top; }
                        road.points[i] = new GlobalPosition(p.x, height, p.z);
                    }
                    bridges++;
                }
                else
                {
                    foreach (var profile in approaches.Roads)
                    {
                        if (!Matches(road, profile.A, profile.B, 3f)) continue;
                        bool forward = Near(road.points[0], profile.A, 3f);
                        road.points = new List<GlobalPosition>(profile.Points);
                        if (!forward) road.points.Reverse();
                        land++;
                        break;
                    }
                }
                road.CalcLength();
                road.UpdateBB();
            }
            Plugin.Log.LogInfo($"[bridges] steering heights updated on {bridges} bridge roads and {land} approaches.");
        }

        static void AddApproaches(MapContext ctx, Dictionary<string, Transform> bridges, List<Mesh> copies)
        {
            var data = LoadApproaches();
            var source = bridges[HalvardDeck];
            var mesh = new Mesh { name = "Ironjaw bridge approaches", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = data.Vertices;
            mesh.triangles = data.Triangles;
            mesh.uv = data.UV;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            copies.Add(mesh);
            var go = new GameObject("TerrainLab bridge approaches");
            go.transform.SetParent(ctx.Root, false);
            go.layer = source.gameObject.layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // The shipped deck's first material is concrete; its second is the marked asphalt surface.
            go.AddComponent<MeshRenderer>().sharedMaterial = source.GetComponent<MeshRenderer>().sharedMaterials[1];
            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.sharedMaterial = source.GetComponent<MeshCollider>().sharedMaterial;
            approachCollider = collider;
            Plugin.Log.LogInfo($"[bridges] added {data.Triangles.Length / 3} approach triangles; terrain unchanged.");
        }

        // Mission vehicles saved at the old surface must not start inside a raised deck or approach.
        // Adjust only our changed colliders, and never move a vehicle already above the new surface down.
        internal static void LiftVehicleSpawn(GameObject prefab, ref GlobalPosition position)
        {
            if (!Ours(patchedMap) || prefab == null) return;
            var vehicle = prefab.GetComponent<GroundVehicle>();
            if (vehicle == null || vehicle.definition == null) return;
            var point = position.ToLocalPosition();
            if (!Physics.Raycast(point + Vector3.up * 100f, Vector3.down, out var hit, 200f, PhysicsLayers.StaticsMask)) return;
            float oldSurface;
            if (deckColliders.TryGetValue(hit.collider, out var grid))
            {
                var q = new Vector2(position.x, position.z);
                if (!grid.Project(q, out _, out float lat, out _, out bool inside) || !inside || lat > 5f ||
                    !grid.Lookup(q, out oldSurface, out _)) return;
            }
            else if (hit.collider == approachCollider)
            {
                oldSurface = float.NegativeInfinity;
                foreach (var below in Physics.RaycastAll(point + Vector3.up * 100f, Vector3.down, 200f, PhysicsLayers.StaticsMask))
                    if (below.collider != approachCollider && MapPatcher.CurrentCtx.TileColliders.Contains(below.collider as MeshCollider))
                        oldSurface = Mathf.Max(oldSurface, below.point.GlobalY());
                if (float.IsNegativeInfinity(oldSurface)) return;
            }
            else return;
            float offset = vehicle.definition.spawnOffset.y;
            float y = hit.point.GlobalY() + offset + 0.1f;
            if (position.y < oldSurface - 0.5f || position.y >= y) return;
            Plugin.Log.LogInfo($"[bridges] lifted {vehicle.definition.jsonKey} spawn {y - position.y:F2} m onto repaired road.");
            position = new GlobalPosition(position.x, y, position.z);
        }

        static Mesh Regrade(Mesh src, Deck d, StationGrid grid, Matrix4x4 toMap, Matrix4x4 fromMap,
                            out float dyMin, out float dyMax, out int moved, out int feet)
        {
            var m = UnityEngine.Object.Instantiate(src);
            m.name = src.name + " (TerrainLab regraded)";
            var v = m.vertices;
            dyMin = float.MaxValue; dyMax = float.MinValue; moved = 0; feet = 0;
            for (int i = 0; i < v.Length; i++)
            {
                var w = toMap.MultiplyPoint3x4(v[i]);
                if (!grid.Lookup(new Vector2(w.x, w.z), out float old, out float dy)) continue;
                if (Mathf.Abs(dy) < 0.005f) continue;
                float y = w.y;
                if (!d.MoveAll && y < old - 1.5f)
                {
                    // pier foot: stays on the ground, unless the deck now comes down past it
                    float cap = old + dy - 1.5f;
                    if (y <= cap) continue;
                    w.y = cap;
                    feet++;
                }
                else
                {
                    w.y = y + dy;
                    if (dy < dyMin) dyMin = dy;
                    if (dy > dyMax) dyMax = dy;
                    moved++;
                }
                v[i] = fromMap.MultiplyPoint3x4(w);
            }
            if (moved == 0) { dyMin = dyMax = 0f; }
            m.vertices = v;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Nearest point on a deck centreline (stations every 2 m) and the interpolated shipped top / move there.</summary>
        class StationGrid
        {
            const float Cell = 16f;
            const float MaxDist = 20f;
            readonly Deck d;
            readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            readonly float[] S;            // distance along the centreline at each station
            readonly Vector2 lo, hi;       // centreline bounds

            static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

            public StationGrid(Deck deck)
            {
                d = deck;
                S = new float[d.P.Length];
                lo = hi = d.P[0];
                for (int i = 1; i < d.P.Length; i++)
                {
                    S[i] = S[i - 1] + (d.P[i] - d.P[i - 1]).magnitude;
                    lo = Vector2.Min(lo, d.P[i]);
                    hi = Vector2.Max(hi, d.P[i]);
                }
                for (int i = 0; i < d.P.Length; i++)
                {
                    long k = Key(Mathf.FloorToInt(d.P[i].x / Cell), Mathf.FloorToInt(d.P[i].y / Cell));
                    if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>();
                    l.Add(i);
                }
            }

            public bool Overlaps(Vector2 bmin, Vector2 bmax, float margin) =>
                bmax.x >= lo.x - margin && bmin.x <= hi.x + margin && bmax.y >= lo.y - margin && bmin.y <= hi.y + margin;

            /// <summary>Distance along the deck, distance from the centreline, new deck top there, and whether q is
            /// between the deck ends. False when q is more than about 20 m from every station.</summary>
            public bool Project(Vector2 q, out float s, out float lat, out float top, out bool inRange)
            {
                s = lat = top = 0f; inRange = false;
                int best = Nearest(q, out float bd);
                if (best < 0 || bd > MaxDist * MaxDist) return false;
                float segBest = float.MaxValue;
                int n = d.P.Length;
                s = S[best]; lat = Mathf.Sqrt(bd); top = d.Old[best] + d.Dy[best]; inRange = true;
                for (int k = best - 1; k <= best; k++)
                {
                    if (k < 0 || k + 1 >= n) continue;
                    Vector2 a = d.P[k], b = d.P[k + 1], ab = b - a;
                    float L2 = ab.sqrMagnitude;
                    if (L2 < 1e-6f) continue;
                    float u = Vector2.Dot(q - a, ab) / L2;
                    bool outside = (k == 0 && u < 0f) || (k == n - 2 && u > 1f);
                    float uc = Mathf.Clamp01(u);
                    float dist = (a + ab * uc - q).sqrMagnitude;
                    if (dist < segBest)
                    {
                        segBest = dist;
                        s = Mathf.Lerp(S[k], S[k + 1], uc);
                        lat = Mathf.Sqrt(dist);
                        top = Mathf.Lerp(d.Old[k] + d.Dy[k], d.Old[k + 1] + d.Dy[k + 1], uc);
                        inRange = !outside;
                    }
                }
                return true;
            }

            int Nearest(Vector2 q, out float bd)
            {
                int cx = Mathf.FloorToInt(q.x / Cell), cz = Mathf.FloorToInt(q.y / Cell);
                int best = -1;
                bd = float.MaxValue;
                for (int dx = -2; dx <= 2; dx++)
                    for (int dz = -2; dz <= 2; dz++)
                        if (cells.TryGetValue(Key(cx + dx, cz + dz), out var l))
                            foreach (int i in l)
                            {
                                float dd = (d.P[i] - q).sqrMagnitude;
                                if (dd < bd) { bd = dd; best = i; }
                            }
                return best;
            }

            public bool Lookup(Vector2 q, out float old, out float dy)
            {
                old = dy = 0f;
                int cx = Mathf.FloorToInt(q.x / Cell), cz = Mathf.FloorToInt(q.y / Cell);
                int best = -1;
                float bd = float.MaxValue;
                for (int dx = -2; dx <= 2; dx++)
                    for (int dz = -2; dz <= 2; dz++)
                        if (cells.TryGetValue(Key(cx + dx, cz + dz), out var l))
                            foreach (int i in l)
                            {
                                float dd = (d.P[i] - q).sqrMagnitude;
                                if (dd < bd) { bd = dd; best = i; }
                            }
                if (best < 0 || bd > MaxDist * MaxDist) return false;
                // project onto the two segments next to the nearest station; take the closer one
                float segBest = float.MaxValue;
                old = d.Old[best]; dy = d.Dy[best];
                for (int s = best - 1; s <= best; s++)
                {
                    if (s < 0 || s + 1 >= d.P.Length) continue;
                    Vector2 a = d.P[s], b = d.P[s + 1], ab = b - a;
                    float L2 = ab.sqrMagnitude;
                    if (L2 < 1e-6f) continue;
                    float u = Mathf.Clamp01(Vector2.Dot(q - a, ab) / L2);
                    float dist = (a + ab * u - q).sqrMagnitude;
                    if (dist < segBest)
                    {
                        segBest = dist;
                        old = Mathf.Lerp(d.Old[s], d.Old[s + 1], u);
                        dy = Mathf.Lerp(d.Dy[s], d.Dy[s + 1], u);
                    }
                }
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(MapSettings), nameof(MapSettings.CreateRoadNetwork))]
    static class CreateRoadNetworkPatch
    {
        static void Postfix(MapSettings __instance, RoadNetwork __result)
        {
            try { BridgeFix.PatchRoads(__instance, __result); }
            catch (Exception e) { Plugin.Log.LogError($"[bridges] road graph patch failed: {e}"); }
        }
    }

    [HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnVehicle))]
    static class BridgeVehicleSpawnPatch
    {
        static void Prefix(GameObject prefab, ref GlobalPosition globalPosition)
        {
            try { BridgeFix.LiftVehicleSpawn(prefab, ref globalPosition); }
            catch (Exception e) { Plugin.Log.LogError($"[bridges] vehicle spawn height check failed: {e}"); }
        }
    }
}
