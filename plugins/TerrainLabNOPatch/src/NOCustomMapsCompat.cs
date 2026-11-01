using System;
using System.Collections;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SceneLoading;
using UnityEngine;
using UnityEngine.UI;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Lets the mission editor's New Mission panel show NOMapLoader maps (Ironjaw Sound) and NOCustomMaps maps
    /// (Swiss Alps and others) at the same time.
    ///
    /// Both loaders patch <c>MissionEditorNewMenuV2.Awake</c>. NOMapLoader 1.0.5 (<c>PopupMenusPatch</c>, a
    /// prefix) always moves the game's two tiles out of the 'Map options' row into a horizontal scroll view of
    /// its own ('MapScrollRect/Viewport/Content'), even when it has no map to add, and appends one tile per map
    /// to <c>mapButtons</c>. NOCustomMaps 1.4.0 (<c>NewMissionMapTilesPatch</c>, a postfix) then checks the
    /// layout it was written for (<c>MapTileList.Unexpected</c>): the tiles' parent must sit in a
    /// VerticalLayoutGroup. After NOMapLoader that parent is 'Viewport', so NOCustomMaps logs "the New Mission
    /// panel is not laid out as this build of Custom Maps expects ('Viewport' is not a vertical layout)" and
    /// offers none of its maps.
    ///
    /// A second problem: the game's <c>Awake</c> wires tile i to <c>MapLoader.Maps[i]</c>. NOMapLoader's tiles
    /// are appended before that runs, so each one selects whatever map sits at its index. When NOCustomMaps put
    /// a map into <c>MapLoader.Maps</c> before NOMapLoader did, the Ironjaw Sound tile selects that map.
    ///
    /// This postfix runs after the game's <c>Awake</c> and before NOCustomMaps' postfix. Only when NOMapLoader
    /// and NOCustomMaps are both loaded, and NOCustomMaps is going to build its list, it puts every tile back
    /// into the game's row, removes NOMapLoader's scroll view and gives each tile whose index wiring is wrong a
    /// click that selects its own map. NOCustomMaps then builds its scrolling grid with the game's tiles, ours
    /// and its own. In every other case, or when anything is not as expected, the panel is left as it is.
    /// Config: General.NOCustomMapsCompat.
    /// </summary>
    [HarmonyPatch(typeof(MissionEditorNewMenuV2), "Awake")]
    static class NOCustomMapsCompat
    {
        internal const string NOMapLoaderGuid = "NOMapLoader";
        internal const string NOCustomMapsGuid = "com.javoski.custommaps";

        static readonly FieldInfo MapButtonsField = AccessTools.Field(typeof(MissionEditorNewMenuV2), "mapButtons");
        static readonly FieldInfo MapLoaderField = AccessTools.Field(typeof(MissionEditorNewMenuV2), "mapLoader");
        static readonly FieldInfo SelectedField = AccessTools.Field(typeof(MissionEditorNewMenuV2), "selectedMap");
        static readonly MethodInfo MapSelectedMethod = AccessTools.Method(typeof(MissionEditorNewMenuV2), "MapSelected");
        static readonly FieldInfo DetailsField = AccessTools.Field(typeof(NewMissionMapButton), "mapDetails");
        static readonly MethodInfo OnMapSelectedMethod = AccessTools.Method(typeof(NewMissionMapButton), "OnMapSelected");

        [HarmonyPriority(Priority.First)]
        [HarmonyBefore(NOCustomMapsGuid)]
        static void Postfix(MissionEditorNewMenuV2 __instance)
        {
            try { Apply(__instance); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NOCustomMaps compat: {e.GetType().Name}: {e.Message}; the New Mission panel " +
                                      "stays as NOMapLoader built it.");
            }
        }

        static void Apply(MissionEditorNewMenuV2 menu)
        {
            if (menu == null || Plugin.NOCustomMapsCompat == null || !Plugin.NOCustomMapsCompat.Value) return;
            if (!Chainloader.PluginInfos.ContainsKey(NOMapLoaderGuid) ||
                !Chainloader.PluginInfos.ContainsKey(NOCustomMapsGuid)) return;

            string why = WhyNot();
            if (why != null)
            {
                Note($"NOCustomMaps compat: not needed or not possible ({why}); the New Mission panel stays as NOMapLoader built it.");
                return;
            }
            if (MapButtonsField == null || MapLoaderField == null || SelectedField == null || MapSelectedMethod == null ||
                DetailsField == null || OnMapSelectedMethod == null)
            {
                Note("NOCustomMaps compat: this game version's New Mission panel has other members; nothing changed.");
                return;
            }

            // NOMapLoader's layout: 3-panel-layout/Center/Map options/MapScrollRect/Viewport/Content.
            Transform row = menu.transform.Find("3-panel-layout/Center/Map options");
            Transform scroll = row != null ? row.Find("MapScrollRect") : null;
            Transform content = scroll != null ? scroll.Find("Viewport/Content") : null;
            if (row == null || scroll == null || content == null) return; // NOMapLoader did not rebuild this panel

            var buttons = MapButtonsField.GetValue(menu) as NewMissionMapButton[];
            if (buttons == null || buttons.Length == 0) return;
            foreach (var b in buttons)
                if (b == null || b.transform.parent != content)
                {
                    Note("NOCustomMaps compat: the map tiles are not all in NOMapLoader's list; nothing changed.");
                    return;
                }

            // 1. Every tile back into the game's row, in mapButtons order; NOMapLoader's scroll view out of the
            //    way at once (inactive now, destroyed at the end of the frame), so the row holds only tiles.
            foreach (var b in buttons) b.transform.SetParent(row, false);
            scroll.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(scroll.gameObject);

            // 2. Clicks: a tile the game wired to another map's index selects its own map instead.
            var loader = MapLoaderField.GetValue(menu) as MapLoader;
            MapDetails[] maps = loader != null ? loader.Maps : null;
            int rewired = 0;
            for (int i = 0; i < buttons.Length; i++)
            {
                var details = DetailsField.GetValue(buttons[i]) as MapDetails;
                if (details == null) continue;
                if (maps != null && i < maps.Length && maps[i] == details) continue;
                Button button = buttons[i].Button;
                if (button == null) continue;
                button.onClick.RemoveAllListeners(); // runtime listeners only: the game's index lambda
                MapDetails mine = details;
                button.onClick.AddListener(() => MapSelectedMethod.Invoke(menu, new object[] { mine }));
                rewired++;
            }

            // 3. Highlight as the game does after a selection.
            object selected = SelectedField.GetValue(menu);
            foreach (var b in buttons) OnMapSelectedMethod.Invoke(b, new[] { selected });

            Note($"NOCustomMaps compat: {buttons.Length} map tiles handed back to the game's row for NOCustomMaps " +
                 $"({rewired} tile click(s) pointed at their own map).");
        }

        /// <summary>Why NOCustomMaps will not build its map list on this panel, or null if it will. Read from
        /// NOCustomMaps by name: MapPicker.Supported (its option and its game check) and, when its maps are
        /// already known, whether it has any. Anything not found means "leave the panel alone".</summary>
        static string WhyNot()
        {
            Type picker = AccessTools.TypeByName("CustomMaps.MapPicker");
            FieldInfo supported = picker != null ? AccessTools.Field(picker, "Supported") : null;
            if (supported == null) return "NOCustomMaps.MapPicker.Supported not found";
            if (!(supported.GetValue(null) is bool ok) || !ok) return "NOCustomMaps does not offer map tiles";

            Type loader = AccessTools.TypeByName("CustomMaps.BundleLoader");
            PropertyInfo ready = loader != null ? AccessTools.Property(loader, "Ready") : null;
            PropertyInfo mapsProp = loader != null ? AccessTools.Property(loader, "Maps") : null;
            if (ready == null || mapsProp == null) return "NOCustomMaps.BundleLoader not found";
            // Maps waits for hashing when not Ready; NOCustomMaps itself waits a frame at a time then, and builds.
            if (ready.GetValue(null) is bool isReady && isReady &&
                mapsProp.GetValue(null) is IEnumerable maps && !maps.GetEnumerator().MoveNext())
                return "NOCustomMaps has no maps";
            return null;
        }

        // Once per panel: the game has two copies (main menu and the editor's File > New), each woken once.
        static void Note(string message) => Plugin.Log.LogInfo(message);
    }
}
