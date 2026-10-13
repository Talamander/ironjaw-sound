using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab.Scatter
{
    public enum ShaderSource { Bundle, Game }

    /// <summary>
    /// Draws the terrain-lab boulder and rock scatter on our maps (prefab name TerrainLab_* or IronjawSound_*), and nothing on any
    /// other map. Rocks come from two files next to the DLL: the asset bundle terrainlab_rocks (meshes, two
    /// materials, textures) and rocks_&lt;variant&gt;.bin (placement made by terrainlab.rocks). Rocks are drawn
    /// with GPU instancing; big ones get a convex collider on the Statics layer. See docs/SCATTER.md.
    /// </summary>
    [BepInPlugin("talamander.terrainlab.scatter", "TerrainLabScatter", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "1.0.0";
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static string Dir;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> MapNamePrefix;
        internal static ConfigEntry<string> BundleFile;
        internal static ConfigEntry<string> DataFile;
        internal static ConfigEntry<float> Density;
        internal static ConfigEntry<float> DistanceScale;
        internal static ConfigEntry<bool> Shadows;
        internal static ConfigEntry<bool> OnlyMainCamera;
        internal static ConfigEntry<ShaderSource> Shader;
        internal static ConfigEntry<bool> SnapToGround;
        internal static ConfigEntry<float> SnapMaxDelta;
        internal static ConfigEntry<bool> Colliders;
        internal static ConfigEntry<float> ColliderMinSize;
        internal static ConfigEntry<int> ColliderLayer;
        internal static ConfigEntry<bool> TerrainPhysicMaterial;
        internal static ConfigEntry<float> StatsInterval;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;

        void Awake()
        {
            Log = Logger;
            Dir = Path.GetDirectoryName(Info.Location);
            Enabled = Config.Bind("General", "Enabled", true,
                "Draw boulders and rocks on terrain-lab maps. false = the plugin does nothing at all (no rocks, no colliders). " +
                "Read when a map loads.");
            MapNamePrefix = Config.Bind("General", "MapNamePrefix", "TerrainLab_",
                "Maps whose prefab name starts with this get rocks, as do TerrainLab_* and IronjawSound_* maps always. Stock maps are never touched.");
            BundleFile = Config.Bind("Files", "Bundle", "terrainlab_rocks",
                "Asset bundle with the rock meshes and materials, next to the DLL. Must not contain \"map_\".");
            DataFile = Config.Bind("Files", "Data", "",
                "Placement file next to the DLL. Empty = rocks_<variant>.bin from the map name (TerrainLab_opt_1_v4 -> " +
                "rocks_opt_1_v4.bin); if that is missing, the only rocks_*.bin in the folder (with a warning).");
            Density = Config.Bind("Look", "Density", 1.0f,
                new ConfigDescription("Share of the small rocks and boulders to keep (0-1). Lone erratics and rock bands always stay.",
                    new AcceptableValueRange<float>(0f, 1f)));
            DistanceScale = Config.Bind("Look", "DistanceScale", 1.0f,
                new ConfigDescription("Multiplies every LOD and draw distance. Lower it for more frames per second.",
                    new AcceptableValueRange<float>(0.25f, 3f)));
            Shadows = Config.Bind("Look", "Shadows", true, "Near rocks (LOD 0 and 1) cast shadows.");
            OnlyMainCamera = Config.Bind("Look", "OnlyMainCamera", false,
                "Draw only in the main camera. false = every camera (targeting and TV views show rocks too).");
            Shader = Config.Bind("Look", "Shader", ShaderSource.Bundle,
                "Bundle = the URP/Lit shader built into our bundle. Game = the game's own URP/Lit (try this if rocks are " +
                "pink or invisible). An unsupported bundle shader falls back to Game automatically.");
            SnapToGround = Config.Bind("Placement", "SnapToGround", true,
                "Raycast every rock onto the real terrain tiles when the map loads (the data has the master height).");
            SnapMaxDelta = Config.Bind("Placement", "SnapMaxDelta", 30f,
                "A snap hit further than this (m) from the data height is ignored and the data height is used.");
            Colliders = Config.Bind("Physics", "Colliders", true,
                "Solid rocks get a convex collider (vehicles, gear, bullets and line of sight treat them as ground). Rubble never does.");
            ColliderMinSize = Config.Bind("Physics", "ColliderMinSize", 2.0f, "Only solid rocks at least this wide (m) get a collider.");
            ColliderLayer = Config.Bind("Physics", "Layer", 6, "Layer for rock colliders. 6 = Statics, the layer the terrain uses.");
            TerrainPhysicMaterial = Config.Bind("Physics", "TerrainPhysicMaterial", true,
                "Rock colliders use the game's terrain PhysicMaterial, so hits show dirt impacts, not armour sparks.");
            StatsInterval = Config.Bind("Debug", "StatsInterval", 0f,
                "Seconds between draw statistics lines in the log (draw calls, instances, triangles). 0 = off.");
            ToggleKey = Config.Bind("Debug", "ToggleKey", KeyboardShortcut.Empty,
                "Optional key that hides or shows the rocks while flying (drawing only; colliders stay). Empty = none.");

            var go = new GameObject("TerrainLabScatter");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();
            try { new Harmony("talamander.terrainlab.scatter").PatchAll(typeof(Plugin).Assembly); }
            catch (Exception e) { Log.LogError($"Harmony patch failed, rocks will not load: {e}"); }
            Log.LogInfo($"TerrainLabScatter {Version} loaded (enabled={Enabled.Value}, folder {Dir}).");
        }
    }

    /// <summary>LevelInfo.ApplyMapSettings runs after the map prefab exists and before mission units spawn.</summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    static class ApplyMapSettingsPatch
    {
        static void Prefix(MapSettings mapSettings)
        {
            try { Runner.Instance?.OnMapLoaded(mapSettings); }
            catch (Exception e) { Plugin.Log.LogError($"map load hook failed, no rocks: {e}"); }
        }
    }
}
