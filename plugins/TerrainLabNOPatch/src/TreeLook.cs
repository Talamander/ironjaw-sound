using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NuclearOption.Effects;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainLab.NOPatch
{
    public enum TreeLookMode { Stock, Winter, Summer }
    public enum TreeModel { Stock, Spruce }

    /// <summary>
    /// Patch 2b: the winter tree look. Everything here works on a COPY of the stock tree material and on our own
    /// TreeRenderer clone; the stock asset is never changed, and no stock texture or mesh is read back or saved.
    ///
    /// The stock shader (Shader Graphs/TreeShader_Instanced) recolours only "leaf" texels (green ones) in HSV:
    /// hue and saturation come from _ColorLush (weighted per tree by LushMap * LushIntensity) and then
    /// _ColorAltitude (weighted by smoothstep(AltitudeRange) * AltIntensity); brightness comes from the texture
    /// times _ValueVariation. So the Winter look gives every tree a dark conifer hue (LushMap white), and lets the
    /// altitude channel turn high trees frosted. See docs/BEPINEX-PATCH.md, "Tree shader maths".
    ///
    /// Optional (off by default): our own spruce mesh (MeshFile) and our own procedural snow-on-needles textures
    /// (AlbedoTexture, NormalTexture), loaded from files beside the plugin.
    /// </summary>
    static class TreeLook
    {
        static string pluginDir;
        static string PluginDir
        {
            get
            {
                if (pluginDir != null) return pluginDir;
                try { pluginDir = Path.GetDirectoryName(Plugin.Instance.Info.Location); }
                catch { pluginDir = Path.Combine(BepInEx.Paths.PluginPath, "TerrainLabNOPatch"); }
                return pluginDir;
            }
        }

        /// <summary>Set up the look on our TreeRenderer before the detail system activates (CommandSetup reads
        /// mesh and material in DetailRenderer.Start). Returns false only if nothing could be applied.</summary>
        internal static void Apply(TreeRenderer tr, Material stockMat)
        {
            var mesh = DetailSystemPatch.Get(tr, "mesh") as Mesh;
            LogRendererColourFields(tr, "stock");
            LogMesh(mesh, "stock");
            if (Plugin.TreeAlbedoStats.Value && stockMat != null && !GameManager.IsHeadless)
                Plugin.Log.LogInfo($"[2b] Stock tree albedo stats: {AlbedoStats(stockMat.HasProperty("_BaseMap") ? stockMat.GetTexture("_BaseMap") : null, stockMat)}");

            var look = Plugin.TreeLook.Value;
            var model = Plugin.TreeModelSetting.Value;
            bool anyOverride = look != TreeLookMode.Stock || model != TreeModel.Stock
                               || !string.IsNullOrWhiteSpace(Plugin.TreeAlbedoTexture.Value)
                               || !string.IsNullOrWhiteSpace(Plugin.TreeNormalTexture.Value)
                               || !string.IsNullOrWhiteSpace(Plugin.TreeMaterialOverrides.Value);
            if (!anyOverride) { Plugin.Log.LogInfo("[2b] Look=Stock, Model=Stock, no overrides: stock tree material and mesh kept."); return; }

            // 1. Mesh (optional).
            if (model == TreeModel.Spruce)
            {
                var ours = LoadMesh(Plugin.TreeMeshFile.Value);
                if (ours != null)
                {
                    DetailSystemPatch.Set(tr, "mesh", ours);
                    if (Plugin.TreeMeshRadius.Value > 0f) DetailSystemPatch.Set(tr, "meshRadius", Plugin.TreeMeshRadius.Value);
                    LogMesh(ours, "ours");
                }
                else Plugin.Log.LogWarning($"[2b] Model=Spruce but mesh '{Plugin.TreeMeshFile.Value}' could not be loaded; keeping the stock mesh. " +
                                           "Our spruce textures are NOT bound on the stock mesh (its UV layout is unknown).");
                model = ours != null ? TreeModel.Spruce : TreeModel.Stock;
            }

            // 2. Material copy.
            if (stockMat == null) { Plugin.Log.LogWarning("[2b] No stock tree material; look not applied."); return; }
            var m = new Material(stockMat) { name = stockMat.name + " (terrainlab " + look + "/" + model + ")" };
            int set = 0;

            if (look == TreeLookMode.Winter || look == TreeLookMode.Summer)
            {
                // Summer (v7): the Winter conifer colouring without frost (AltIntensity 0, _ColorAltitude stock).
                set += SetColor(m, "_ColorLush", Plugin.TreeConiferColor.Value, "ConiferColor (hue+sat of every tree)");
                if (look == TreeLookMode.Winter) set += SetFrost(m);
                set += SetVec2(m, "_ValueVariation", Plugin.TreeBrightness.Value, "Brightness");
                set += SetVec2(m, "_HueVariation", Plugin.TreeHueJitter.Value, "HueJitter");
                set += SetColor(m, "_BaseColor", Plugin.TreeBaseTint.Value, "BaseTint");
                WinterRendererFields(tr);
            }

            // Textures: explicit settings win; Model=Spruce defaults to our spruce textures.
            string albedo = Plugin.TreeAlbedoTexture.Value, normal = Plugin.TreeNormalTexture.Value;
            if (model == TreeModel.Spruce)
            {
                if (string.IsNullOrWhiteSpace(albedo)) albedo = Plugin.TreeLook.Value == TreeLookMode.Summer ? "snow_spruce_b_none.png" : "snow_spruce_b_light.png";
                if (string.IsNullOrWhiteSpace(normal)) normal = "snow_spruce_n.png";
            }
            else if (!string.IsNullOrWhiteSpace(albedo) || !string.IsNullOrWhiteSpace(normal))
                Plugin.Log.LogWarning("[2b] Albedo/NormalTexture set with the STOCK mesh: the stock clump is alpha-cut cards in its own UV layout, " +
                                      "so our texture will not line up (expect solid or speckled cards). Test only.");
            if (!string.IsNullOrWhiteSpace(albedo))
            {
                var t = LoadTexture(albedo, linear: false);
                if (t != null) set += SetTex(m, "_BaseMap", t, albedo);
                else Plugin.Log.LogWarning($"[2b] AlbedoTexture '{albedo}' not found; _BaseMap stays stock.");
            }
            if (!string.IsNullOrWhiteSpace(normal))
            {
                var t = LoadTexture(normal, linear: true);
                if (t != null) set += SetTex(m, "_BumpMap", t, normal);
                else Plugin.Log.LogWarning($"[2b] NormalTexture '{normal}' not found; _BumpMap stays stock.");
            }
            if (model == TreeModel.Spruce && Plugin.TreeSpruceCutoff.Value > 0f)
                set += SetFloat(m, "_Cutoff", Plugin.TreeSpruceCutoff.Value, "SpruceCutoff");

            set += RawOverrides(m);

            DetailSystemPatch.Set(tr, "material", m);
            Plugin.Log.LogInfo($"[2b] Tree material copy '{m.name}' in use with {set} change(s); shader '{m.shader?.name}', keywords [{string.Join(",", m.shaderKeywords)}].");
            LogRendererColourFields(tr, "final");
            if (Plugin.TreeAlbedoStats.Value && !GameManager.IsHeadless && model == TreeModel.Spruce)
                Plugin.Log.LogInfo($"[2b] Our tree albedo stats: {AlbedoStats(m.GetTexture("_BaseMap"), m)}");
        }

        // ---------- Winter look pieces ----------

        static int SetFrost(Material m)
        {
            float b = Plugin.TreeFrostBrighten.Value;
            if (!ColorUtility.TryParseHtmlString(Plugin.TreeFrostColor.Value, out var tint))
            {
                Plugin.Log.LogWarning($"[2b] FrostColor '{Plugin.TreeFrostColor.Value}' is not #RRGGBB; _ColorAltitude stays stock.");
                return 0;
            }
            if (!m.HasProperty("_ColorAltitude")) { Plugin.Log.LogWarning("[2b] Tree material has no _ColorAltitude; frost not applied."); return 0; }
            if (b <= 0f) return SetColor(m, "_ColorAltitude", Plugin.TreeFrostColor.Value, "FrostColor (plain)");
            var v = EncodeFrost(tint, b);
            // SetVector stores the raw (linear) value; SetColor would gamma-convert and break on negative values.
            m.SetVector("_ColorAltitude", v);
            Plugin.Log.LogInfo($"[2b] _ColorAltitude = raw {Fmt(v)} (FrostColor {Plugin.TreeFrostColor.Value}, FrostBrighten {b:G3}: high trees get " +
                               $"value x (1 + {b:G3} x (1 - hue)), toward the frost tint)");
            return 1;
        }

        /// <summary>All-negative linear colour whose shader HSV is (complement of the tint hue, -brighten). Mirrors
        /// scripts/tree_look.py encode_frost.</summary>
        internal static Vector4 EncodeFrost(Color tintGamma, float brighten)
        {
            var lin = tintGamma.linear;
            Color.RGBToHSV(lin, out float th, out _, out _);
            float he = Mathf.Repeat(th + 0.5f, 1f);
            const float V = -0.5f;
            float D = brighten * -V;
            float[] k = { he + 1f, he + 2f / 3f, he + 1f / 3f };
            var c = new float[3];
            for (int i = 0; i < 3; i++)
            {
                float hueRgb = Mathf.Clamp01(Mathf.Abs(Mathf.Repeat(k[i], 1f) * 6f - 3f) - 1f);
                c[i] = V - D * (1f - hueRgb);
            }
            return new Vector4(c[0], c[1], c[2], 1f);
        }

        static void WinterRendererFields(TreeRenderer tr)
        {
            // LushMap: white makes every tree take the conifer hue fully; "ours" keeps the bundle LushMap (valley forest).
            string lm = Plugin.TreeWinterLushMap.Value?.Trim() ?? "";
            if (lm.Equals("white", StringComparison.OrdinalIgnoreCase))
            {
                tr.LushMap = Texture2D.whiteTexture;
                Plugin.Log.LogInfo("[2b] LushMap = white (every tree gets ConiferColor)");
            }
            else if (lm.Length > 0 && !lm.Equals("ours", StringComparison.OrdinalIgnoreCase))
            {
                var t = OurAssets.Get<Texture2D>(lm);
                if (t != null) { tr.LushMap = t; Plugin.Log.LogInfo($"[2b] LushMap = our '{t.name}' {t.width}x{t.height}"); }
                else Plugin.Log.LogWarning($"[2b] WinterLushMap '{lm}' not in our bundle; LushMap stays '{tr.LushMap?.name}'.");
            }
            else Plugin.Log.LogInfo($"[2b] LushMap stays '{tr.LushMap?.name}'");

            // Altitude window for the frost. Explicit AltitudeRangeMin/Max (>= 0) were applied in TreeSetup and win.
            if (Plugin.TreeAltitudeMin.Value < 0f) tr.AltitudeRange.Min = Plugin.TreeSnowAltitudeMin.Value;
            if (Plugin.TreeAltitudeMax.Value < 0f) tr.AltitudeRange.Max = Plugin.TreeSnowAltitudeMax.Value;
            if (tr.AltitudeRange.Max <= tr.AltitudeRange.Min + 1f)
            {
                Plugin.Log.LogWarning($"[2b] AltitudeRange {tr.AltitudeRange.Min}..{tr.AltitudeRange.Max} is empty; using min..min+300.");
                tr.AltitudeRange.Max = tr.AltitudeRange.Min + 300f;
            }
            SetField(tr, "AltIntensity", Plugin.TreeLook.Value == TreeLookMode.Summer ? 0f : Plugin.TreeSnowStrength.Value);
            SetField(tr, "LushIntensity", Plugin.TreeLushStrength.Value);
            Plugin.Log.LogInfo($"[2b] Frost by altitude: AltitudeRange {tr.AltitudeRange.Min}..{tr.AltitudeRange.Max} m, SnowStrength (AltIntensity) {Plugin.TreeSnowStrength.Value}, LushStrength {Plugin.TreeLushStrength.Value}");
        }

        static void SetField(TreeRenderer tr, string field, float value)
        {
            if (value < 0f) { Plugin.Log.LogInfo($"[2b] TreeRenderer.{field} kept ({DetailSystemPatch.Get(tr, field)})"); return; }
            var f = HarmonyLib.AccessTools.Field(typeof(TreeRenderer), field);
            if (f == null || f.FieldType != typeof(float)) { Plugin.Log.LogWarning($"[2b] TreeRenderer.{field} not found (game update?); not set."); return; }
            f.SetValue(tr, value);
        }

        static void LogRendererColourFields(TreeRenderer tr, string what)
        {
            var sb = new StringBuilder($"[2b] TreeRenderer colour fields ({what}):");
            foreach (var n in new[] { "AltIntensity", "LushIntensity", "LargeNoiseScale", "NoiseSmallScale", "BlastIntensity", "VisibleBufferSizePercent", "meshRadius", "viewRadius" })
            {
                var f = HarmonyLib.AccessTools.Field(typeof(TreeRenderer), n);
                sb.Append($" {n}={(f != null ? Convert.ToString(f.GetValue(tr), CultureInfo.InvariantCulture) : "missing")}");
            }
            sb.Append($" AltitudeRange={tr.AltitudeRange.Min}..{tr.AltitudeRange.Max} LushMap='{(tr.LushMap != null ? tr.LushMap.name : "null")}'");
            Plugin.Log.LogInfo(sb.ToString());
        }

        // ---------- property setters (each checks HasProperty and logs) ----------

        static int SetColor(Material m, string prop, string hex, string what)
        {
            if (string.IsNullOrWhiteSpace(hex)) return 0;
            if (!m.HasProperty(prop)) { Plugin.Log.LogWarning($"[2b] Tree material has no '{prop}' ({what}); skipped."); return 0; }
            if (!ColorUtility.TryParseHtmlString(hex.Trim(), out var c)) { Plugin.Log.LogWarning($"[2b] {what} '{hex}' is not #RRGGBB; {prop} unchanged."); return 0; }
            var old = m.GetColor(prop);
            m.SetColor(prop, c);
            Plugin.Log.LogInfo($"[2b] {prop} = {hex.Trim()} ({what}; was {old})");
            return 1;
        }

        static int SetVec2(Material m, string prop, string pair, string what)
        {
            if (string.IsNullOrWhiteSpace(pair)) return 0;
            if (!m.HasProperty(prop)) { Plugin.Log.LogWarning($"[2b] Tree material has no '{prop}' ({what}); skipped."); return 0; }
            var p = pair.Split(',');
            if (p.Length < 2 || !float.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                || !float.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            { Plugin.Log.LogWarning($"[2b] {what} '{pair}' is not 'min,max'; {prop} unchanged."); return 0; }
            var old = m.GetVector(prop);
            m.SetVector(prop, new Vector4(a, b, 0, 0));
            Plugin.Log.LogInfo($"[2b] {prop} = ({a}, {b}) ({what}; was {old})");
            return 1;
        }

        static int SetFloat(Material m, string prop, float v, string what)
        {
            if (!m.HasProperty(prop)) { Plugin.Log.LogWarning($"[2b] Tree material has no '{prop}' ({what}); skipped."); return 0; }
            var old = m.GetFloat(prop);
            m.SetFloat(prop, v);
            Plugin.Log.LogInfo($"[2b] {prop} = {v} ({what}; was {old})");
            return 1;
        }

        static int SetTex(Material m, string prop, Texture t, string what)
        {
            if (!m.HasProperty(prop)) { Plugin.Log.LogWarning($"[2b] Tree material has no '{prop}' ({what}); skipped."); return 0; }
            var old = m.GetTexture(prop);
            m.SetTexture(prop, t);
            Plugin.Log.LogInfo($"[2b] {prop} = '{t.name}' {t.width}x{t.height} ({what}; was '{(old != null ? old.name : "null")}')");
            return 1;
        }

        /// <summary>MaterialOverrides (prop=value;...) last, for experiments. value: #RRGGBB[AA], a float,
        /// x,y,z,w (raw vector, no gamma conversion), or a texture (PNG beside the plugin or Texture2D in our bundle).</summary>
        static int RawOverrides(Material m)
        {
            int set = 0;
            foreach (var pair in TerrainShaderPatch.ParsePairs(Plugin.TreeMaterialOverrides.Value))
            {
                string k = pair.Key, v = pair.Value;
                if (!m.HasProperty(k)) { Plugin.Log.LogWarning($"[2b] MaterialOverrides: tree material has no '{k}'"); continue; }
                try
                {
                    if (v.StartsWith("#") && ColorUtility.TryParseHtmlString(v, out var col)) m.SetColor(k, col);
                    else if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) m.SetFloat(k, f);
                    else if (v.Contains(","))
                    {
                        var c = v.Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).Concat(new[] { 0f, 0f, 0f, 0f }).ToArray();
                        m.SetVector(k, new Vector4(c[0], c[1], c[2], c[3]));
                    }
                    else
                    {
                        var t = LoadTexture(v, linear: k == "_BumpMap");
                        if (t == null) { Plugin.Log.LogWarning($"[2b] MaterialOverrides: texture '{v}' not found; {k} unchanged"); continue; }
                        m.SetTexture(k, t);
                    }
                    set++;
                    Plugin.Log.LogInfo($"[2b] MaterialOverrides: {k} = {v}");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[2b] MaterialOverrides: {k} = {v} failed: {e.Message}"); }
            }
            return set;
        }

        // ---------- our files ----------

        static string FindFile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
            foreach (var p in new[] { Path.Combine(PluginDir, "trees", name), Path.Combine(PluginDir, name) })
                if (File.Exists(p)) return p;
            return null;
        }

        /// <summary>PNG/JPG beside the plugin (trees/ folder first), else a Texture2D of that name in our bundle.</summary>
        internal static Texture2D LoadTexture(string name, bool linear)
        {
            var path = FindFile(name);
            if (path != null)
            {
                try
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = Path.GetFileNameWithoutExtension(path) };
                    if (!ImageConversion.LoadImage(t, File.ReadAllBytes(path), false)) { Plugin.Log.LogWarning($"[2b] Could not decode {path}"); return null; }
                    t.wrapMode = TextureWrapMode.Repeat;
                    t.filterMode = FilterMode.Trilinear;
                    t.anisoLevel = 4;
                    t.Apply(true, true);   // mips, then drop the CPU copy
                    Plugin.Log.LogInfo($"[2b] Loaded {(linear ? "linear" : "sRGB")} texture {path} ({t.width}x{t.height}, {t.mipmapCount} mips)");
                    return t;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[2b] Loading {path} failed: {e.Message}"); return null; }
            }
            var b = OurAssets.Get<Texture2D>(Path.GetFileNameWithoutExtension(name.Trim()));
            if (b != null) Plugin.Log.LogInfo($"[2b] Texture '{name}' from our bundle");
            return b;
        }

        /// <summary>Our own mesh format (scripts/snow_spruce.py): "TLM1", int vertexCount, int indexCount,
        /// float3 positions (Unity space, metres, pivot at ground), float3 normals, float2 uv, int32 indices.</summary>
        internal static Mesh LoadMesh(string name)
        {
            var path = FindFile(name);
            if (path == null) { Plugin.Log.LogWarning($"[2b] Mesh file '{name}' not found beside the plugin."); return null; }
            try
            {
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    if (new string(br.ReadChars(4)) != "TLM1") { Plugin.Log.LogWarning($"[2b] {path} is not a TLM1 mesh."); return null; }
                    int nv = br.ReadInt32(), ni = br.ReadInt32();
                    if (nv <= 0 || nv > 65000 || ni <= 0 || ni % 3 != 0 || ni > 300000) { Plugin.Log.LogWarning($"[2b] {path}: bad counts v={nv} i={ni}"); return null; }
                    var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var uv = new Vector2[nv]; var idx = new int[ni];
                    for (int i = 0; i < nv; i++) pos[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    for (int i = 0; i < nv; i++) nrm[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    for (int i = 0; i < nv; i++) uv[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
                    for (int i = 0; i < ni; i++) { idx[i] = br.ReadInt32(); if (idx[i] < 0 || idx[i] >= nv) { Plugin.Log.LogWarning($"[2b] {path}: index out of range"); return null; } }
                    var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path), indexFormat = IndexFormat.UInt16 };
                    mesh.vertices = pos; mesh.normals = nrm; mesh.uv = uv;
                    mesh.SetTriangles(idx, 0, true);
                    mesh.RecalculateTangents();
                    mesh.RecalculateBounds();
                    mesh.UploadMeshData(false);
                    Plugin.Log.LogInfo($"[2b] Loaded mesh {path}: {nv} vertices, {ni / 3} triangles, bounds {mesh.bounds}");
                    return mesh;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[2b] Loading mesh {path} failed: {e.Message}"); return null; }
        }

        // ---------- diagnostics ----------

        static void LogMesh(Mesh mesh, string what)
        {
            if (mesh == null) { Plugin.Log.LogWarning($"[2b] Tree mesh ({what}) is null"); return; }
            var sb = new StringBuilder($"[2b] Tree mesh ({what}) '{mesh.name}': {mesh.vertexCount} vertices, {mesh.subMeshCount} submesh(es), " +
                                       $"submesh0 {mesh.GetIndexCount(0) / 3} triangles, bounds {mesh.bounds}, readable {mesh.isReadable}, channels [");
            foreach (VertexAttribute a in Enum.GetValues(typeof(VertexAttribute)))
                if (mesh.HasVertexAttribute(a)) sb.Append($"{a}:{mesh.GetVertexAttributeFormat(a)}x{mesh.GetVertexAttributeDimension(a)} ");
            sb.Append("]");
            Plugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>Numbers only (nothing saved): over a 256x256 downsample, the share of texels that pass the
        /// alpha cut, and for those the mean linear RGB, the mean HSV value, and the share the shader treats as leaf.</summary>
        static string AlbedoStats(Texture t, Material m)
        {
            if (t == null) return "null";
            RenderTexture rt = null;
            try
            {
                float cutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.75f;
                float off = m.HasProperty("_LeafMaskOffSet") ? m.GetFloat("_LeafMaskOffSet") : -0.1f;
                float sens = m.HasProperty("_LeakMaskSensitivity") ? m.GetFloat("_LeakMaskSensitivity") : 0.03f;
                rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                Graphics.Blit(t, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var r = new Texture2D(256, 256, TextureFormat.RGBAFloat, false, true);
                r.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                r.Apply();
                RenderTexture.active = prev;
                var px = r.GetPixels();
                UnityEngine.Object.Destroy(r);
                int pass = 0, leaf = 0; double sr = 0, sg = 0, sb = 0, sv = 0, lv = 0;
                foreach (var c in px)
                {
                    if (c.a < cutoff) continue;
                    pass++; sr += c.r; sg += c.g; sb += c.b;
                    float v = Mathf.Max(c.r, Mathf.Max(c.g, c.b)); sv += v;
                    float g = (2 * c.g - c.r - c.b) / (c.r + 2 * c.g + c.b + 0.001f);
                    float x = Mathf.Clamp01(5f * (g + off - (sens - 0.1f)));
                    if (x * x * (3 - 2 * x) > 0.5f) { leaf++; lv += v; }
                }
                if (pass == 0) return $"'{t.name}' no texel passes alpha {cutoff}";
                return $"'{t.name}' {t.width}x{t.height}: {100.0 * pass / px.Length:F1}% of texels pass alpha {cutoff}; of those mean linear RGB " +
                       $"({sr / pass:F3}, {sg / pass:F3}, {sb / pass:F3}), mean value {sv / pass:F3}, leaf share {100.0 * leaf / pass:F1}%, " +
                       $"leaf mean value {(leaf > 0 ? lv / leaf : 0):F3}";
            }
            catch (Exception e) { return $"'{t.name}' stats failed: {e.Message}"; }
            finally { if (rt != null) RenderTexture.ReleaseTemporary(rt); }
        }

        static string Fmt(Vector4 v) => string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4}, {2:F4}, {3:F4})", v.x, v.y, v.z, v.w);
    }
}
