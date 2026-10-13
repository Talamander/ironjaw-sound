using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mirage;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace TerrainLab.Gameplay
{
    // Manifest written by src/terrainlab/stock_buildings.py (format terrainlab.stockbuildings/1).
    [Serializable] public class ManifestBuilding { public string source; public float x, y, z, heading; public string label; }
    [Serializable] public class ManifestSet { public string name; public bool powerlines; public bool animate; public ManifestBuilding[] buildings; }
    [Serializable] public class Manifest { public string format; public string prefab; public ManifestSet[] sets; }

    /// <summary>
    /// Copies stock map buildings (houses, sheds, lighthouse, turbines, pylons) onto a TerrainLab map when it loads.
    ///
    /// The stock map prefabs (Heartland, Ignus) stay loaded with the game scene, so their MapBuildings can be found
    /// with Resources.FindObjectsOfTypeAll and cloned by name. Nothing is read from disk except our own manifest.
    ///
    /// Networked mode (default): a prefix on NetworkMap.ServerSpawn / ClientRegister runs on the fresh map instance,
    /// before it spawns its network objects. Each manifest set becomes a GameObject with a NetworkIdentity and a
    /// MapBuildingSet holding the clones, appended to the instance's NetworkObjects, so the game spawns it like a
    /// stock building set: buildings take damage, collapse into their debris prefab, the state syncs in multiplayer,
    /// and powerline sets draw cables pylon to pylon. A set already present in the map bundle under the same name
    /// (empty MapBuildingSet + NetworkIdentity listed in NetworkMap.NetworkObjects) is filled instead of created.
    /// Every multiplayer peer needs this plugin and the same manifest, or the network ids do not match.
    ///
    /// Static mode (fallback, or CopyMode = Static): plain clones without a set. Damage is handled locally by a
    /// TakeDamage prefix (no multiplayer sync), cables are drawn directly.
    /// </summary>
    public static class StockBuildings
    {
        static BepInEx.Logging.ManualLogSource Log => Plugin.Log;
        static readonly Regex Suffix = new Regex(@"(\s*\(\d+\))+$");
        static readonly HashSet<int> doneMaps = new HashSet<int>();
        static readonly Dictionary<MapBuilding, float> staticHp = new Dictionary<MapBuilding, float>();

        public static string ManifestDir =>
            Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "buildings");

        public static void Patch(Harmony h)
        {
            TryPatch(h, AccessTools.Method(typeof(NetworkMap), nameof(NetworkMap.ServerSpawn)), nameof(ServerSpawnPrefix));
            TryPatch(h, AccessTools.Method(typeof(NetworkMap), nameof(NetworkMap.ClientRegister)), nameof(ClientRegisterPrefix));
            TryPatch(h, AccessTools.Method(typeof(MapBuilding), nameof(MapBuilding.TakeDamage)), nameof(TakeDamagePrefix));
        }

        static void TryPatch(Harmony h, System.Reflection.MethodBase target, string prefix)
        {
            try
            {
                if (target == null) { Log.LogWarning($"[TLGameplay] stock buildings: patch target for {prefix} not found"); return; }
                h.Patch(target, prefix: new HarmonyMethod(typeof(StockBuildings), prefix));
            }
            catch (Exception e) { Log.LogWarning($"[TLGameplay] stock buildings: patch {prefix} failed: {e.Message}"); }
        }

        static void ServerSpawnPrefix(NetworkMap __instance) => Inject(__instance, "server");
        static void ClientRegisterPrefix(NetworkMap __instance) => Inject(__instance, "client");

        static void Inject(NetworkMap nm, string role)
        {
            if (!Plugin.CopyBuildings.Value || nm == null) return;
            Transform mapRoot = null;
            try
            {
                var ms = nm.GetComponentInParent<MapSettings>();
                mapRoot = ms != null ? ms.transform : nm.transform;
                string prefab = mapRoot.name.Replace("(Clone)", "").Trim();
                if (!MapIds.IsOurPrefab(prefab)) return;
                if (!doneMaps.Add(mapRoot.gameObject.GetInstanceID())) return;
                string path = Path.Combine(ManifestDir, prefab + ".json");
                if (!File.Exists(path))
                {
                    Log.LogInfo($"[TLGameplay] stock buildings: no manifest for {prefab} at {path}");
                    return;
                }
                // JsonUtility returned no sets for a valid manifest in game (0.2.0), so use the game's own Newtonsoft.Json.
                var m = Newtonsoft.Json.JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(path));
                if (m?.sets == null || m.sets.Length == 0) { Log.LogInfo($"[TLGameplay] stock buildings: manifest has no sets ({path})"); return; }
                Log.LogInfo($"[TLGameplay] stock buildings: manifest {Path.GetFileName(path)} has {m.sets.Length} sets, {m.sets.Sum(s => s.buildings?.Length ?? 0)} buildings");
                Build(nm, mapRoot, m, role);
            }
            catch (Exception e)
            {
                // Never break the map spawn: log and carry on without copies.
                Log.LogError($"[TLGameplay] stock buildings: failed on {(mapRoot != null ? mapRoot.name : "?")}: {e}");
            }
        }

        static Dictionary<string, GameObject> FindSources()
        {
            // Asset objects only (prefab contents have no valid scene), ordered by hierarchy path so every peer
            // picks the same source for a name.
            var found = new Dictionary<string, (string path, GameObject go)>();
            foreach (var mb in Resources.FindObjectsOfTypeAll<MapBuilding>())
            {
                if (mb == null || mb.gameObject.scene.IsValid()) continue;
                string name = Suffix.Replace(mb.gameObject.name, "").Trim();
                string p = HierPath(mb.transform);
                if (!found.TryGetValue(name, out var cur) || string.CompareOrdinal(p, cur.path) < 0)
                    found[name] = (p, mb.gameObject);
            }
            return found.ToDictionary(kv => kv.Key, kv => kv.Value.go);
        }

        static string HierPath(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Transform FindByName(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        static void Build(NetworkMap nm, Transform mapRoot, Manifest m, string role)
        {
            var sources = FindSources();
            bool networked = !string.Equals(Plugin.CopyMode.Value, "Static", StringComparison.OrdinalIgnoreCase);
            var newIds = new List<NetworkIdentity>();
            int made = 0, missing = 0;
            var missingNames = new HashSet<string>();
            foreach (var set in m.sets)
            {
                var items = (set.buildings ?? new ManifestBuilding[0])
                    .Where(b => { bool ok = b != null && b.source != null && sources.ContainsKey(b.source); if (!ok) { missing++; if (b?.source != null) missingNames.Add(b.source); } return ok; })
                    .ToList();
                if (items.Count == 0) continue;

                // Mode A: an empty set baked into the bundle under the same name.
                var baked = FindByName(mapRoot, set.name);
                var bakedSet = baked != null ? baked.GetComponent<MapBuildingSet>() : null;
                if (networked && bakedSet != null && baked.GetComponentInChildren<MapBuilding>(true) == null)
                {
                    SetFlags(bakedSet, set);
                    // Like mode B: no Awake on the copies until Grounding has moved them.
                    bool wasActive = baked.gameObject.activeSelf;
                    baked.gameObject.SetActive(false);
                    try { foreach (var b in items) Clone(sources[b.source], baked, b); }
                    finally { baked.gameObject.SetActive(wasActive); }
                    made += items.Count;
                    Log.LogInfo($"[TLGameplay] stock buildings: filled bundle set {set.name} with {items.Count} ({role})");
                    continue;
                }

                GameObject go = null;
                try
                {
                    go = new GameObject(set.name);
                    go.SetActive(false);  // no Awake until the children are in place
                    go.transform.SetParent(mapRoot, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    NetworkIdentity id = null;
                    if (networked)
                    {
                        id = go.AddComponent<NetworkIdentity>();
                        var mbs = go.AddComponent<MapBuildingSet>();
                        SetFlags(mbs, set);
                    }
                    var clones = items.Select(b => Clone(sources[b.source], go.transform, b)).ToList();
                    go.SetActive(true);
                    if (networked) newIds.Add(id);
                    else MakeStatic(clones, set.powerlines);
                    made += items.Count;
                    Log.LogInfo($"[TLGameplay] stock buildings: {set.name}: {items.Count} copies, " +
                                $"{(networked ? "networked set" : "static")}{(set.powerlines ? ", power line" : "")} ({role})");
                }
                catch (Exception e)
                {
                    Log.LogWarning($"[TLGameplay] stock buildings: set {set.name} failed ({e.GetType().Name}: {e.Message}); " +
                                   "rebuilding it as static copies");
                    if (go != null) UnityEngine.Object.DestroyImmediate(go);
                    var holder = new GameObject(set.name + " (static)");
                    holder.SetActive(false);
                    holder.transform.SetParent(mapRoot, false);
                    var clones = items.Select(b => Clone(sources[b.source], holder.transform, b)).ToList();
                    holder.SetActive(true);
                    MakeStatic(clones, set.powerlines);
                    made += items.Count;
                }
            }
            if (newIds.Count > 0)
            {
                var list = (nm.NetworkObjects ?? new NetworkIdentity[0]).ToList();
                int first = list.Count;
                list.AddRange(newIds);
                nm.NetworkObjects = list.ToArray();  // the instance's own copy; the prefab is untouched
                Log.LogInfo($"[TLGameplay] stock buildings: added {newIds.Count} network set(s) at NetworkObjects[{first}..{list.Count - 1}] " +
                            $"(map prefix {nm.MapPrefix})");
            }
            Log.LogInfo($"[TLGameplay] stock buildings: {made} copies on {mapRoot.name}" +
                        (missing > 0 ? $", {missing} skipped (no stock source named {string.Join(", ", missingNames)})" : "") +
                        $"; {sources.Count} stock building names available");
        }

        static void SetFlags(MapBuildingSet mbs, ManifestSet set)
        {
            var t = Traverse.Create(mbs);
            t.Field("powerlines").SetValue(set.powerlines);
            t.Field("animate").SetValue(set.animate);
        }

        static MapBuilding Clone(GameObject src, Transform parent, ManifestBuilding b)
        {
            var go = UnityEngine.Object.Instantiate(src, parent, false);
            go.name = $"{b.source} (TL {b.label})";
            var e = src.transform.localEulerAngles;
            go.transform.localPosition = new Vector3(b.x, b.y, b.z);
            // The stock rotation holds the mesh import turn (a multiple of 90 degrees) plus a tilt of about 12.7
            // degrees on the suburb blocks. The tilt makes one end of a 100 m block dig 20 m into our ground, so by
            // default it is removed: the model axis nearest to world up is turned exactly upright (0.2.3).
            var q = Quaternion.Euler(e.x, 0f, e.z);
            if (!Plugin.KeepStockTilt.Value) q = Upright(q);
            go.transform.localRotation = Quaternion.Euler(0f, b.heading, 0f) * q;
            go.transform.localScale = src.transform.lossyScale;
            try { Grounding.Apply(go, b.source, b.label, b.y, e); }
            catch (Exception ex) { Log.LogWarning($"[TLGameplay] ground: {b.source} ({b.label}) failed: {ex.Message}"); }
            return go.GetComponent<MapBuilding>();
        }

        static Quaternion Upright(Quaternion q)
        {
            Vector3 best = Vector3.up; float dot = -2f;
            foreach (var a in new[] { Vector3.right, Vector3.up, Vector3.forward, Vector3.left, Vector3.down, Vector3.back })
            {
                float d = Vector3.Dot(q * a, Vector3.up);
                if (d > dot) { dot = d; best = q * a; }
            }
            return Quaternion.FromToRotation(best, Vector3.up) * q;
        }

        static void MakeStatic(List<MapBuilding> clones, bool powerlines)
        {
            foreach (var c in clones) if (c != null) staticHp[c] = 100f;
            if (!powerlines) return;
            for (int i = 0; i + 1 < clones.Count; i++)
            {
                try { clones[i].SpawnPowerline(clones[i + 1]); }
                catch (Exception e) { Log.LogWarning($"[TLGameplay] stock buildings: cable {i} failed: {e.Message}"); break; }
            }
        }

        // Static copies have no MapBuildingSet: apply the game's own damage formula locally instead of the
        // "buildingSet was not set" error.
        static bool TakeDamagePrefix(MapBuilding __instance, float pierceDamage, float blastDamage, float amountAffected,
                                     float fireDamage, float impactDamage)
        {
            if (__instance == null || !staticHp.TryGetValue(__instance, out float hp)) return true;
            try
            {
                var a = __instance.GetArmorProperties();
                float dmg = impactDamage;
                if (a != null)
                {
                    dmg += Mathf.Max(pierceDamage - a.pierceArmor, 0f) / Mathf.Max(a.pierceTolerance, 0.01f);
                    dmg += Mathf.Max(blastDamage - a.blastArmor, 0f) * amountAffected / Mathf.Max(a.blastTolerance, 0.01f);
                    dmg += Mathf.Max(fireDamage - a.fireArmor, 0f) / Mathf.Max(a.fireTolerance, 0.01f);
                }
                hp -= dmg;
                if (hp < 0f)
                {
                    staticHp.Remove(__instance);
                    __instance.Destruct();
                }
                else staticHp[__instance] = hp;
            }
            catch (Exception e) { Log.LogWarning($"[TLGameplay] stock buildings: static damage failed: {e.Message}"); }
            return false;
        }

        /// <summary>One-line state of the copies for the mission report.</summary>
        public static string Census()
        {
            int sets = 0, alive = 0, pylons = 0;
            foreach (var s in UnityEngine.Object.FindObjectsOfType<MapBuildingSet>())
            {
                if (!s.name.StartsWith("TL_Stock", StringComparison.Ordinal)) continue;
                sets++;
                foreach (var b in s.GetComponentsInChildren<MapBuilding>())
                {
                    alive++;
                    if (b.name.StartsWith("pylon1", StringComparison.Ordinal)) pylons++;
                }
            }
            int stat = staticHp.Keys.Count(k => k != null);
            return $"stock building copies: {sets} networked set(s), {alive} standing in sets ({pylons} pylons), {stat} static";
        }
    }
}
