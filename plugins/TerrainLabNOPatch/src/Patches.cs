using System;
using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Single entry point. MapSettingsManager.LoadMap (private static) instantiates the map prefab under
    /// Datum.origin, activates it, spawns/registers network objects and then calls
    /// LevelInfo.ApplyMapSettings(mapSettings). A prefix there runs after the map exists and before
    /// LoadFromMission spawns mission units, so blockers and blasts find our detail system.
    /// </summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    static class ApplyMapSettingsPatch
    {
        static readonly HashSet<int> done = new HashSet<int>();

        static void Prefix(LevelInfo __instance, MapSettings mapSettings)
        {
            try { MapPatcher.OnMapLoaded(mapSettings, done); }
            catch (Exception e) { Plugin.Log.LogError($"ApplyMapSettings prefix failed: {e}"); }
            try { OceanTint.Apply(__instance, mapSettings != null && MapPatcher.PrefixOf(mapSettings) == Plugin.MapPrefix.Value); }
            catch (Exception e) { Plugin.Log.LogError($"[7 ocean] failed: {e}"); }
        }
    }

    static class MapPatcher
    {
        internal static MapSettings Current;
        internal static MapContext CurrentCtx;

        internal static int PrefixOf(MapSettings ms)
        {
            var nm = ms.NetworkMap != null ? ms.NetworkMap : ms.GetComponent<NetworkMap>();
            return nm != null ? nm.MapPrefix : -1;
        }

        internal static void OnMapLoaded(MapSettings ms, HashSet<int> done)
        {
            if (ms == null) { Plugin.Log.LogWarning("ApplyMapSettings called with null MapSettings"); return; }
            int prefix = PrefixOf(ms);
            if (prefix != Plugin.MapPrefix.Value)
            {
                Plugin.Log.LogInfo($"Map '{ms.name}' prefix {prefix}: not ours (want {Plugin.MapPrefix.Value}), skipping.");
                Current = null;
                return;
            }
            if (!done.Add(ms.GetInstanceID()))
            {
                Plugin.Log.LogInfo($"Map '{ms.name}' instance already patched, skipping.");
                return;
            }
            Current = ms;
            Plugin.Log.LogInfo($"=== TerrainLab map loaded: '{ms.name}' prefix {prefix}, MapSize {ms.MapSize}, root at {ms.transform.position} ===");

            var ctx = new MapContext(ms);
            CurrentCtx = ctx;
            Run("diagnostics (before)", Plugin.DiagnosticsEnabled.Value, () => Diagnostics.LogMapBefore(ctx));
            // Shader first, so patch 4 registers the final tile materials with TerrainHeightMap.
            Run("3 terrain shader", Plugin.TerrainShaderEnabled.Value, () => TerrainShaderPatch.Apply(ctx));
            Run("1 terrain PhysicMaterial", Plugin.TerrainMaterialEnabled.Value, () => TerrainMaterialPatch.Apply(ctx));
            Run("1b runway pads", Plugin.TerrainMaterialEnabled.Value && Plugin.RunwayPadsEnabled.Value, () => TerrainMaterialPatch.AddRunwayPads(ctx));
            Run("4+2 detail system and trees", Plugin.DetailSystemEnabled.Value, () => DetailSystemPatch.Apply(ctx));
            if (!Plugin.DetailSystemEnabled.Value && Plugin.TreesEnabled.Value)
                Plugin.Log.LogWarning("[2 trees] skipped: trees need the detail system (4.DetailSystem.Enabled = false).");
            Run("diagnostics (after)", Plugin.DiagnosticsEnabled.Value, () => Diagnostics.LogMapAfter(ctx));
            OurAssets.Release();
        }

        static void Run(string label, bool enabled, Action a)
        {
            if (!enabled) { Plugin.Log.LogInfo($"[{label}] disabled in config."); return; }
            try { a(); }
            catch (Exception e) { Plugin.Log.LogError($"[{label}] failed: {e}"); }
        }
    }

    /// <summary>Things every patch needs about the loaded map instance.</summary>
    class MapContext
    {
        public readonly MapSettings Map;
        public readonly Transform Root;
        public readonly Transform TerrainRoot;
        public readonly List<MeshRenderer> Tiles = new List<MeshRenderer>();
        public readonly List<MeshCollider> TileColliders = new List<MeshCollider>();
        public readonly List<PhysicMaterial> OriginalTilePhysicMaterials = new List<PhysicMaterial>();
        public Material OriginalTileMaterial;
        public string Variant;

        public MapContext(MapSettings ms)
        {
            Map = ms;
            Root = ms.transform;
            TerrainRoot = Root.Find("Terrain");
            var src = TerrainRoot != null ? TerrainRoot : Root;
            foreach (var mr in src.GetComponentsInChildren<MeshRenderer>(true))
                if (mr.GetComponent<MeshFilter>() != null) Tiles.Add(mr);
            foreach (var mc in src.GetComponentsInChildren<MeshCollider>(true))
            {
                TileColliders.Add(mc);
                if (mc.sharedMaterial != null && !OriginalTilePhysicMaterials.Contains(mc.sharedMaterial))
                    OriginalTilePhysicMaterials.Add(mc.sharedMaterial);
            }
            if (Tiles.Count > 0) OriginalTileMaterial = Tiles[0].sharedMaterial;
            // Prefab name is TerrainLab_<variant> (IronjawSound_<variant> from v7); instances get "(Clone)".
            Variant = MapIds.VariantOf(ms.name);
            OurAssets.SetVariant(Variant);
            if (TerrainRoot == null)
                Plugin.Log.LogWarning($"No 'Terrain' child under '{ms.name}'; using every MeshRenderer under the root as a tile.");
        }
    }

    /// <summary>Finds the stock map prefab whose parts we borrow.</summary>
    static class Stock
    {
        static MapSettings cached;

        internal static MapSettings MapPrefab()
        {
            if (cached != null) return cached;
            string want = Plugin.StockMapPrefab.Value;
            var msm = MapSettingsManager.i;
            if (msm != null && msm.Maps != null)
            {
                foreach (var m in msm.Maps)
                    if (m != null && m.Prefab != null && m.Prefab.name == want) { cached = m.Prefab; break; }
            }
            if (cached == null)
            {
                foreach (var ms in Resources.FindObjectsOfTypeAll<MapSettings>())
                    if (ms.name == want && !ms.gameObject.scene.IsValid()) { cached = ms; break; }
            }
            if (cached == null) Plugin.Log.LogError($"Stock map prefab '{want}' not found in MapSettingsManager.Maps or loaded assets.");
            else Plugin.Log.LogInfo($"Stock map prefab: '{cached.name}' (MapSize {cached.MapSize})");
            return cached;
        }

        internal static Material TerrainMaterial()
        {
            var p = MapPrefab();
            if (p == null) return null;
            string shaderName = Plugin.StockTerrainShader.Value;
            foreach (var mr in p.GetComponentsInChildren<MeshRenderer>(true))
                foreach (var m in mr.sharedMaterials)
                    if (m != null && m.shader != null && m.shader.name == shaderName)
                    {
                        Plugin.Log.LogInfo($"Stock terrain material: '{m.name}' on '{mr.name}' (shader '{m.shader.name}')");
                        return m;
                    }
            Plugin.Log.LogError($"No material with shader '{shaderName}' under stock prefab '{p.name}'.");
            return null;
        }
    }
}
