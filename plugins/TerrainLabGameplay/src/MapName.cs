using System;
using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace TerrainLab.Gameplay
{
    /// <summary>
    /// Players see a map's name through MapDetails.MapName, read in two places: NewMissionMapButton.SetMap (the
    /// mission editor's new-mission map picker, which NOMapLoader fills with one button per custom map) and
    /// MapLoader.TryGetMapName (the lobby "map_name" and the dedicated-server map name). NOMapLoader hands the same
    /// MapDetails object to MapLoader.Maps and MapSettingsManager.Maps, so renaming that one object renames the
    /// map everywhere. On every MapDetails whose PrefabName is ours (MapIds: TerrainLab_ or IronjawSound_), MapName is set to
    /// Map.DisplayName. Prefab, bundle and MapKey stay as they are, so missions and replays still match.
    /// An empty DisplayName keeps the name from the bundle.
    /// </summary>
    static class MapName
    {
        static readonly HashSet<int> logged = new HashSet<int>();

        public static void Patch(Harmony h)
        {
            var setMap = AccessTools.Method(typeof(NewMissionMapButton), nameof(NewMissionMapButton.SetMap));
            var tryGet = AccessTools.Method(typeof(MapLoader), nameof(MapLoader.TryGetMapName));
            var menu = AccessTools.Method(typeof(MainMenu), "Start");
            if (setMap != null) h.Patch(setMap, prefix: new HarmonyMethod(typeof(MapName), nameof(SetMapPrefix)));
            else Plugin.Log.LogWarning("[TLGameplay] map name: NewMissionMapButton.SetMap not found");
            if (tryGet != null) h.Patch(tryGet, prefix: new HarmonyMethod(typeof(MapName), nameof(TryGetPrefix)));
            else Plugin.Log.LogWarning("[TLGameplay] map name: MapLoader.TryGetMapName not found");
            if (menu != null) h.Patch(menu, postfix: new HarmonyMethod(typeof(MapName), nameof(ApplyAll)));
        }

        static void SetMapPrefix(MapDetails details) => Apply(details);

        static void TryGetPrefix(MapLoader __instance)
        {
            if (__instance?.Maps == null) return;
            foreach (var d in __instance.Maps) Apply(d);
        }

        /// <summary>Renames every loaded MapDetails of ours. Also called when a mission on our map starts.</summary>
        public static void ApplyAll()
        {
            try
            {
                foreach (var d in Resources.FindObjectsOfTypeAll<MapDetails>()) Apply(d);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[TLGameplay] map name: {e.Message}"); }
        }

        static void Apply(MapDetails d)
        {
            string name = Plugin.MapDisplayName.Value;
            if (d == null || string.IsNullOrEmpty(name) || !MapIds.IsOurPrefab(d.PrefabName)) return;
            string was = d.MapName;
            if (was != name) d.MapName = name;
            if (logged.Add(d.GetInstanceID()))
                Plugin.Log.LogInfo($"[TLGameplay] map name: {name} (prefab {d.PrefabName}, bundle name '{was}')");
        }
    }
}
