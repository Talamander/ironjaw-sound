using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace TerrainLab.Scatter
{
    /// <summary>
    /// Lives on a hidden DontDestroyOnLoad object. Loads rocks when our map loads, snaps them to the terrain,
    /// adds colliders, and every frame draws the visible rocks with Graphics.DrawMeshInstanced.
    /// </summary>
    public class Runner : MonoBehaviour
    {
        internal static Runner Instance;
        const int Batch = 1023;
        const float CellM = 512f;

        // assets (kept loaded across maps once found)
        AssetBundle bundle;
        Mesh[,] meshes;            // [type, lod]
        Material[] mats;           // [variant] 0 bare, 1 snow
        int[,] tris;

        // current map
        Transform root;
        RockData data;
        Matrix4x4[] local;         // per rock, map-root space
        bool[] keep;               // after the density setting
        float[] radius;            // bounding radius (m) per rock for culling
        List<int>[] cells;
        Bounds[] cellBounds;
        int cellsPerSide;
        int drawLayer;
        GameObject colliderRoot;

        // draw lists per (type, variant, lod)
        class DrawList
        {
            public Mesh Mesh;
            public Material Mat;
            public ShadowCastingMode Shadow;
            public readonly List<Matrix4x4[]> Arrays = new List<Matrix4x4[]>();
            public int Count;
            public int Tris;
            public void Clear() { Count = 0; }
            public void Add(in Matrix4x4 m)
            {
                int a = Count / Batch, i = Count % Batch;
                if (a == Arrays.Count) Arrays.Add(new Matrix4x4[Batch]);
                Arrays[a][i] = m;
                Count++;
            }
        }
        DrawList[,,] lists;

        bool hidden;
        Vector3 lastCamPos = new Vector3(1e9f, 0, 0);
        Quaternion lastCamRot;
        Vector3 lastRootPos;
        float lastRebuild, nextStats;
        float camFov;
        long statCalls, statInst, statTris; int statFrames;

        void Awake() { Instance = this; }

        // ------------------------------------------------------------------------------------------- loading
        internal void OnMapLoaded(MapSettings ms)
        {
            Unload();
            if (ms == null) return;
            string name = MapIds.Clean(ms.name);
            // Ours: the built-in MapIds prefixes (TerrainLab_, IronjawSound_) or General.MapNamePrefix.
            string cfgPrefix = Plugin.MapNamePrefix.Value ?? "";
            string prefix = MapIds.PrefixOf(name) ??
                            (cfgPrefix.Length > 0 && name.StartsWith(cfgPrefix, StringComparison.Ordinal) ? cfgPrefix : null);
            if (prefix == null)
            {
                Plugin.Log.LogInfo($"map '{name}' is not a terrain-lab map; no rocks.");
                return;
            }
            if (!Plugin.Enabled.Value)
            {
                Plugin.Log.LogInfo($"map '{name}': rocks switched off in the config (General.Enabled = false).");
                return;
            }
            var sw = Stopwatch.StartNew();
            // MapIds.VariantOf handles the stable "IronjawSound" name (variant "ironjawsound") as well as the prefixes.
            string variant = MapIds.PrefixOf(name) != null ? MapIds.VariantOf(name) : name.Substring(prefix.Length);
            Plugin.Log.LogInfo($"=== terrain-lab map '{name}' (variant '{variant}'): loading rocks ===");
            if (!LoadAssets()) return;
            long tAssets = sw.ElapsedMilliseconds;

            string path = DataPath(variant);
            if (path == null) return;
            try { data = RockData.Load(path); }
            catch (Exception e) { Plugin.Log.LogError($"MISSING/BAD rock data {path}: {e.Message}. No rocks on this map."); data = null; return; }
            long tData = sw.ElapsedMilliseconds;
            if (!string.Equals(data.VariantSafe, variant, StringComparison.OrdinalIgnoreCase))
                Plugin.Log.LogWarning($"rock data is for variant '{data.VariantSafe}' but the map is '{variant}'. Rocks may float or sit in the sea; rebuild with terrainlab.rocks.");
            Plugin.Log.LogInfo($"data {Path.GetFileName(path)}: {data.Rocks.Length} rocks, variant '{data.Variant}', git {data.Git}, made {data.Created}");
            if (!MapTypes()) { data = null; return; }

            root = ms.transform;
            lastRootPos = root.position;
            if (root.rotation != Quaternion.identity || root.lossyScale != Vector3.one)
                Plugin.Log.LogWarning($"map root has rotation {root.rotation.eulerAngles} / scale {root.lossyScale}; culling assumes neither.");
            var terrain = root.Find("Terrain");
            var anyTile = (terrain != null ? terrain : root).GetComponentInChildren<MeshRenderer>(true);
            drawLayer = anyTile != null ? anyTile.gameObject.layer : 0;

            int snapped = 0, fallback = 0;
            if (Plugin.SnapToGround.Value) Snap(out snapped, out fallback);
            long tSnap = sw.ElapsedMilliseconds;
            BuildInstances();
            long tInst = sw.ElapsedMilliseconds;
            int cols = Plugin.Colliders.Value ? MakeColliders() : 0;
            long tCol = sw.ElapsedMilliseconds;
            LogCounts();
            Plugin.Log.LogInfo($"rocks ready in {sw.ElapsedMilliseconds} ms (bundle {tAssets}, data {tData - tAssets}, snap {tSnap - tData} " +
                               $"[{snapped} on tiles, {fallback} kept data height], instances {tInst - tSnap}, colliders {tCol - tInst} [{cols}]); " +
                               $"draw layer {drawLayer} ({LayerMask.LayerToName(drawLayer)})");
            lastRebuild = -1f;
            nextStats = Time.unscaledTime + Plugin.StatsInterval.Value;
        }

        string DataPath(string variant)
        {
            string want = string.IsNullOrEmpty(Plugin.DataFile.Value) ? $"rocks_{variant}.bin" : Plugin.DataFile.Value;
            string p = Path.Combine(Plugin.Dir, want);
            if (File.Exists(p)) return p;
            var all = Directory.GetFiles(Plugin.Dir, "rocks_*.bin");
            if (string.IsNullOrEmpty(Plugin.DataFile.Value) && all.Length == 1)
            {
                Plugin.Log.LogWarning($"MISSING rock data {want}; using {Path.GetFileName(all[0])} instead (made for another variant?).");
                return all[0];
            }
            Plugin.Log.LogError($"MISSING rock data: {p} not found ({all.Length} other rocks_*.bin in {Plugin.Dir}). No rocks on this map.");
            return null;
        }

        bool LoadAssets()
        {
            if (bundle != null && mats != null) return true;
            string p = Path.Combine(Plugin.Dir, Plugin.BundleFile.Value);
            if (Plugin.BundleFile.Value.ToLowerInvariant().Contains("map_"))
                Plugin.Log.LogWarning("bundle name contains \"map_\": NOMapLoader will also try to open it as a map.");
            if (!File.Exists(p)) { Plugin.Log.LogError($"MISSING rock bundle {p}. No rocks."); return false; }
            bundle = AssetBundle.LoadFromFile(p);
            if (bundle == null) { Plugin.Log.LogError($"could not open rock bundle {p} (already loaded or wrong Unity version?). No rocks."); return false; }
            var bare = bundle.LoadAsset<Material>("Rock_Bare");
            var snow = bundle.LoadAsset<Material>("Rock_Snow");
            if (bare == null || snow == null)
            {
                Plugin.Log.LogError($"MISSING material in {p}: Rock_Bare {(bare != null ? "ok" : "missing")}, Rock_Snow {(snow != null ? "ok" : "missing")}. No rocks.");
                return false;
            }
            mats = new[] { bare, snow };
            foreach (var m in mats)
            {
                bool useGame = Plugin.Shader.Value == ShaderSource.Game || m.shader == null || !m.shader.isSupported;
                if (useGame)
                {
                    var sh = UnityEngine.Shader.Find("Universal Render Pipeline/Lit");
                    if (sh != null)
                    {
                        if (Plugin.Shader.Value != ShaderSource.Game)
                            Plugin.Log.LogWarning($"bundle shader '{m.shader?.name}' not supported here; using the game's URP/Lit for {m.name}.");
                        m.shader = sh;
                        m.EnableKeyword("_NORMALMAP");
                    }
                    else Plugin.Log.LogWarning("the game's URP/Lit shader was not found; keeping the bundle shader.");
                }
                m.enableInstancing = true;
                Plugin.Log.LogInfo($"material {m.name}: shader '{m.shader.name}' supported={m.shader.isSupported}, base map {(m.GetTexture("_BaseMap") != null ? "ok" : "MISSING")}, normal {(m.GetTexture("_BumpMap") != null ? "ok" : "MISSING")}");
            }
            return true;
        }

        bool MapTypes()
        {
            int nt = data.Types.Length;
            meshes = new Mesh[nt, 3];
            tris = new int[nt, 3];
            int missing = 0;
            for (int t = 0; t < nt; t++)
                for (int l = 0; l < 3; l++)
                {
                    var m = bundle.LoadAsset<Mesh>($"{data.Types[t]}_lod{l}");
                    if (m == null) { Plugin.Log.LogWarning($"MISSING mesh {data.Types[t]}_lod{l} in the bundle; that LOD is not drawn."); missing++; continue; }
                    meshes[t, l] = m;
                    tris[t, l] = (int)(m.GetIndexCount(0) / 3);
                }
            if (missing == nt * 3) { Plugin.Log.LogError("no rock meshes found in the bundle. No rocks."); return false; }
            lists = new DrawList[nt, 2, 3];
            for (int t = 0; t < nt; t++)
                for (int v = 0; v < 2; v++)
                    for (int l = 0; l < 3; l++)
                        lists[t, v, l] = new DrawList { Mesh = meshes[t, l], Mat = mats[v], Tris = tris[t, l],
                            Shadow = l < 2 && Plugin.Shadows.Value ? ShadowCastingMode.On : ShadowCastingMode.Off };
            return true;
        }

        void Snap(out int hits, out int fallback)
        {
            hits = 0; fallback = 0;
            Physics.SyncTransforms();
            var rocks = data.Rocks;
            int n = rocks.Length;
            var cmds = new NativeArray<RaycastCommand>(n, Allocator.TempJob);
            var res = new NativeArray<RaycastHit>(n, Allocator.TempJob);
            try
            {
                int mask = 1 << Plugin.ColliderLayer.Value;
                for (int i = 0; i < n; i++)
                {
                    var w = root.TransformPoint(rocks[i].Pos + Vector3.up * 400f);
#pragma warning disable CS0618
                    cmds[i] = new RaycastCommand(w, Vector3.down, 800f, mask, 1);
#pragma warning restore CS0618
                }
                RaycastCommand.ScheduleBatch(cmds, res, 256).Complete();
                float maxD = Plugin.SnapMaxDelta.Value;
                for (int i = 0; i < n; i++)
                {
                    var h = res[i];
                    if (h.collider != null)
                    {
                        float y = root.InverseTransformPoint(h.point).y;
                        if (Mathf.Abs(y - rocks[i].Pos.y) <= maxD) { rocks[i].Pos.y = y; hits++; continue; }
                    }
                    fallback++;
                }
            }
            finally { cmds.Dispose(); res.Dispose(); }
        }

        static bool AlwaysKept(string type) => type.StartsWith("erratic") || type.StartsWith("outcrop");

        void BuildInstances()
        {
            var rocks = data.Rocks;
            int n = rocks.Length;
            local = new Matrix4x4[n];
            keep = new bool[n];
            radius = new float[n];
            float half = data.TilesPerSide * data.TileM * 0.5f;
            cellsPerSide = Mathf.CeilToInt(2f * half / CellM);
            cells = new List<int>[cellsPerSide * cellsPerSide];
            float dens = Plugin.Density.Value;
            int dropped = 0;
            for (int i = 0; i < n; i++)
            {
                var r = rocks[i];
                var rot = Quaternion.AngleAxis(r.TiltX, Vector3.right) * Quaternion.AngleAxis(r.TiltZ, Vector3.forward)
                          * Quaternion.AngleAxis(r.Yaw, Vector3.up);
                local[i] = Matrix4x4.TRS(r.Pos - Vector3.up * r.Sink, rot, Vector3.one * r.Scale);
                radius[i] = r.Scale * 0.75f;
                string tn = r.Type < data.Types.Length ? data.Types[r.Type] : "";
                // stable per-rock hash so the same rocks stay when density changes
                uint hsh = (uint)i * 2654435761u;
                keep[i] = r.Type < data.Types.Length && (AlwaysKept(tn) || dens >= 1f || (hsh >> 8) / 16777216f < dens);
                if (!keep[i]) { dropped++; continue; }
                int cx = Mathf.Clamp((int)((r.Pos.x + half) / CellM), 0, cellsPerSide - 1);
                int cz = Mathf.Clamp((int)((r.Pos.z + half) / CellM), 0, cellsPerSide - 1);
                int c = cz * cellsPerSide + cx;
                (cells[c] ??= new List<int>()).Add(i);
            }
            cellBounds = new Bounds[cells.Length];
            for (int c = 0; c < cells.Length; c++)
            {
                if (cells[c] == null) continue;
                float lo = float.MaxValue, hi = float.MinValue, rmax = 0f;
                foreach (int i in cells[c]) { lo = Mathf.Min(lo, rocks[i].Pos.y); hi = Mathf.Max(hi, rocks[i].Pos.y); rmax = Mathf.Max(rmax, radius[i]); }
                int cx = c % cellsPerSide, cz = c / cellsPerSide;
                var min = new Vector3(cx * CellM - half - rmax, lo - rmax, cz * CellM - half - rmax);
                var max = new Vector3((cx + 1) * CellM - half + rmax, hi + 2f * rmax, (cz + 1) * CellM - half + rmax);
                cellBounds[c] = new Bounds((min + max) * 0.5f, max - min);
            }
            if (dropped > 0) Plugin.Log.LogInfo($"density {dens:0.00}: {dropped} small rocks left out");
        }

        int MakeColliders()
        {
            var rocks = data.Rocks;
            colliderRoot = new GameObject("TerrainLabRocks");
            colliderRoot.transform.SetParent(root, false);
            colliderRoot.layer = Plugin.ColliderLayer.Value;
            PhysicMaterial pm = null;
            if (Plugin.TerrainPhysicMaterial.Value)
            {
                try { pm = GameAssets.i != null ? GameAssets.i.terrainMaterial : null; }
                catch (Exception e) { Plugin.Log.LogWarning($"GameAssets.terrainMaterial not available ({e.Message}); colliders use the default material."); }
                if (pm == null) Plugin.Log.LogWarning("terrain PhysicMaterial not found; rock hits will look like armour hits.");
            }
            var baked = new HashSet<int>();
            int made = 0;
            float min = Plugin.ColliderMinSize.Value;
            for (int i = 0; i < rocks.Length; i++)
            {
                var r = rocks[i];
                if (!keep[i] || !r.Solid || r.Scale < min) continue;
                var mesh = meshes[r.Type, 2] ?? meshes[r.Type, 1] ?? meshes[r.Type, 0];
                if (mesh == null) continue;
                if (baked.Add(mesh.GetInstanceID()))
                {
                    try { Physics.BakeMesh(mesh.GetInstanceID(), true); }
                    catch (Exception e) { Plugin.Log.LogWarning($"BakeMesh {mesh.name}: {e.Message}"); }
                }
                var go = new GameObject("r");
                go.layer = Plugin.ColliderLayer.Value;
                var t = go.transform;
                t.SetParent(colliderRoot.transform, false);
                t.localPosition = local[i].GetColumn(3);
                t.localRotation = local[i].rotation;
                t.localScale = Vector3.one * r.Scale;
                var mc = go.AddComponent<MeshCollider>();
                mc.convex = true;
                mc.sharedMesh = mesh;
                if (pm != null) mc.sharedMaterial = pm;
                made++;
            }
            colliderRoot.isStatic = true;
            return made;
        }

        void LogCounts()
        {
            var rocks = data.Rocks;
            int nt = data.TilesPerSide;
            var perTile = new int[nt * nt];
            var perType = new int[data.Types.Length];
            var perClass = new int[Math.Max(1, data.Classes.Length)];
            int snowy = 0, shown = 0;
            for (int i = 0; i < rocks.Length; i++)
            {
                if (!keep[i]) continue;
                shown++;
                if (rocks[i].Tile < perTile.Length) perTile[rocks[i].Tile]++;
                perType[rocks[i].Type]++;
                if (rocks[i].Class < perClass.Length) perClass[rocks[i].Class]++;
                if (rocks[i].Variant == 1) snowy++;
            }
            Plugin.Log.LogInfo($"{shown} rocks ({snowy} snowy). per type: " +
                string.Join(", ", data.Types.Select((t, k) => $"{t} {perType[k]}")));
            Plugin.Log.LogInfo("per class: " + string.Join(", ", data.Classes.Select((c, k) => $"{c} {perClass[k]}")));
            var sb = new StringBuilder("rocks per 5120 m tile (row 0 = south, printed north first):\n");
            for (int row = nt - 1; row >= 0; row--)
            {
                sb.Append($"  r{row,2}:");
                for (int col = 0; col < nt; col++) sb.Append($" {perTile[row * nt + col],5}");
                sb.Append('\n');
            }
            Plugin.Log.LogInfo(sb.ToString().TrimEnd());
        }

        void Unload()
        {
            if (colliderRoot != null) Destroy(colliderRoot);
            colliderRoot = null;
            root = null;
            data = null;
            local = null;
            cells = null;
            lists = null;
        }

        // ------------------------------------------------------------------------------------------- drawing
        Camera MainCamera()
        {
            Camera cam = null;
            try { var csm = SceneSingleton<CameraStateManager>.i; if (csm != null) cam = csm.mainCamera; } catch (Exception) { }
            return cam != null ? cam : Camera.main;
        }

        void Update()
        {
            if (Plugin.ToggleKey.Value.IsDown())
            {
                hidden = !hidden;
                Plugin.Log.LogInfo(hidden ? "rocks hidden (toggle key)" : "rocks shown (toggle key)");
            }
        }

        void LateUpdate()
        {
            if (lists == null) return;
            if (root == null) { Plugin.Log.LogInfo("map unloaded; rocks released."); Unload(); return; }
            if (hidden) return;
            var cam = MainCamera();
            if (cam == null) return;
            var ct = cam.transform;
            bool rootMoved = root.position != lastRootPos;
            float now = Time.unscaledTime;
            bool moved = (ct.position - lastCamPos).sqrMagnitude > 25f * 25f || Quaternion.Angle(ct.rotation, lastCamRot) > 4f ||
                         Mathf.Abs(cam.fieldOfView - camFov) > 1f;
            if (rootMoved || moved || (now - lastRebuild > 0.25f && (ct.position - lastCamPos).sqrMagnitude > 0.25f) || lastRebuild < 0f)
                Rebuild(cam, now);
            var only = Plugin.OnlyMainCamera.Value ? cam : null;
            long calls = 0, inst = 0, tr = 0;
            foreach (var dl in lists)
            {
                if (dl.Count == 0 || dl.Mesh == null) continue;
                for (int a = 0; a * Batch < dl.Count; a++)
                {
                    int cnt = Math.Min(Batch, dl.Count - a * Batch);
                    Graphics.DrawMeshInstanced(dl.Mesh, 0, dl.Mat, dl.Arrays[a], cnt, null, dl.Shadow, true, drawLayer, only,
                                               LightProbeUsage.Off);
                    calls++;
                }
                inst += dl.Count;
                tr += (long)dl.Count * dl.Tris;
            }
            statCalls += calls; statInst += inst; statTris += tr; statFrames++;
            float iv = Plugin.StatsInterval.Value;
            if (iv > 0f && now >= nextStats && statFrames > 0)
            {
                Plugin.Log.LogInfo($"draw: {statCalls / statFrames} instanced calls/frame (plus shadow passes), {statInst / statFrames} rocks, " +
                                   $"{statTris / statFrames / 1000}k triangles/frame; camera at map {root.InverseTransformPoint(ct.position):F0}");
                statCalls = statInst = statTris = 0; statFrames = 0;
                nextStats = now + iv;
            }
        }

        static readonly Plane[] planes = new Plane[6];

        void Rebuild(Camera cam, float now)
        {
            lastRebuild = now;
            var ct = cam.transform;
            lastCamPos = ct.position;
            lastCamRot = ct.rotation;
            camFov = cam.fieldOfView;
            lastRootPos = root.position;
            foreach (var dl in lists) dl.Clear();
            GeometryUtility.CalculateFrustumPlanes(cam, planes);
            // widen a little so the throttled list stays valid while the camera turns
            for (int p = 0; p < 4; p++) planes[p].distance += 60f;
            var toWorld = root.localToWorldMatrix;
            Vector3 camLocal = root.InverseTransformPoint(ct.position);
            float ds = Plugin.DistanceScale.Value;
            // the farthest any rock type is drawn
            float far = 0f;
            for (int t = 0; t < data.LodM.Length; t++) far = Mathf.Max(far, data.LodM[t][2]);
            far *= ds;
            float half = data.TilesPerSide * data.TileM * 0.5f;
            int c0x = Mathf.Clamp((int)((camLocal.x - far + half) / CellM), 0, cellsPerSide - 1);
            int c1x = Mathf.Clamp((int)((camLocal.x + far + half) / CellM), 0, cellsPerSide - 1);
            int c0z = Mathf.Clamp((int)((camLocal.z - far + half) / CellM), 0, cellsPerSide - 1);
            int c1z = Mathf.Clamp((int)((camLocal.z + far + half) / CellM), 0, cellsPerSide - 1);
            var rocks = data.Rocks;
            var lod = data.LodM;
            Vector3 rootPos = root.position;
            for (int cz = c0z; cz <= c1z; cz++)
                for (int cx = c0x; cx <= c1x; cx++)
                {
                    int c = cz * cellsPerSide + cx;
                    var list = cells[c];
                    if (list == null) continue;
                    var b = cellBounds[c];
                    var wb = new Bounds(b.center + rootPos, b.size);
                    if (b.SqrDistance(camLocal) > far * far) continue;
                    if (!GeometryUtility.TestPlanesAABB(planes, wb)) continue;
                    foreach (int i in list)
                    {
                        var r = rocks[i];
                        float d = Vector3.Distance(camLocal, r.Pos) - radius[i];
                        var lm = lod[r.Type];
                        int l = d < lm[0] * ds ? 0 : d < lm[1] * ds ? 1 : d < lm[2] * ds ? 2 : 3;
                        if (l == 3) continue;
                        lists[r.Type, r.Variant & 1, l].Add(toWorld * local[i]);
                    }
                }
        }

        void OnDestroy()
        {
            Unload();
            if (bundle != null) bundle.Unload(false);
        }
    }
}
