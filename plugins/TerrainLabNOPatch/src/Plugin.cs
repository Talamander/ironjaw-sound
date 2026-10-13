using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    public enum GrassMode { Disable, Stock, OurMask }
    public enum SplatMode { Ours, Stock, Black }
    public enum MacroAOMode { White, Stock, None }

    /// <summary>
    /// Companion plugin for the terrain-lab map loaded by NOMapLoader. Every patch is gated on the loaded map's
    /// NetworkMap.MapPrefix (61 by default) and has its own config toggle. See docs/BEPINEX-PATCH.md.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("NOMapLoader", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "talamander.terrainlab.nopatch";
        public const string Name = "TerrainLabNOPatch";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // General
        internal static ConfigEntry<int> MapPrefix;
        internal static ConfigEntry<string> StockMapPrefab;
        internal static ConfigEntry<string> BundleNameContains;

        // Patch 1: terrain PhysicMaterial identity
        internal static ConfigEntry<bool> TerrainMaterialEnabled;
        internal static ConfigEntry<string> ColliderMaterialNames;
        internal static ConfigEntry<bool> RunwayPadsEnabled;
        internal static ConfigEntry<float> RunwayPadOverrun;
        internal static ConfigEntry<float> RunwayPadTolerance;
        internal static ConfigEntry<float> RunwayPadDefaultWidth;

        // Patch 2: trees
        internal static ConfigEntry<bool> TreesEnabled;
        internal static ConfigEntry<string> TreePositionsAsset;
        internal static ConfigEntry<string> LushMapAsset;
        internal static ConfigEntry<float> TreeAltitudeMin;
        internal static ConfigEntry<float> TreeAltitudeMax;
        internal static ConfigEntry<bool> DumpTreeMaterial;
        internal static ConfigEntry<string> TreeMaterialOverrides;
        internal static ConfigEntry<TreeLookMode> TreeLook;
        internal static ConfigEntry<TreeModel> TreeModelSetting;
        internal static ConfigEntry<string> TreeWinterLushMap;
        internal static ConfigEntry<string> TreeConiferColor;
        internal static ConfigEntry<string> TreeFrostColor;
        internal static ConfigEntry<float> TreeFrostBrighten;
        internal static ConfigEntry<string> TreeBrightness;
        internal static ConfigEntry<string> TreeHueJitter;
        internal static ConfigEntry<string> TreeBaseTint;
        internal static ConfigEntry<float> TreeSnowAltitudeMin;
        internal static ConfigEntry<float> TreeSnowAltitudeMax;
        internal static ConfigEntry<float> TreeSnowStrength;
        internal static ConfigEntry<float> TreeLushStrength;
        internal static ConfigEntry<string> TreeAlbedoTexture;
        internal static ConfigEntry<string> TreeNormalTexture;
        internal static ConfigEntry<string> TreeMeshFile;
        internal static ConfigEntry<float> TreeMeshRadius;
        internal static ConfigEntry<float> TreeSpruceCutoff;
        internal static ConfigEntry<bool> TreeAlbedoStats;

        // Patch 3: terrain shader
        internal static ConfigEntry<bool> TerrainShaderEnabled;
        internal static ConfigEntry<string> StockTerrainShader;
        internal static ConfigEntry<SplatMode> Splats;
        internal static ConfigEntry<string> SplatMapping;
        internal static ConfigEntry<MacroAOMode> MacroAO;
        internal static ConfigEntry<string> ExtraBindings;
        internal static ConfigEntry<bool> DumpStockMaterial;

        // Patch 4: detail system (TerrainHeightMap, BlastManager, ShaderGlobalManager, DetailRenderer)
        internal static ConfigEntry<bool> DetailSystemEnabled;
        internal static ConfigEntry<float> HeightMin;
        internal static ConfigEntry<float> HeightMax;
        internal static ConfigEntry<GrassMode> Grass;
        internal static ConfigEntry<string> GrassMaskAsset;

        // Patch 5: diagnostics
        internal static ConfigEntry<bool> DiagnosticsEnabled;
        internal static ConfigEntry<float> DiagnosticsInterval;

        // 6: automatic screenshots (off by default; env TERRAINLAB_AUTOSHOT=1 also turns it on)
        internal static ConfigEntry<bool> AutoshotEnabled;
        internal static ConfigEntry<string> AutoshotViews;
        internal static ConfigEntry<string> AutoshotOut;
        internal static ConfigEntry<string> AutoshotModes;
        internal static ConfigEntry<string> AutoshotProbeModes;
        internal static ConfigEntry<float> AutoshotSettle;
        internal static ConfigEntry<float> AutoshotTimeOfDay;

        // 7: ocean colour on our maps (stock colours restored on other maps)
        internal static ConfigEntry<bool> OceanEnabled;
        internal static ConfigEntry<string> OceanDeepColor;
        internal static ConfigEntry<string> OceanShallowColor;
        internal static ConfigEntry<float> OceanStrength;
        internal static ConfigEntry<bool> OceanLogProperties;
        internal static ConfigEntry<string> OceanFloats;

        void Awake()
        {
            Instance = this;
            Log = Logger;

            MapPrefix = Config.Bind("General", "MapPrefix", 61,
                "Patches run only when the loaded map's NetworkMap.MapPrefix equals this value.");
            StockMapPrefab = Config.Bind("General", "StockMapPrefab", "Terrain1",
                "Stock map prefab (MapSettingsManager.Maps) to borrow the terrain material and DetailRenderer_Base from.");
            BundleNameContains = Config.Bind("General", "BundleNameContains", "map_terrainlab",
                "Bundle files under BepInEx/plugins whose name contains this (or map_terrainlab / map_ironjawsound, always) are searched for assets the prefab does not reference (TreePositions, LushMap, GrassMask, splats).");

            OceanEnabled = Config.Bind("7.Ocean", "Enabled", true,
                "Patch 7: tint the stock ocean (WaterMat Deep_Water_Color / Shallow_Water_Color) on our maps; other maps get the stock colours back.");
            OceanDeepColor = Config.Bind("7.Ocean", "DeepColor", "#082420",
                "Deep water colour on our maps (hex). Dark green-teal for a glacial fjord.");
            OceanShallowColor = Config.Bind("7.Ocean", "ShallowColor", "#3A7A70",
                "Shallow water colour on our maps (hex). Milky turquoise.");
            OceanStrength = Config.Bind("7.Ocean", "Strength", 0.9f,
                "0 = stock colours, 1 = exactly DeepColor/ShallowColor. Alpha always stays stock.");
            OceanFloats = Config.Bind("7.Ocean", "Floats", "_WaveHeight=1;Normal_Strength=0.25;_Whitecaps=0.2;_Wind=0.2",
                "Water material floats set on our maps (calmer sheltered sound), 'Name=value;Name=value'. Stock " +
                "WaterMat: _WaveHeight 2, Normal_Strength 0.397, _Whitecaps 0.6, _Wind 0.4. Other maps get the stock values back. Empty = stock.");
            OceanLogProperties = Config.Bind("7.Ocean", "LogProperties", false,
                "Log the water shader's property list and values once per session.");

            TerrainMaterialEnabled = Config.Bind("1.TerrainMaterial", "Enabled", true,
                "Patch 1: put GameAssets.terrainMaterial on our terrain MeshColliders so the game's identity checks treat them as terrain.");
            ColliderMaterialNames = Config.Bind("1.TerrainMaterial", "ColliderMaterialNames", "Terrain",
                "Comma-separated PhysicMaterial names to swap. Colliders with no material under the 'Terrain' child are swapped too.");
            RunwayPadsEnabled = Config.Bind("1.TerrainMaterial", "RunwayPads", true,
                "Add thin BoxColliders with no PhysicMaterial along each runway marker, so landing gear sees tarmac, not soft ground.");
            RunwayPadOverrun = Config.Bind("1.TerrainMaterial", "RunwayPadOverrun", 50f,
                "Metres of pad added past each runway end.");
            RunwayPadTolerance = Config.Bind("1.TerrainMaterial", "RunwayPadTolerance", 0.5f,
                "Skip a runway pad if the terrain under it deviates from a straight sloped line by more than this many metres.");
            RunwayPadDefaultWidth = Config.Bind("1.TerrainMaterial", "RunwayPadDefaultWidth", 45f,
                "Pad width when the runway marker name carries no width.");

            TreesEnabled = Config.Bind("2.Trees", "Enabled", true,
                "Patch 2: draw our TreePositions.bytes with the stock tree mesh, material and compute shaders. Needs the detail system (patch 4).");
            TreePositionsAsset = Config.Bind("2.Trees", "TreePositionsAsset", "TreePositions",
                "TextAsset name in our bundle.");
            LushMapAsset = Config.Bind("2.Trees", "LushMapAsset", "LushMap",
                "Texture2D name in our bundle used as TreeRenderer.LushMap. White if missing.");
            TreeAltitudeMin = Config.Bind("2.Trees", "AltitudeRangeMin", -1f,
                "TreeRenderer.AltitudeRange.Min (tree colour by altitude), metres. Negative: SnowAltitudeMin in the Winter look, the stock value otherwise.");
            TreeAltitudeMax = Config.Bind("2.Trees", "AltitudeRangeMax", -1f,
                "TreeRenderer.AltitudeRange.Max, metres. Negative: SnowAltitudeMax in the Winter look, the stock value otherwise.");
            DumpTreeMaterial = Config.Bind("2.Trees", "DumpStockMaterial", false,
                "Log every property of the stock tree material once (the names MaterialOverrides can use).");
            TreeMaterialOverrides = Config.Bind("2.Trees", "MaterialOverrides", "",
                "prop=value;... applied to a copy of the stock tree material (the stock asset is never changed). " +
                "value: #RRGGBB or #RRGGBBAA (colour), a number (float), x,y,z,w (vector), or a Texture2D name in our bundle. " +
                "Empty keeps the stock material. UNTESTED: take property names from the [2] tree material dump in the log.");

            // 2.Trees look (patch 2b, TreeLook.cs). Untested in game; every step logs with the [2b] tag.
            TreeLook = Config.Bind("2.Trees", "Look", TreeLookMode.Summer,
                "Summer (Ironjaw Sound v7 on): every tree gets the dark ConiferColor, with no frost. " +
                "Winter (v4-v6): the same, and trees higher than SnowAltitudeMin fade to a frosted, " +
                "lighter colour by SnowAltitudeMax (snow by altitude, so low valley forest stays green). Stock: the stock colours.");
            TreeModelSetting = Config.Bind("2.Trees", "Model", TreeModel.Spruce,
                "Stock: stock TreeClump1 mesh. Spruce (default since opt-1-v8): our own original spruce clump (MeshFile) with our own textures; the snow-free albedo in the Summer look, the snowy one in Winter.");
            TreeWinterLushMap = Config.Bind("2.Trees", "WinterLushMap", "White",
                "Winter look only. White: every tree takes ConiferColor fully. Ours: keep LushMapAsset (only valley forest gets it). " +
                "Any other value: a Texture2D name in our bundle (for example splat_s_fields, the snow splat).");
            TreeConiferColor = Config.Bind("2.Trees", "ConiferColor", "#1C3A20",
                "Winter: hue and saturation of every tree (_ColorLush). Brightness comes from the texture and Brightness, not from this colour.");
            TreeFrostColor = Config.Bind("2.Trees", "FrostColor", "#C9DCEB",
                "Winter: tint of high trees (_ColorAltitude).");
            TreeFrostBrighten = Config.Bind("2.Trees", "FrostBrighten", 0.45f,
                "Winter: how much lighter the high trees get (0 = same brightness, only the FrostColor hue; 1 = up to about 2x). " +
                "The shader cannot paint snow on top surfaces; this is the closest it allows.");
            TreeBrightness = Config.Bind("2.Trees", "Brightness", "0.45,0.72",
                "Winter: per-tree brightness range (_ValueVariation, stock 0.7,1.2). Lower = darker forest.");
            TreeHueJitter = Config.Bind("2.Trees", "HueJitter", "-0.03,0.03",
                "Winter: per-tree hue shift range (_HueVariation, stock -0.075,0.025).");
            TreeBaseTint = Config.Bind("2.Trees", "BaseTint", "#FFFFFF",
                "Winter: multiplies the whole texture, trunks included (_BaseColor). #FFFFFF = none.");
            TreeSnowAltitudeMin = Config.Bind("2.Trees", "SnowAltitudeMin", 150f,
                "Winter: height in metres where frost starts (AltitudeRange.Min, used when AltitudeRangeMin is negative). " +
                "On opt-1-v4, half of the trees stand below 94 m and 90 % below 323 m.");
            TreeSnowAltitudeMax = Config.Bind("2.Trees", "SnowAltitudeMax", 450f,
                "Winter: height in metres where frost is full (AltitudeRange.Max, used when AltitudeRangeMax is negative).");
            TreeSnowStrength = Config.Bind("2.Trees", "SnowStrength", 1f,
                "Winter: frost amount on the highest trees, 0..1 (TreeRenderer.AltIntensity). 0 = no frost anywhere. Negative keeps the stock value.");
            TreeLushStrength = Config.Bind("2.Trees", "LushStrength", 1f,
                "Winter: how fully trees take ConiferColor, 0..1 (TreeRenderer.LushIntensity). Negative keeps the stock value.");
            TreeAlbedoTexture = Config.Bind("2.Trees", "AlbedoTexture", "",
                "Our own albedo (_BaseMap): a PNG in BepInEx/plugins/TerrainLabNOPatch/trees/ or a Texture2D in our bundle. " +
                "Empty: stock, or our spruce texture when Model=Spruce (snow_spruce_b_none.png in Summer, snow_spruce_b_light.png in Winter). Do not use with the stock mesh (UV layout differs).");
            TreeNormalTexture = Config.Bind("2.Trees", "NormalTexture", "",
                "Our own normal map (_BumpMap), loaded linear. Empty: stock, or our spruce normal map when Model=Spruce.");
            TreeMeshFile = Config.Bind("2.Trees", "MeshFile", "snow_spruce.tlmesh",
                "Model=Spruce: our mesh file (TLM1 format, scripts/snow_spruce.py) in the trees/ folder.");
            TreeMeshRadius = Config.Bind("2.Trees", "MeshRadius", -1f,
                "Model=Spruce: TreeRenderer.meshRadius for culling. Negative keeps the stock value.");
            TreeSpruceCutoff = Config.Bind("2.Trees", "SpruceCutoff", 0.5f,
                "Model=Spruce: alpha cutoff (_Cutoff) for our spruce texture. 0 or less keeps the stock value.");
            TreeAlbedoStats = Config.Bind("2.Trees", "AlbedoStats", false,
                "Log numbers about the tree albedo (alpha coverage, mean colour, share of leaf texels). Nothing is saved.");

            TerrainShaderEnabled = Config.Bind("3.TerrainShader", "Enabled", true,
                "Patch 3: replace our tile material with a copy of the stock terrain material and bind our textures.");
            StockTerrainShader = Config.Bind("3.TerrainShader", "StockShaderName", "Shader Graphs/TerrainShader",
                "Shader name of the stock terrain material.");
            Splats = Config.Bind("3.TerrainShader", "SplatMode", SplatMode.Ours,
                "Ours: bind splats from our bundle by SplatMapping (missing ones go black). Stock: keep Heartland splats. Black: all splat weights zero.");
            SplatMapping = Config.Bind("3.TerrainShader", "SplatMapping",
                "_splat_rock=splat_s_rock;_splat_grass=splat_s_grass;_splat_lush=splat_s_lush;_splat_fields=splat_s_fields",
                "Stock splat property = our texture name. The stock splats are greyscale, one weight per texture, so bind " +
                "the stock-layout splat_s_* files (bundle built with --splats). The packed splat_a..d hold four classes each and do not fit.");
            MacroAO = Config.Bind("3.TerrainShader", "MacroAO", MacroAOMode.White,
                "_macro_ao: White texture, keep Stock, or None (null).");
            ExtraBindings = Config.Bind("3.TerrainShader", "ExtraBindings",
                "_SampleTexture2D_1a4f7e08675c47d2a231a6bf2eacea9b_Texture_1_Texture2D=snow_b;" +
                "_SampleTexture2D_a867fd84b3f5415caa454567fa1e5506_Texture_1_Texture2D=snow_n;" +
                "_SampleTexture2D_3bd9a1d970ff4fbc9888fa54a498dd5f_Texture_1_Texture2D=snow_angle;" +
                "_SampleTexture2D_5a49beebfe604041acd648420add582f_Texture_1_Texture2D=rock_b;" +
                "_SampleTexture2D_3f967dd12e9c49af9fa80c834d7c5a19_Texture_1_Texture2D=rock_n;" +
                "_SampleTexture2D_959d2f3a5ff8423b93bfc1fd0a56dd49_Texture_1_Texture2D=rock_n",
                "Extra 'property=textureAssetName;...' bindings from our bundle, applied last. Default: the stock fields " +
                "detail set (fields_b, fields_n, fields_angleVariation) is replaced by our snow_b, snow_n and snow_angle, " +
                "because our splat_s_fields carries snow and ice; from v7 the stock rocky set (rocky_b, rocky_n twice) is " +
                "replaced by our procedural rock_b and rock_n. A name missing from the bundle keeps the stock texture (logged). Empty = none.");
            DumpStockMaterial = Config.Bind("3.TerrainShader", "DumpStockMaterial", false,
                "Log every property of the stock terrain material once per session.");

            DetailSystemEnabled = Config.Bind("4.DetailSystem", "Enabled", true,
                "Patch 4: clone the stock DetailRenderer_Base (TerrainHeightMap, BlastManager, ShaderGlobalManager, DetailRenderer, GrassRenderer, TreeRenderer) onto our map and rewire it.");
            HeightMin = Config.Bind("4.DetailSystem", "HeightMin", 0f, "TerrainHeightMap.height.Min in metres.");
            HeightMax = Config.Bind("4.DetailSystem", "HeightMax", 2000f, "TerrainHeightMap.height.Max in metres.");
            Grass = Config.Bind("4.DetailSystem", "GrassMode", GrassMode.Disable,
                "Disable: grass off. Stock: stock grass with Heartland lush maps. OurMask: stock grass with our GrassMask as every lush map.");
            GrassMaskAsset = Config.Bind("4.DetailSystem", "GrassMaskAsset", "GrassMask", "Texture2D name in our bundle.");

            DiagnosticsEnabled = Config.Bind("5.Diagnostics", "Enabled", true,
                "Log map, tiles, colliders, materials, runways, tree counts, camera position and NOMapLoader errors.");
            DiagnosticsInterval = Config.Bind("5.Diagnostics", "IntervalSeconds", 0f, "Seconds between position and tree-count lines in the log. 0 = off (the default since 1.0.0).");

            AutoshotEnabled = Config.Bind("6.Autoshot", "Enabled", false,
                "Open our map in the Mission Editor from the main menu, take screenshots of the views file and quit. The environment variable TERRAINLAB_AUTOSHOT=1 (or 0) overrides this.");
            AutoshotViews = Config.Bind("6.Autoshot", "ViewsFile", "autoshot/views.txt",
                "Views file (env TERRAINLAB_AUTOSHOT_VIEWS). Relative paths are relative to the folder above the game folder.");
            AutoshotOut = Config.Bind("6.Autoshot", "OutDir", "shots",
                "Output root (env TERRAINLAB_AUTOSHOT_OUT); a <timestamp> folder is made inside (env TERRAINLAB_AUTOSHOT_STAMP names it).");
            AutoshotModes = Config.Bind("6.Autoshot", "Modes", "stock,urp,unlit",
                "Terrain material modes shot for every view (env TERRAINLAB_AUTOSHOT_MODES): stock, urp, unlit, greydetail, splatblack, nomacro, packedsplats, solo_rock|_grass|_lush|_fields, allsplats_r|g|b|a.");
            AutoshotProbeModes = Config.Bind("6.Autoshot", "ProbeModes", "",
                "Extra modes for views marked 'probe' (env TERRAINLAB_AUTOSHOT_PROBE_MODES).");
            AutoshotSettle = Config.Bind("6.Autoshot", "SettleSeconds", 6f,
                "Seconds to wait after each camera move (trees and detail windows) (env TERRAINLAB_AUTOSHOT_SETTLE).");
            AutoshotTimeOfDay = Config.Bind("6.Autoshot", "TimeOfDay", 15f,
                "Mission time of day in hours (env TERRAINLAB_AUTOSHOT_TIME). At 15 the game sun is 20 deg up from the south-west (azimuth 229); at 16.5 only 11 deg. The Blender afternoon preview sun is 29 deg at 261.");

            Release.Run();
            Diagnostics.Install();
            Autoshot.Install();

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            foreach (var m in harmony.GetPatchedMethods())
                Log.LogInfo($"Harmony patched {m.DeclaringType?.FullName}.{m.Name}");
            Log.LogInfo($"{Name} {Version} loaded. MapPrefix gate = {MapPrefix.Value}. " +
                        $"Patches: terrainMaterial={TerrainMaterialEnabled.Value} (runwayPads={RunwayPadsEnabled.Value}), " +
                        $"trees={TreesEnabled.Value}, terrainShader={TerrainShaderEnabled.Value}, detailSystem={DetailSystemEnabled.Value} " +
                        $"(grass={Grass.Value}), diagnostics={DiagnosticsEnabled.Value}");
        }

        void Start()
        {
            // All plugins have finished Awake by now.
            if (Chainloader.PluginInfos.TryGetValue("NOMapLoader", out var info))
                Log.LogInfo($"NOMapLoader found: {info.Metadata.Name} {info.Metadata.Version} at {info.Location}");
            else
                Log.LogWarning("NOMapLoader not found in Chainloader.PluginInfos (GUID 'NOMapLoader'). Our map will not load without it.");
        }
    }
}
