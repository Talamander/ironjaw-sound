using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Release identity and map registration for the packaged Ironjaw Sound release (1.0.0 and later).
    ///
    /// 1. Reads IronjawSound.release.json next to this DLL, logs "Ironjaw Sound &lt;version&gt;" and checks that every
    ///    listed file is present with the listed size (a cheap check for a broken download or a half-copied folder).
    /// 2. Registers our map bundle with NOMapLoader. NOMapLoader 1.0.5 only scans its own folder
    ///    (BepInEx/plugins/NOMapLoader), and a mod manager installs this release into its own folder, so this plugin
    ///    loads the bundle the same way NOMapLoader does and adds it to NOMapLoader's map table. NOMapLoader reads
    ///    that table later (main menu and MapSettingsManager), so adding to it after NOMapLoader's Awake is enough.
    /// 3. Warns about stale copies of our bundles elsewhere under BepInEx/plugins (for example the v9 layout put
    ///    map_ironjawsound into BepInEx/plugins/NOMapLoader).
    /// </summary>
    static class Release
    {
        internal const string ManifestName = "IronjawSound.release.json";
        internal static string Dir;
        internal static string ReleaseVersion = "";

        // The manifest is written by scripts/package_release.py: top-level name, version, date, then "files" with
        // {"path", "size", "sha256"} objects. Parsed with regexes (Unity's JsonUtility does not fill the nested array
        // here, and the plugin has no JSON library reference).
        static string Field(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        internal static void Run()
        {
            try { Dir = Path.GetDirectoryName(typeof(Release).Assembly.Location); }
            catch { Dir = null; }
            if (string.IsNullOrEmpty(Dir)) return;
            try { CheckManifest(); } catch (Exception e) { Plugin.Log.LogWarning($"Release manifest check failed: {e.Message}"); }
            try { RegisterMaps(); } catch (Exception e) { Plugin.Log.LogError($"Map registration failed: {e}"); }
            try { WarnStaleCopies(); } catch (Exception e) { Plugin.Log.LogWarning($"Stale copy check failed: {e.Message}"); }
        }

        static void CheckManifest()
        {
            string path = Path.Combine(Dir, ManifestName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"No {ManifestName} next to the plugin (development build).");
                return;
            }
            string json = File.ReadAllText(path);
            string name = Field(json, "name"), version = Field(json, "version"), date = Field(json, "date");
            ReleaseVersion = version;
            var bad = new List<string>();
            int count = 0;
            foreach (Match f in Regex.Matches(json, "\"path\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"size\"\\s*:\\s*(\\d+)"))
            {
                count++;
                string rel = f.Groups[1].Value;
                long size = long.Parse(f.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                string p = Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p)) bad.Add($"{rel} (missing)");
                else if (new FileInfo(p).Length != size) bad.Add($"{rel} (size {new FileInfo(p).Length}, expected {size})");
            }
            if (bad.Count == 0)
                Plugin.Log.LogInfo($"{name} {version} ({date}), {count} files present with the expected sizes.");
            else
                Plugin.Log.LogError($"{name} {version}: {bad.Count} file(s) do not match the release. Reinstall the release zip. " +
                                    string.Join("; ", bad));
        }

        static void RegisterMaps()
        {
            if (!Chainloader.PluginInfos.TryGetValue("NOMapLoader", out var nml))
            {
                Plugin.Log.LogError("NOMapLoader is not installed. Ironjaw Sound needs NOMapLoader 1.0.5 or later " +
                                    "(https://github.com/MinecrackTyler/NOMapLoader/releases).");
                return;
            }
            string nmlDir = Path.GetDirectoryName(nml.Location);
            if (SamePath(nmlDir, Dir))
            {
                Plugin.Log.LogInfo("Installed inside the NOMapLoader folder; NOMapLoader loads the map bundle itself.");
                return;
            }

            var pluginType = AccessTools.TypeByName("NOMapLoader.Plugin");
            var dataType = AccessTools.TypeByName("NOMapLoader.CustomMapData");
            var tableField = pluginType == null ? null : AccessTools.Field(pluginType, "CustomMaps");
            var table = tableField?.GetValue(null) as IDictionary;
            if (dataType == null || table == null)
            {
                Plugin.Log.LogError($"This NOMapLoader version ({nml.Metadata.Version}) has no map table this plugin knows. " +
                                    "Copy map_ironjawsound into BepInEx/plugins/NOMapLoader as a workaround.");
                return;
            }

            var bundles = Directory.GetFiles(Dir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => MapIds.IsOurBundle(Path.GetFileName(f).ToLowerInvariant())
                            && Path.GetFileName(f).ToLowerInvariant().StartsWith("map_")
                            && Path.GetExtension(f).Length == 0)
                .ToList();
            foreach (var file in bundles)
            {
                var bundle = AssetBundle.LoadFromFile(file);
                if (bundle == null)
                {
                    Plugin.Log.LogError($"Could not load map bundle {file}. Another copy with the same bundle name may be loaded.");
                    continue;
                }
                int added = 0;
                foreach (var details in bundle.LoadAllAssets<MapDetails>())
                {
                    if (table.Contains(details.PrefabName))
                        Plugin.Log.LogWarning($"Map '{details.PrefabName}' was already loaded from another copy (likely an old " +
                                              $"map_ironjawsound in {nmlDir}). Using this release's copy instead. Delete the old copy " +
                                              "to save memory and loading time.");
                    var prefab = bundle.LoadAsset<GameObject>(details.PrefabName);
                    var settings = prefab != null ? prefab.GetComponent<MapSettings>() : null;
                    if (settings == null)
                    {
                        Plugin.Log.LogError($"Map bundle {Path.GetFileName(file)}: prefab or MapSettings '{details.PrefabName}' not found.");
                        continue;
                    }
                    var data = Activator.CreateInstance(dataType);
                    AccessTools.Field(dataType, "Details").SetValue(data, details);
                    AccessTools.Field(dataType, "Settings").SetValue(data, settings);
                    AccessTools.Field(dataType, "Prefab").SetValue(data, prefab);
                    table[details.PrefabName] = data;
                    added++;
                    Plugin.Log.LogInfo($"Registered map '{details.PrefabName}' from {Path.GetFileName(file)} with NOMapLoader {nml.Metadata.Version}.");
                }
                // Same as NOMapLoader: keep the loaded assets, release the bundle file. OurAssets re-opens it later.
                bundle.Unload(false);
                if (added == 0) Plugin.Log.LogError($"Map bundle {Path.GetFileName(file)} holds no map.");
            }
            if (bundles.Count == 0)
                Plugin.Log.LogWarning($"No map bundle next to the plugin in {Dir}.");
        }

        static void WarnStaleCopies()
        {
            if (!File.Exists(Path.Combine(Dir, ManifestName))) return; // development layout: copies are expected
            var stale = Directory.GetFiles(Paths.PluginPath, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    return (MapIds.IsOurBundle(n) || n == "terrainlab_rocks" || n.StartsWith("terrainlab") && n.EndsWith(".dll"))
                           && Path.GetExtension(n) != ".manifest"
                           && !SamePath(Path.GetDirectoryName(f), Dir);
                })
                .ToList();
            foreach (var f in stale)
                Plugin.Log.LogWarning($"Old Ironjaw Sound file outside this release folder: {f}. Delete it (see the README, " +
                                      "\"Updating from v9\").");
        }

        static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
