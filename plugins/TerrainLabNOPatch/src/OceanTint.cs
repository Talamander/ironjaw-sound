using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Patch 7: ocean colour per map. The stock ocean is one material, LevelInfo.waterMaterial ("WaterMat", shader
    /// "Shader Graphs/WaterSurface"). Its colours live only in the material (Deep_Water_Color, Shallow_Water_Color);
    /// no game code writes them, so a one-time SetColor sticks for the session. On our maps the colours are pulled
    /// toward the configured fjord colours; on any other map the cached stock values are put back, so stock maps
    /// look unchanged after visiting ours. The material is changed in place (never copied), because the game and
    /// the water renderers share that one instance.
    /// </summary>
    static class OceanTint
    {
        static readonly Dictionary<string, Color> original = new Dictionary<string, Color>();
        static readonly Dictionary<string, float> originalF = new Dictionary<string, float>();
        static Material cachedMat;
        static bool logged;

        internal static void Apply(LevelInfo li, bool ours)
        {
            if (li == null) return;
            var mat = AccessTools.Field(typeof(LevelInfo), "waterMaterial")?.GetValue(li) as Material;
            if (mat == null) { Plugin.Log.LogWarning("[7 ocean] LevelInfo.waterMaterial is null; ocean left as is."); return; }
            if (mat != cachedMat) { original.Clear(); originalF.Clear(); cachedMat = mat; }
            if (!logged && Plugin.OceanLogProperties.Value) { LogProperties(mat); logged = true; }

            var targets = new[] {
                ("Deep_Water_Color", Plugin.OceanDeepColor.Value),
                ("Shallow_Water_Color", Plugin.OceanShallowColor.Value),
            };
            foreach (var (prop, hex) in targets)
            {
                if (!mat.HasProperty(prop)) { Plugin.Log.LogWarning($"[7 ocean] {mat.name} has no {prop}; skipped."); continue; }
                if (!original.ContainsKey(prop)) original[prop] = mat.GetColor(prop);
                Color stock = original[prop];
                if (!ours || !Plugin.OceanEnabled.Value)
                {
                    mat.SetColor(prop, stock);
                    continue;
                }
                if (!TryHex(hex, out Color want)) { Plugin.Log.LogWarning($"[7 ocean] bad colour '{hex}' for {prop}; skipped."); continue; }
                float k = Mathf.Clamp01(Plugin.OceanStrength.Value);
                // Blend in RGB only; the stock alpha (transparency) is kept.
                Color c = Color.Lerp(stock, want, k);
                c.a = stock.a;
                mat.SetColor(prop, c);
                Plugin.Log.LogInfo($"[7 ocean] {prop}: stock {Fmt(stock)} -> {Fmt(c)} (target {hex}, strength {k:0.00})");
            }
            ApplyFloats(mat, ours && Plugin.OceanEnabled.Value);
            if (!ours) Plugin.Log.LogInfo("[7 ocean] not our map: stock ocean colours and floats restored.");
        }

        /// <summary>Wave floats (wave height, normal strength, whitecaps, wind) are material constants as well: no
        /// game code writes them, so the sea state is set here per map. Every float ever changed is restored to its
        /// cached stock value when ours is false.</summary>
        static void ApplyFloats(Material mat, bool ours)
        {
            var want = new Dictionary<string, float>();
            if (ours)
            {
                foreach (var part in (Plugin.OceanFloats.Value ?? "").Split(';'))
                {
                    var kv = part.Split('=');
                    if (kv.Length != 2) continue;
                    string name = kv[0].Trim();
                    if (name.Length == 0) continue;
                    if (!float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    { Plugin.Log.LogWarning($"[7 ocean] bad float '{part}'; skipped."); continue; }
                    if (!mat.HasProperty(name)) { Plugin.Log.LogWarning($"[7 ocean] {mat.name} has no {name}; skipped."); continue; }
                    want[name] = f;
                }
            }
            foreach (var name in want.Keys)
                if (!originalF.ContainsKey(name)) originalF[name] = mat.GetFloat(name);
            foreach (var kv in originalF)
            {
                float v = want.TryGetValue(kv.Key, out float w) ? w : kv.Value;
                mat.SetFloat(kv.Key, v);
                if (ours) Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "[7 ocean] {0}: stock {1:0.###} -> {2:0.###}", kv.Key, kv.Value, v));
            }
        }

        static void LogProperties(Material mat)
        {
            var sh = mat.shader;
            Plugin.Log.LogInfo($"[7 ocean] water material '{mat.name}', shader '{sh?.name}', {sh?.GetPropertyCount() ?? 0} properties:");
            if (sh == null) return;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                string n = sh.GetPropertyName(i);
                var t = sh.GetPropertyType(i);
                string v = "";
                try
                {
                    switch (t)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color: v = Fmt(mat.GetColor(n)); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector: v = mat.GetVector(n).ToString("0.###"); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range: v = mat.GetFloat(n).ToString("0.###", CultureInfo.InvariantCulture); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture: v = mat.GetTexture(n)?.name ?? "null"; break;
                    }
                }
                catch (Exception e) { v = "? " + e.Message; }
                Plugin.Log.LogInfo($"[7 ocean]   {n} ({t}) = {v}");
            }
        }

        static string Fmt(Color c) => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", c.r, c.g, c.b, c.a);

        static bool TryHex(string s, out Color c)
        {
            c = Color.black;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (!s.StartsWith("#")) s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out c);
        }
    }
}
