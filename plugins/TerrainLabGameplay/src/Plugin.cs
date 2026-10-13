using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab.Gameplay
{
    /// <summary>
    /// Read-only diagnostics for TerrainLab gameplay missions (see docs/NO-GAMEPLAY.md).
    ///
    /// It changes nothing in the game. When a mission on a map whose MapKey path starts with "TerrainLab_" (or, from
    /// v7, "IronjawSound_"; see MapIds.cs) is
    /// running, it waits for the airbases to register, then logs once:
    ///   - map, mission name, road and sea-lane counts
    ///   - each airbase: owner, custom or built-in, runways (length, level, takeoff/landing), hangars and the
    ///     aircraft each one can spawn, VTOL and service points, taxi roads
    ///   - a census of live units by faction and type
    /// After that it logs every airbase owner change. Every line starts with "[TLGameplay]".
    ///
    /// Since 0.2.0 it also copies stock map buildings onto TerrainLab maps from a manifest in
    /// buildings/&lt;map prefab&gt;.json next to the DLL (see StockBuildings.cs). Since 0.2.4 it also shows TerrainLab_*
    /// maps under the player-facing name in Map.DisplayName (see MapName.cs); internal ids are not changed.
    /// </summary>
    [BepInPlugin("talamander.terrainlab.gameplay", "TerrainLabGameplay", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> SettleSeconds;
        internal static ConfigEntry<bool> CopyBuildings;
        internal static ConfigEntry<string> CopyMode;
        internal static ConfigEntry<bool> KeepStockTilt;
        internal static ConfigEntry<bool> GroundUnits;
        internal static ConfigEntry<bool> GroundingLog;
        internal static ConfigEntry<bool> MapImageTint;
        internal static ConfigEntry<string> MapDisplayName;
        internal static ConfigEntry<bool> InstallMissions;
        internal static BepInEx.Logging.ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "Log airbases, runways, hangars, roads and units when a mission runs on our map (TerrainLab_* or IronjawSound_*).");
            SettleSeconds = Config.Bind("General", "SettleSeconds", 8f,
                "Seconds to wait after the airbases register before writing the report.");
            CopyBuildings = Config.Bind("StockBuildings", "CopyBuildings", true,
                "Copy stock buildings (houses, sheds, lighthouse, wind turbines, pylons) onto our maps (TerrainLab_*, IronjawSound_*) " +
                "from buildings/<map prefab>.json. Every multiplayer peer needs the same setting and manifest.");
            CopyMode = Config.Bind("StockBuildings", "CopyMode", "Networked",
                new ConfigDescription("Networked: copies join a network building set (damage syncs, cables drawn by " +
                    "the game). Static: plain local copies with local damage only.",
                    new AcceptableValueList<string>("Networked", "Static")));
            KeepStockTilt = Config.Bind("StockBuildings", "KeepStockTilt", false,
                "Keep the tilt a stock building has (about 12.7 degrees on suburb blocks), so copies dig in at one end and float at the other.");
            GroundUnits = Config.Bind("StockBuildings", "GroundUnits", true,
                "Move each house of a copied block up or down to follow this map's ground under it.");
            GroundingLog = Config.Bind("StockBuildings", "GroundingLog", false,
                "Log per-building ground checks (stock tilt, mesh bottom against terrain, relief).");
            MapImageTint = Config.Bind("Map", "MapImageTint", false,
                "Tint the TerrainLab map image with the colour theme's Map Background colour, as the game does. " +
                "Off shows the map image in its own colours (the tint turns our coloured relief map all green).");
            MapDisplayName = Config.Bind("Map", "DisplayName", "Ironjaw Sound",
                "Name players see for our maps (TerrainLab_* and IronjawSound_* prefabs: map picker, lobby, server list). " +
                "Prefab, bundle and map key are not changed. Empty keeps the name stored in the bundle.");
            InstallMissions = Config.Bind("Missions", "InstallMissions", true,
                "Copy this release's missions (Missions/Ironjaw Sound - * next to the DLL) into the game's mission folder at start. " +
                "Only adds or updates missions you have not edited; never deletes anything.");
            var harmony = new Harmony("talamander.terrainlab.gameplay");
            if (InstallMissions.Value)
            {
                try { MissionInstaller.Install(harmony); }
                catch (Exception e) { Logger.LogWarning($"[TLGameplay] mission install failed: {e.Message}"); }
            }
            try { StockBuildings.Patch(harmony); }
            catch (Exception e) { Logger.LogWarning($"[TLGameplay] stock buildings disabled: {e.Message}"); }
            try { MapTint.Patch(harmony); }
            catch (Exception e) { Logger.LogWarning($"[TLGameplay] map tint patch disabled: {e.Message}"); }
            try { MapName.Patch(harmony); }
            catch (Exception e) { Logger.LogWarning($"[TLGameplay] map name patch disabled: {e.Message}"); }
            // Per-frame work runs on a hidden object that survives scene loads (some setups destroy the
            // BepInEx manager object after the chainloader finishes).
            var go = new GameObject("TerrainLabGameplay");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();
            Logger.LogInfo($"[TLGameplay] TerrainLabGameplay 1.0.0 loaded (enabled={Enabled.Value}, copyBuildings={CopyBuildings.Value}, mode={CopyMode.Value}).");
        }
    }

    public class Runner : MonoBehaviour
    {
        static BepInEx.Logging.ManualLogSource Log => Plugin.Log;
        float nextPoll;
        string activeKey;          // map + mission currently tracked, null when none
        float readyAt = -1f;       // time when the report is due
        bool reported;
        readonly Dictionary<string, string> owners = new Dictionary<string, string>();
        int errors;

        void Update()
        {
            if (!Plugin.Enabled.Value || Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 2f;
            try { Poll(); }
            catch (Exception e)
            {
                if (errors++ < 5) Log.LogWarning($"[TLGameplay] poll failed: {e.GetType().Name}: {e.Message}");
            }
        }

        void Poll()
        {
            var mission = MissionManager.CurrentMission;
            string path = mission?.MapKey.Path ?? "";
            if (mission == null || !MapIds.IsOurPrefab(path))
            {
                if (activeKey != null) Log.LogInfo($"[TLGameplay] left {activeKey}");
                activeKey = null;
                owners.Clear();
                return;
            }
            string key = $"{path} / {mission.Name}";
            if (key != activeKey)
            {
                activeKey = key;
                reported = false;
                readyAt = -1f;
                owners.Clear();
                MapName.ApplyAll();
                Log.LogInfo($"[TLGameplay] mission on TerrainLab map: map={path} mission='{mission.Name}' " +
                            $"gameState={GameManager.gameState} savedAirbases={mission.airbases?.Count ?? 0} " +
                            $"savedUnits(veh/ship/bld)={mission.vehicles?.Count ?? 0}/{mission.ships?.Count ?? 0}/{mission.buildings?.Count ?? 0}");
            }
            if (!reported)
            {
                if (FactionRegistry.airbaseLookup.Count == 0) return;
                if (readyAt < 0f) { readyAt = Time.unscaledTime + Plugin.SettleSeconds.Value; return; }
                if (Time.unscaledTime < readyAt) return;
                reported = true;
                Report(mission, path);
            }
            WatchOwners();
        }

        static string FactionName(FactionHQ hq)
        {
            if (hq == null) return "none";
            try { return hq.faction != null ? hq.faction.factionName : hq.name; } catch (Exception) { return "?"; }
        }

        static int RoadCount(object network)
        {
            if (network == null) return 0;
            var roads = Traverse.Create(network).Field("roads").GetValue() as System.Collections.ICollection;
            return roads?.Count ?? 0;
        }

        void Report(NuclearOption.SavedMission.Mission mission, string path)
        {
            var sb = new StringBuilder();
            object level = null;
            try { level = NetworkSceneSingleton<LevelInfo>.i; } catch (Exception) { }
            if (level != null)
            {
                var t = Traverse.Create(level);
                sb.AppendLine($"[TLGameplay] level: roads={RoadCount(t.Property("roadNetwork").GetValue())} " +
                              $"seaLanes={RoadCount(t.Property("seaLanes").GetValue())} (map + mission roads merged)");
            }
            else sb.AppendLine("[TLGameplay] level: LevelInfo not found");

            foreach (var kv in FactionRegistry.airbaseLookup.OrderBy(k => k.Key))
            {
                var ab = kv.Value;
                if (ab == null) continue;
                var saved = ab.SavedAirbase;
                sb.AppendLine($"[TLGameplay] airbase {kv.Key} '{saved?.DisplayName}': owner={FactionName(ab.CurrentHQ)} " +
                              $"custom={ab.IsCustom} attached={ab.AttachedAirbase} disabled={ab.disabled} " +
                              $"captureRange={saved?.CaptureRange} captureDefense={saved?.CaptureDefense} pos={ab.transform.position}");
                if (ab.runways != null)
                {
                    foreach (var rw in ab.runways)
                    {
                        if (rw == null) continue;
                        string name = Traverse.Create(rw).Field("name").GetValue<string>();
                        bool lvl = false;
                        try { lvl = rw.IsLevel(); } catch (Exception) { }
                        sb.AppendLine($"[TLGameplay]   runway '{name}': length={rw.Length:F0} level={lvl} takeoff={rw.Takeoff} " +
                                      $"landing={rw.Landing} reversable={rw.Reversable} exits={rw.exitPoints?.Length ?? 0}");
                    }
                }
                var service = Traverse.Create(ab).Field("servicePoints").GetValue() as Transform[];
                sb.AppendLine($"[TLGameplay]   vtolPoints={ab.verticalLandingPoints?.Length ?? 0} servicePoints={service?.Length ?? 0} " +
                              $"taxiRoads={RoadCount(ab.GetTaxiNetwork())} buildings={ab.buildings?.Count ?? 0} " +
                              $"hangars={ab.hangars?.Count ?? 0} storage={SafeStorage(ab)}");
                if (ab.hangars != null)
                {
                    foreach (var h in ab.hangars)
                    {
                        if (h == null) continue;
                        string unit = h.attachedUnit != null ? h.attachedUnit.unitName : h.name;
                        string types = "";
                        try
                        {
                            var defs = h.GetAvailableAircraft();
                            types = defs == null ? "" : string.Join(",", defs.Where(d => d != null).Select(d => d.jsonKey));
                        }
                        catch (Exception) { types = "?"; }
                        sb.AppendLine($"[TLGameplay]     hangar {unit}: available={h.Available} disabled={h.Disabled} aircraft=[{types}]");
                    }
                }
            }

            var census = new SortedDictionary<string, int>();
            foreach (var u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled) continue;
                string type = u.definition != null ? u.definition.jsonKey : u.unitName;
                string k = $"{FactionName(u.NetworkHQ)} {u.GetType().Name} {type}";
                census[k] = census.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            sb.AppendLine($"[TLGameplay] units alive: {census.Values.Sum()}");
            foreach (var kv in census) sb.AppendLine($"[TLGameplay]   {kv.Value,4} x {kv.Key}");
            try { sb.AppendLine("[TLGameplay] " + StockBuildings.Census()); } catch (Exception) { }
            foreach (var line in sb.ToString().Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Length > 0) Log.LogInfo(l);
            }
        }

        static string SafeStorage(Airbase ab)
        {
            try { return ab.HasStorage() ? ab.GetStorage().ToString() : "0"; } catch (Exception) { return "?"; }
        }

        void WatchOwners()
        {
            foreach (var kv in FactionRegistry.airbaseLookup)
            {
                if (kv.Value == null) continue;
                string now = FactionName(kv.Value.CurrentHQ);
                if (owners.TryGetValue(kv.Key, out var was))
                {
                    if (was != now) Log.LogInfo($"[TLGameplay] airbase {kv.Key} owner {was} -> {now}");
                }
                owners[kv.Key] = now;
            }
        }
    }
}
