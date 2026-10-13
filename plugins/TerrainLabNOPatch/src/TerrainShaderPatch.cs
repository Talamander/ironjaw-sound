using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Patch 3. Our tiles ship with a URP/Lit material (_BaseMap = macro basecolor, _BumpMap = macro normal).
    /// Replace it with a copy of the stock terrain material (shader "Shader Graphs/TerrainShader") so the stock
    /// detail textures and tuning floats stay, then bind our macro maps and splats by the stock property names
    /// (_macro_basecolor, _macro_normal, _macro_ao, _splat_fields/_grass/_lush/_rock).
    /// </summary>
    static class TerrainShaderPatch
    {
        static bool dumped;
        internal static Material OurMaterial;

        internal static void Apply(MapContext ctx)
        {
            var stock = Stock.TerrainMaterial();
            if (Plugin.DumpStockMaterial.Value && !dumped && stock != null) { dumped = true; Dump(stock); }

            Material mat;
            if (stock != null) mat = new Material(stock);
            else
            {
                var sh = Shader.Find(Plugin.StockTerrainShader.Value);
                if (sh == null) { Plugin.Log.LogError($"[3] Shader '{Plugin.StockTerrainShader.Value}' not found; tiles keep '{ctx.OriginalTileMaterial?.name}'."); return; }
                Plugin.Log.LogWarning("[3] No stock terrain material; using Shader.Find. Stock detail textures and floats will be missing.");
                mat = new Material(sh);
            }
            mat.name = "TerrainLab_TerrainShader";

            var orig = ctx.OriginalTileMaterial;
            Texture baseTex = Tex(orig, "_BaseMap") ?? Tex(orig, "_MainTex") ?? OurAssets.Get<Texture2D>("macro_basecolor");
            Texture normTex = Tex(orig, "_BumpMap") ?? OurAssets.Get<Texture2D>("macro_normal");
            Bind(mat, "_macro_basecolor", baseTex, "our tile _BaseMap");
            Bind(mat, "_macro_normal", normTex, "our tile _BumpMap");

            switch (Plugin.MacroAO.Value)
            {
                case MacroAOMode.White: Bind(mat, "_macro_ao", Texture2D.whiteTexture, "white"); break;
                case MacroAOMode.None: Bind(mat, "_macro_ao", null, "null"); break;
                default: Plugin.Log.LogInfo("[3] _macro_ao kept from stock"); break;
            }

            switch (Plugin.Splats.Value)
            {
                case SplatMode.Stock:
                    Plugin.Log.LogWarning("[3] SplatMode=Stock: Heartland splats stay (wrong layout for our map, test only).");
                    break;
                case SplatMode.Black:
                    foreach (var p in new[] { "_splat_fields", "_splat_grass", "_splat_lush", "_splat_rock" })
                        Bind(mat, p, Texture2D.blackTexture, "black");
                    break;
                default:
                    foreach (var pair in ParsePairs(Plugin.SplatMapping.Value))
                    {
                        var t = OurAssets.Get<Texture2D>(pair.Value);
                        if (t == null) { Bind(mat, pair.Key, Texture2D.blackTexture, $"black ({pair.Value} missing)"); continue; }
                        Bind(mat, pair.Key, t, pair.Value);
                    }
                    break;
            }

            foreach (var pair in ParsePairs(Plugin.ExtraBindings.Value))
            {
                var t = OurAssets.Get<Texture2D>(pair.Value);
                // An older bundle without the asset keeps the stock texture rather than going null.
                if (t == null) { Plugin.Log.LogWarning($"[3] ExtraBindings: '{pair.Value}' not in our bundle, {pair.Key} keeps '{Tex(mat, pair.Key)?.name}'"); continue; }
                Bind(mat, pair.Key, t, pair.Value);
            }

            int n = 0;
            foreach (var mr in ctx.Tiles)
            {
                var mats = mr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                mr.sharedMaterials = mats;
                n++;
            }
            OurMaterial = mat;
            Plugin.Log.LogInfo($"[3] Swapped material on {n} tiles: '{orig?.name}' ({orig?.shader?.name}) to '{mat.name}' ({mat.shader.name}), keywords [{string.Join(",", mat.shaderKeywords)}]");

            // Without a ShaderGlobalManager (patch 4 off) nobody sets the world extent for the terrain shader.
            if (!Plugin.DetailSystemEnabled.Value)
            {
                Shader.SetGlobalVector("_Datum_WorldExtent", ctx.Map.MapSize / 2f);
                Plugin.Log.LogInfo($"[3] Set _Datum_WorldExtent = {ctx.Map.MapSize / 2f} (no ShaderGlobalManager)");
            }
        }

        static Texture Tex(Material m, string prop) => m != null && m.HasProperty(prop) ? m.GetTexture(prop) : null;

        static void Bind(Material m, string prop, Texture t, string what)
        {
            if (!m.HasProperty(prop)) { Plugin.Log.LogWarning($"[3] Material has no property '{prop}' (wanted {what})"); return; }
            m.SetTexture(prop, t);
            Plugin.Log.LogInfo($"[3] {prop} = {(t != null ? $"'{t.name}' {t.width}x{t.height}" : "null")} ({what})");
        }

        internal static List<KeyValuePair<string, string>> ParsePairs(string s)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            foreach (var part in s.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                list.Add(new KeyValuePair<string, string>(part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim()));
            }
            return list;
        }

        internal static void Dump(Material m, string what = "Stock terrain material")
        {
            var sh = m.shader;
            var sb = new StringBuilder();
            sb.AppendLine($"{what} '{m.name}', shader '{sh.name}', keywords [{string.Join(",", m.shaderKeywords)}], {sh.GetPropertyCount()} properties:");
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                string name = sh.GetPropertyName(i);
                var type = sh.GetPropertyType(i);
                string val;
                switch (type)
                {
                    case ShaderPropertyType.Texture:
                        var t = m.GetTexture(name);
                        val = t != null ? $"'{t.name}' {t.width}x{t.height} scale {m.GetTextureScale(name)}" : "null";
                        break;
                    case ShaderPropertyType.Color: val = m.GetColor(name).ToString(); break;
                    case ShaderPropertyType.Vector: val = m.GetVector(name).ToString(); break;
                    default: val = m.GetFloat(name).ToString("G6"); break;
                }
                sb.AppendLine($"  {name} [{type}] = {val}  ({sh.GetPropertyDescription(i)})");
            }
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
