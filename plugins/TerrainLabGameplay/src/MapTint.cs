using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TerrainLab.Gameplay
{
    /// <summary>
    /// The game multiplies the map sprite by the colour theme's Map Background colour (DynamicMap.LoadMapImage and
    /// the theme-change handler). Stock map images are drawn for that tint; ours is a coloured shaded-relief map,
    /// so the tint turns it all green. On our maps (MapIds: TerrainLab_*, IronjawSound_*) the image colour is set back to white, keeping the
    /// theme's alpha. MapImageTint = true restores the game's behaviour.
    /// </summary>
    static class MapTint
    {
        static bool ours;

        public static void Patch(Harmony h)
        {
            var load = AccessTools.Method(typeof(DynamicMap), nameof(DynamicMap.LoadMapImage));
            var theme = AccessTools.Method(typeof(DynamicMap), "DynamicMap_OnThemeGroupChanged");
            if (load != null) h.Patch(load, postfix: new HarmonyMethod(typeof(MapTint), nameof(LoadPostfix)));
            else Plugin.Log.LogWarning("[TLGameplay] map tint: DynamicMap.LoadMapImage not found");
            if (theme != null) h.Patch(theme, postfix: new HarmonyMethod(typeof(MapTint), nameof(Untint)));
        }

        static void LoadPostfix(MapSettings mapSettings)
        {
            ours = mapSettings != null && MapIds.IsOurPrefab(mapSettings.gameObject.name);
            Untint();
            if (ours) Plugin.Log.LogInfo($"[TLGameplay] map tint: {(Plugin.MapImageTint.Value ? "theme tint kept" : "map image shown untinted")}");
        }

        static void Untint()
        {
            if (!ours || Plugin.MapImageTint.Value) return;
            try
            {
                var map = SceneSingleton<DynamicMap>.i;
                if (map == null) return;
                var img = map.mapImage.GetComponent<Image>();
                img.color = new Color(1f, 1f, 1f, img.color.a);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[TLGameplay] map tint: {e.Message}"); }
        }
    }
}
