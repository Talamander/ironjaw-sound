using System;

namespace TerrainLab.Gameplay
{
    /// <summary>
    /// The one place that decides whether a map is ours. Maps up to opt-1-v6 use the prefab prefix "TerrainLab_" and
    /// bundle files "map_terrainlab_*"; from v7 the map is built as "IronjawSound_" / "map_ironjawsound_*". Both stay
    /// accepted so v6 bundles and old demos still load. From opt-1-v7 the map has a stable name with no version:
    /// prefab "IronjawSound", bundle file "map_ironjawsound", variant "ironjawsound" (rocks_ironjawsound.bin, the
    /// buildings/IronjawSound.json manifest). (A copy of this file lives in each TerrainLab plugin.)
    /// </summary>
    static class MapIds
    {
        internal static readonly string[] PrefabPrefixes = { "TerrainLab_", "IronjawSound_" };
        internal static readonly string[] BundlePrefixes = { "map_terrainlab", "map_ironjawsound" };
        /// <summary>The stable prefab name (opt-1-v7 on) and the variant the plugins use for it.</summary>
        internal const string StablePrefab = "IronjawSound";
        internal const string StableVariant = "ironjawsound";

        /// <summary>Prefab or MapKey path name without "(Clone)".</summary>
        internal static string Clean(string name) => (name ?? "").Replace("(Clone)", "").Trim();

        /// <summary>True when a prefab name or MapKey path starts with one of our prefixes.</summary>
        internal static bool IsOurPrefab(string name) => PrefixOf(name) != null;

        /// <summary>The matching prefab prefix, or null for any other map.</summary>
        internal static string PrefixOf(string name)
        {
            string n = Clean(name);
            if (n == StablePrefab) return StablePrefab;
            foreach (var p in PrefabPrefixes)
                if (n.StartsWith(p, StringComparison.Ordinal)) return p;
            return null;
        }

        /// <summary>Variant part of the prefab name ("TerrainLab_opt_1_v6" -> "opt_1_v6", "IronjawSound" ->
        /// "ironjawsound"); the whole name if not ours.</summary>
        internal static string VariantOf(string name)
        {
            string n = Clean(name);
            if (n == StablePrefab) return StableVariant;
            string p = PrefixOf(n);
            return p == null ? n : n.Substring(p.Length);
        }

        /// <summary>True when a bundle (file) name contains one of our bundle prefixes, case-insensitive.</summary>
        internal static bool IsOurBundle(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            foreach (var p in BundlePrefixes)
                if (n.Contains(p)) return true;
            return false;
        }
    }
}
