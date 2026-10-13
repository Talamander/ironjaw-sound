using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using NuclearOption.Effects;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Patch 5. Map facts at load, a status line every N seconds (camera, local aircraft, ground under the
    /// camera, trees drawn) and every NOMapLoader warning/error or exception we can see.
    /// </summary>
    static class Diagnostics
    {
        internal static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        static bool installed;

        internal static void Install()
        {
            if (installed) return;
            installed = true;
            // Our own runner: the BepInEx manager object is not guaranteed to live through scene loads.
            var go = new GameObject("TerrainLabNOPatch.Runner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<DiagnosticsRunner>();
            if (!Plugin.DiagnosticsEnabled.Value) return;
            BepInEx.Logging.Logger.Listeners.Add(new NOMapLoaderListener());
            Application.logMessageReceivedThreaded += OnUnityLog;
        }

        static void OnUnityLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            if ((stack != null && stack.Contains("NOMapLoader")) || (condition != null && condition.Contains("NOMapLoader")))
                Pending.Enqueue($"NOMapLoader {type}: {condition}\n{stack}");
        }

        /// <summary>BepInEx's LogOutput.log already holds anything NOMapLoader logged before we existed.</summary>
        internal static void ScanLogFileOnce()
        {
            string path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
            try
            {
                if (!File.Exists(path)) { Plugin.Log.LogInfo($"[5] {path} not found; skipping NOMapLoader log scan."); return; }
                int n = 0, bad = 0;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (!line.Contains(":NOMapLoader")) continue;
                        n++;
                        if (line.StartsWith("[Error") || line.StartsWith("[Warning") || line.StartsWith("[Fatal"))
                        {
                            bad++;
                            Plugin.Log.LogWarning($"[5] NOMapLoader (from LogOutput.log): {line}");
                        }
                    }
                }
                Plugin.Log.LogInfo($"[5] LogOutput.log: {n} NOMapLoader lines, {bad} warnings/errors.");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[5] Could not scan {path}: {e.Message}"); }
        }

        static bool scanned;

        internal static void LogMapBefore(MapContext ctx)
        {
            if (!scanned) { scanned = true; ScanLogFileOnce(); }
            var sb = new StringBuilder();
            var msm = MapSettingsManager.i;
            if (msm != null && msm.Maps != null)
                sb.AppendLine($"MapSettingsManager.Maps ({msm.Maps.Length}): " + string.Join(", ", msm.Maps.Select(m =>
                    m == null ? "null" : $"{m.Details?.MapName}/{m.Prefab?.name}/prefix {(m.Prefab != null ? MapPatcher.PrefixOf(m.Prefab) : -1)}")));
            int allColliders = ctx.Root.GetComponentsInChildren<Collider>(true).Length;
            sb.AppendLine($"Map '{ctx.Map.name}' variant '{ctx.Variant}', MapPrefix {MapPatcher.PrefixOf(ctx.Map)}, MapSize {ctx.Map.MapSize}, Latitude {ctx.Map.Latitude}, " +
                          $"TerrainColorMap {(ctx.Map.TerrainColorMap != null ? ctx.Map.TerrainColorMap.name : "null")}, Datum.origin {(Datum.origin != null ? Datum.origin.position.ToString() : "null")}");
            sb.AppendLine($"Tiles {ctx.Tiles.Count} renderers, {ctx.TileColliders.Count} tile MeshColliders, {allColliders} colliders under the map root.");
            sb.Append("Sample tile before patches: " + SampleTile(ctx));
            Plugin.Log.LogInfo("[5] " + sb);
        }

        internal static void LogMapAfter(MapContext ctx)
        {
            Plugin.Log.LogInfo("[5] Sample tile after patches: " + SampleTile(ctx));
            var sb = new StringBuilder("[5] Runway markers in map prefab:");
            int n = 0;
            foreach (var t in ctx.Root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("Runway ")) continue;
                var s = t.Find("Start"); var e = t.Find("End");
                if (s == null || e == null) continue;
                n++;
                sb.Append($"\n  {(t.parent != null ? t.parent.name : "")}/{t.name}: start {s.position.ToGlobalPosition()} end {e.position.ToGlobalPosition()} length {Vector3.Distance(s.position, e.position):F0} m");
            }
            if (n == 0) sb.Append(" none");
            Plugin.Log.LogInfo(sb.ToString());
            DiagnosticsRunner.ScheduleAirbaseLog = Time.unscaledTime + 5f;
        }

        static string SampleTile(MapContext ctx)
        {
            if (ctx.Tiles.Count == 0) return "no tiles";
            var mr = ctx.Tiles[ctx.Tiles.Count / 2];
            var mc = mr.GetComponent<MeshCollider>();
            var mf = mr.GetComponent<MeshFilter>();
            var m = mr.sharedMaterial;
            var ga = GameAssets.i != null ? GameAssets.i.terrainMaterial : null;
            return $"'{mr.name}' at {mr.transform.position.ToGlobalPosition()}, layer {mr.gameObject.layer}, mesh {(mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount + " verts" : "none")}, " +
                   $"material '{m?.name}' shader '{m?.shader?.name}', collider material '{mc?.sharedMaterial?.name}' (is GameAssets.terrainMaterial: {mc != null && mc.sharedMaterial == ga})";
        }
    }

    class NOMapLoaderListener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs e)
        {
            if (e.Source == null || e.Source.SourceName != "NOMapLoader") return;
            if ((e.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) == 0) return;
            // Logging from inside a listener re-enters BepInEx; queue it for the runner instead.
            Diagnostics.Pending.Enqueue($"NOMapLoader {e.Level}: {e.Data}");
        }

        public void Dispose() { }
    }

    class DiagnosticsRunner : MonoBehaviour
    {
        internal static float ScheduleAirbaseLog = -1f;
        float next;
        bool treeRequestInFlight;

        void Update()
        {
            while (Diagnostics.Pending.TryDequeue(out var msg)) Plugin.Log.LogWarning("[5] " + msg);
            if (!Plugin.DiagnosticsEnabled.Value) return;
            var ms = MapPatcher.Current;
            if (ms == null) return;   // not our map (or unloaded)

            if (ScheduleAirbaseLog > 0 && Time.unscaledTime > ScheduleAirbaseLog) { ScheduleAirbaseLog = -1f; LogAirbases(); }

            if (Plugin.DiagnosticsInterval.Value <= 0f || Time.unscaledTime < next) return;
            next = Time.unscaledTime + Mathf.Max(1f, Plugin.DiagnosticsInterval.Value);
            try { Tick(ms); } catch (Exception e) { Plugin.Log.LogWarning($"[5] tick failed: {e.Message}"); }
        }

        void Tick(MapSettings ms)
        {
            var sb = new StringBuilder("[5] tick");
            var cam = Camera.main;
            if (cam != null)
            {
                var p = cam.transform.position;
                sb.Append($" camera {p.ToGlobalPosition()}");
                if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out var hit, 20000f, ~0, QueryTriggerInteraction.Ignore))
                    sb.Append($" ground '{hit.collider.name}' at {hit.point.ToGlobalPosition().y:F1} m (AGL {p.y - hit.point.y:F0} m, physMat '{hit.collider.sharedMaterial?.name ?? "none"}')");
                else sb.Append(" ground: no hit");
            }
            else sb.Append(" camera none");
            if (GameManager.GetLocalAircraft(out var ac) && ac != null)
                sb.Append($" | aircraft '{ac.name}' {ac.transform.position.ToGlobalPosition()} speed {(ac.rb != null ? ac.rb.velocity.magnitude : 0f):F0} m/s");
            Plugin.Log.LogInfo(sb.ToString());
            RequestTreeCount();
        }

        void RequestTreeCount()
        {
            if (treeRequestInFlight) return;
            var dr = DetailRenderer.i;
            if (dr == null || !dr.isActiveAndEnabled) { Plugin.Log.LogInfo("[5] trees: no active DetailRenderer"); return; }
            var buf = DetailSystemPatch.Get(dr, "argBuffer") as GraphicsBuffer;
            var trees = DetailSystemPatch.Get(dr, "treeRenderers") as TreeRenderer[];
            if (buf == null || trees == null || trees.Length == 0) { Plugin.Log.LogInfo($"[5] trees: argBuffer {(buf == null ? "null" : "ok")}, tree renderers {trees?.Length ?? 0}"); return; }
            if (!SystemInfo.supportsAsyncGPUReadback) { Plugin.Log.LogInfo("[5] trees: no AsyncGPUReadback"); return; }
            var idx = trees.Select(t => t != null ? t.ArgIndex : uint.MaxValue).ToArray();
            treeRequestInFlight = true;
            AsyncGPUReadback.Request(buf, req =>
            {
                treeRequestInFlight = false;
                if (req.hasError) { Plugin.Log.LogInfo("[5] trees: readback error"); return; }
                var data = req.GetData<uint>();
                var sb = new StringBuilder("[5] trees:");
                foreach (uint r in idx)
                {
                    if (r == uint.MaxValue) continue;
                    int pos = (int)((r * 3 + 0) * 5 + 3), vis = (int)((r * 3 + 2) * 5 + 1);
                    if (vis >= data.Length) { sb.Append($" renderer {r} out of range"); continue; }
                    sb.Append($" renderer {r}: {data[pos]} positions, {data[vis]} drawn");
                }
                Plugin.Log.LogInfo(sb.ToString());
            });
        }

        void LogAirbases()
        {
            var sb = new StringBuilder("[5] Airbase components in scene:");
            int n = 0;
            foreach (var ab in FindObjectsOfType<Airbase>())
            {
                n++;
                sb.Append($"\n  '{ab.name}' at {ab.transform.position.ToGlobalPosition()}, runways {ab.runways?.Length ?? 0}");
                if (ab.runways == null) continue;
                foreach (var rw in ab.runways)
                    if (rw != null && rw.Start != null && rw.End != null)
                        sb.Append($"\n    runway {rw.index}: {rw.Start.position.ToGlobalPosition()} to {rw.End.position.ToGlobalPosition()}, {Vector3.Distance(rw.Start.position, rw.End.position):F0} m, takeoff {rw.Takeoff}, landing {rw.Landing}");
            }
            if (n == 0) sb.Append(" none (mission airbases may not have spawned yet, or the map has none)");
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
