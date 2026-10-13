using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Looks up assets from our own map bundle that the map prefab does not reference (TreePositions.bytes,
    /// LushMap, GrassMask, splats). NOMapLoader calls bundle.Unload(false) right after loading the prefab, so
    /// these are not in memory any more. Order: already-loaded objects, any still-open bundle whose name
    /// matches, then re-open the bundle file from BepInEx/plugins. Results are cached per session.
    /// </summary>
    static class OurAssets
    {
        static readonly Dictionary<string, UnityEngine.Object> cache = new Dictionary<string, UnityEngine.Object>();
        static readonly List<AssetBundle> opened = new List<AssetBundle>();
        static bool searchedFiles;
        static string variant = "";

        internal static void SetVariant(string v) { variant = (v ?? "").ToLowerInvariant(); }

        // Our bundles: the built-in MapIds prefixes (map_terrainlab, map_ironjawsound) plus General.BundleNameContains.
        static bool IsOurs(string name)
        {
            string extra = (Plugin.BundleNameContains.Value ?? "").Trim().ToLowerInvariant();
            return MapIds.IsOurBundle(name) || (extra.Length > 0 && (name ?? "").ToLowerInvariant().Contains(extra));
        }

        static string Filter() => string.Join("|", MapIds.BundlePrefixes) + "|" + Plugin.BundleNameContains.Value;

        internal static T Get<T>(string name) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(name)) return null;
            string key = typeof(T).Name + ":" + name;
            if (cache.TryGetValue(key, out var hit) && hit != null) return (T)hit;

            T found = null;
            string filter = Filter();
            foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (b == null || !IsOurs(b.name)) continue;
                found = TryLoad<T>(b, name);
                if (found != null) { Plugin.Log.LogInfo($"Asset '{name}' ({typeof(T).Name}) from loaded bundle '{b.name}'"); break; }
            }
            if (found == null)
            {
                OpenBundleFiles();
                foreach (var b in opened)
                {
                    found = TryLoad<T>(b, name);
                    if (found != null) { Plugin.Log.LogInfo($"Asset '{name}' ({typeof(T).Name}) from re-opened bundle '{b.name}'"); break; }
                }
            }
            if (found == null) Plugin.Log.LogWarning($"Asset '{name}' ({typeof(T).Name}) not found in any '{filter}' bundle.");
            cache[key] = found;
            return found;
        }

        static T TryLoad<T>(AssetBundle b, string name) where T : UnityEngine.Object
        {
            try
            {
                var a = b.LoadAsset<T>(name);
                if (a != null) return a;
                // Fall back to a case-insensitive file-name match over the bundle's asset paths.
                foreach (var path in b.GetAllAssetNames())
                    if (Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        a = b.LoadAsset<T>(path);
                        if (a != null) return a;
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"LoadAsset '{name}' from '{b.name}' threw: {e.Message}"); }
            return null;
        }

        static void OpenBundleFiles()
        {
            if (searchedFiles) return;
            searchedFiles = true;
            string filter = Filter();
            List<string> files;
            try
            {
                files = Directory.GetFiles(Paths.PluginPath, "*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        string n = Path.GetFileName(f).ToLowerInvariant();
                        string ext = Path.GetExtension(n);
                        return IsOurs(n) && ext != ".manifest" && ext != ".dll" && ext != ".cfg" && ext != ".txt" && ext != ".md";
                    })
                    // Prefer the release folder (next to this DLL), then a file that names our variant.
                    .OrderByDescending(f => TerrainLab.NOPatch.Release.Dir != null && string.Equals(Path.GetDirectoryName(f), TerrainLab.NOPatch.Release.Dir, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => variant.Length > 0 && Path.GetFileName(f).ToLowerInvariant().Contains(variant))
                    .ToList();
            }
            catch (Exception e) { Plugin.Log.LogError($"Bundle file search under {Paths.PluginPath} failed: {e.Message}"); return; }

            if (files.Count == 0) Plugin.Log.LogWarning($"No bundle file containing '{filter}' under {Paths.PluginPath}.");
            foreach (var f in files)
            {
                var b = AssetBundle.LoadFromFile(f);
                if (b == null) { Plugin.Log.LogWarning($"Could not re-open bundle {f} (maybe still loaded under another name)."); continue; }
                Plugin.Log.LogInfo($"Re-opened bundle {f} ({b.GetAllAssetNames().Length} assets)");
                opened.Add(b);
            }
        }

        /// <summary>Unload re-opened bundles. Loaded assets stay alive (Unload(false)).</summary>
        internal static void Release()
        {
            foreach (var b in opened)
                if (b != null) b.Unload(false);
            if (opened.Count > 0) Plugin.Log.LogInfo($"Released {opened.Count} re-opened bundle(s), assets kept.");
            opened.Clear();
            searchedFiles = false;
        }
    }
}
