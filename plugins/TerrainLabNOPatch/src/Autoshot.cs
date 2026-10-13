using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Cysharp.Threading.Tasks;
using NuclearOption.Effects;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Automatic screenshot mode, off by default. Enable with the environment variable TERRAINLAB_AUTOSHOT=1 or
    /// the config entry 6.Autoshot.Enabled. From the main menu it opens a new Mission Editor mission on our map
    /// (MissionEditor.LoadEditor, the same call the editor's "New mission" menu makes), waits for the map and the
    /// detail system to settle, moves the free camera to each view in a views file, captures one PNG per view and
    /// terrain material mode, writes shots.txt and quits the game.
    ///
    /// Views file: one view per line, '#' comments. Global (map-centred) metres, Y up:
    ///   id  x  y  z  heading_deg  pitch_deg  hfov_deg  [probe]
    /// heading is clockwise from north, pitch is negative looking down. 'probe' views also get the probe modes.
    ///
    /// Stock maps: TERRAINLAB_AUTOSHOT_MAP may name a stock prefab ("Terrain1" = Heartland, "Terrain_naval" = Ignus).
    /// Then there is no MapPatcher context: the runner does not wait for one and shoots the stock mode only.
    ///
    /// Auto views (TERRAINLAB_AUTOSHOT_AUTO=1): after the map settles the runner finds the airbase with the longest
    /// runway (not carriers) and the densest MapBuilding cluster (the town), and adds views at fixed distances:
    /// town 2 km out at 300 m above the town ground and a 500 m low pass at 80 m, airbase 5 km out at 500 m and
    /// 1 km out at 150 m, plus two frame-time views 100 m over each (ft_*). Cameras sit on the bearing
    /// TERRAINLAB_AUTOSHOT_AUTO_BEARING (degrees from the target to the camera, default 225, sun behind at 15:00).
    /// Every auto view is logged as a views-file line ("auto view: ...") so it can be shot again by hand.
    /// The views file may be missing in auto mode.
    /// </summary>
    static class Autoshot
    {
        static bool Env(string name, out string value)
        {
            value = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(value);
        }

        internal static bool Enabled
        {
            get
            {
                if (Env("TERRAINLAB_AUTOSHOT", out var v)) return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                return Plugin.AutoshotEnabled.Value;
            }
        }

        static string Setting(string env, string cfg) => Env(env, out var v) ? v : cfg;

        internal static void Install()
        {
            if (!Enabled) return;
            var go = new GameObject("TerrainLabNOPatch.Autoshot");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<AutoshotRunner>();
            Plugin.Log.LogInfo("[6] Autoshot enabled: the game will open our map in the Mission Editor, take screenshots and quit.");
        }

        internal struct View
        {
            public string Id;
            public Vector3 Pos;
            public float Heading, Pitch, HFov;
            public bool Probe;
        }

        internal static List<View> LoadViews(string path)
        {
            var list = new List<View>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 7) { Plugin.Log.LogWarning($"[6] Bad views line: '{raw}'"); continue; }
                float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                list.Add(new View
                {
                    Id = p[0], Pos = new Vector3(F(1), F(2), F(3)), Heading = F(4), Pitch = F(5), HFov = F(6),
                    Probe = p.Skip(7).Any(t => t == "probe"),
                });
            }
            return list;
        }
    }

    class AutoshotRunner : MonoBehaviour
    {
        string outDir;
        readonly StringBuilder report = new StringBuilder();
        readonly List<Canvas> hiddenCanvases = new List<Canvas>();
        readonly Dictionary<string, Material> modeMaterials = new Dictionary<string, Material>();

        IEnumerator Start()
        {
            string root = Path.GetFullPath(Path.Combine(Paths.GameRootPath, ".."));
            string viewsPath = Autoshot_Setting("TERRAINLAB_AUTOSHOT_VIEWS", Plugin.AutoshotViews.Value);
            if (!Path.IsPathRooted(viewsPath)) viewsPath = Path.Combine(root, viewsPath);
            string outRoot = Autoshot_Setting("TERRAINLAB_AUTOSHOT_OUT", Plugin.AutoshotOut.Value);
            if (!Path.IsPathRooted(outRoot)) outRoot = Path.Combine(root, outRoot);
            string stamp = Autoshot_Setting("TERRAINLAB_AUTOSHOT_STAMP", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            outDir = Path.Combine(outRoot, stamp);
            Directory.CreateDirectory(outDir);
            var modes = Split(Autoshot_Setting("TERRAINLAB_AUTOSHOT_MODES", Plugin.AutoshotModes.Value));
            var probeModes = Split(Autoshot_Setting("TERRAINLAB_AUTOSHOT_PROBE_MODES", Plugin.AutoshotProbeModes.Value));
            float settle = float.Parse(Autoshot_Setting("TERRAINLAB_AUTOSHOT_SETTLE", Plugin.AutoshotSettle.Value.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            float tod = float.Parse(Autoshot_Setting("TERRAINLAB_AUTOSHOT_TIME", Plugin.AutoshotTimeOfDay.Value.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            Note($"autoshot out {outDir}, views {viewsPath}, modes [{string.Join(",", modes)}], probe modes [{string.Join(",", probeModes)}], settle {settle} s, time of day {tod}");

            bool auto = Autoshot_Setting("TERRAINLAB_AUTOSHOT_AUTO", "0") == "1";
            List<Autoshot.View> views;
            try { views = auto && !File.Exists(viewsPath) ? new List<Autoshot.View>() : Autoshot.LoadViews(viewsPath); }
            catch (Exception e) { Note($"ERROR reading views: {e.Message}"); Finish(); yield break; }
            Note($"{views.Count} views");

            // 1. Main menu loaded.
            float t0 = Time.realtimeSinceStartup;
            while (MainMenu.State != MainMenu.LoadingState.Loaded)
            {
                if (Time.realtimeSinceStartup - t0 > 300f) { Note("ERROR main menu did not load in 300 s"); Finish(); yield break; }
                yield return new WaitForSecondsRealtime(1f);
            }
            // MainMenu.State flips to Loaded before the menu sets GameState.Menu. LoadEditor only starts an
            // offline editor host from GameState.Menu; from any other state it takes the in-editor path and
            // throws. When Steam is not reachable (the test copy under umu), MainMenu.StartAsync throws on
            // SteamFriends.GetPersonaName and never sets Menu, so set it ourselves after a grace period.
            float tm = Time.realtimeSinceStartup;
            while (GameManager.gameState != GameState.Menu && Time.realtimeSinceStartup - tm < 15f)
                yield return new WaitForSecondsRealtime(1f);
            if (GameManager.gameState != GameState.Menu)
            {
                Note($"game state still {GameManager.gameState} after the menu loaded (Steam init failed?); setting Menu and running SetupGame");
                try { GameManager.SetGameState(GameState.Menu); } catch (Exception e) { Note($"SetGameState threw: {e.Message}"); }
                try { GameManager.SetupGame(); } catch (Exception e) { Note($"SetupGame threw: {e.Message}"); }
            }
            yield return new WaitForSecondsRealtime(3f);
            Note($"main menu loaded after {Time.realtimeSinceStartup - t0:F0} s");

            // 2. New Mission Editor mission on our map (what MissionEditorNewMenuV2 does for a chosen map).
            string mapName = Autoshot_Setting("TERRAINLAB_AUTOSHOT_MAP", "");
            if (mapName.Length == 0)
            {
                var loader = Resources.FindObjectsOfTypeAll<MapLoader>().FirstOrDefault();
                var md = loader?.Maps?.FirstOrDefault(m => m != null && MapIds.IsOurPrefab(m.PrefabName));
                mapName = md?.PrefabName;
                Note($"MapLoader maps: {string.Join(", ", loader?.Maps?.Select(m => m?.PrefabName) ?? new string[0])}");
            }
            if (string.IsNullOrEmpty(mapName)) { Note("ERROR no TerrainLab_/IronjawSound_ map in MapLoader.Maps"); Finish(); yield break; }
            bool ours = MapIds.IsOurPrefab(mapName);
            if (!ours)
            {
                Note($"'{mapName}' is a stock map: no MapPatcher context, stock mode only");
                modes = new List<string> { "stock" };
                probeModes = new List<string>();
            }
            Exception err = null;
            try
            {
                var cfg = NewMissionConfig.DefaultMission(MapKey.GameWorldPrefab(mapName));
                cfg.Name = "TerrainLab autoshot";
                MissionManager.NewMission(cfg);
                MissionManager.CurrentMission.environment.timeOfDay = tod;
                // Clear sky by default; TERRAINLAB_AUTOSHOT_WEATHER (0-1) and TERRAINLAB_AUTOSHOT_CLOUD_ALT (m ASL)
                // reproduce a mission's cloud for look comparisons.
                MissionManager.CurrentMission.environment.weatherIntensity =
                    float.Parse(Autoshot_Setting("TERRAINLAB_AUTOSHOT_WEATHER", "0"), CultureInfo.InvariantCulture);
                string ca = Autoshot_Setting("TERRAINLAB_AUTOSHOT_CLOUD_ALT", "");
                if (!string.IsNullOrEmpty(ca))
                    MissionManager.CurrentMission.environment.cloudAltitude = float.Parse(ca, CultureInfo.InvariantCulture);
                Note($"weather {MissionManager.CurrentMission.environment.weatherIntensity}, cloud altitude {MissionManager.CurrentMission.environment.cloudAltitude}");
                MissionEditor.LoadEditor(MissionManager.CurrentMission).Forget();
            }
            catch (Exception e) { err = e; }
            if (err != null) { Note($"ERROR starting the editor: {err}"); Finish(); yield break; }
            Note($"editor load started on '{mapName}'");

            // 3. Map patched, editor camera up.
            t0 = Time.realtimeSinceStartup;
            while ((ours && MapPatcher.CurrentCtx == null) || GameManager.gameState != GameState.Editor || SceneSingleton<CameraStateManager>.i == null)
            {
                if (Time.realtimeSinceStartup - t0 > 300f) { Note($"ERROR map/editor not ready in 300 s (state {GameManager.gameState}, ctx {(MapPatcher.CurrentCtx != null)})"); Finish(); yield break; }
                yield return new WaitForSecondsRealtime(1f);
            }
            Note($"editor ready after {Time.realtimeSinceStartup - t0:F0} s");
            yield return new WaitForSecondsRealtime(10f);

            var ctx = MapPatcher.CurrentCtx;
            var cam = SceneSingleton<CameraStateManager>.i;
            cam.allowInputs = false;
            LogEnvironment(ctx, cam);
            if (ctx != null) BuildModes(ctx);
            if (auto)
            {
                List<Autoshot.View> av = null;
                try { av = AutoViews(); }
                catch (Exception e) { Note($"ERROR auto views: {e}"); }
                if (av != null) views.AddRange(av);
                Note($"{views.Count} views after auto");
            }

            // 4. Views.
            foreach (var v in views)
            {
                Exception moveErr = null;
                try
                {
                    var rot = Quaternion.Euler(-v.Pitch, v.Heading, 0f);
                    cam.SetCameraPosition(new GlobalPosition(v.Pos.x, v.Pos.y, v.Pos.z), rot);
                    float aspect = cam.mainCamera.aspect;
                    float vfov = Camera.HorizontalToVerticalFieldOfView(v.HFov, aspect);
                    cam.SetDesiredFoV(vfov, vfov);
                    cam.cameraVelocity = Vector3.zero;
                }
                catch (Exception e) { moveErr = e; }
                if (moveErr != null) { Note($"ERROR moving camera for {v.Id}: {moveErr.Message}"); continue; }
                yield return new WaitForSecondsRealtime(settle);
                if (frameTiming) yield return MeasureFrames(v.Id);
                var list = v.Probe ? modes.Concat(probeModes).ToList() : modes;
                foreach (var mode in list)
                {
                    if (!ApplyMode(ctx, mode)) { Note($"{v.Id}: mode {mode} unavailable, skipped"); continue; }
                    yield return null; yield return null;
                    yield return new WaitForSecondsRealtime(0.5f);
                    HideCanvases();
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    Capture(v, mode, cam);
                }
                ApplyMode(ctx, modes.FirstOrDefault() ?? "stock");
            }

            Finish();
        }

        static string Autoshot_Setting(string env, string cfg)
        {
            var v = Environment.GetEnvironmentVariable(env);
            return string.IsNullOrEmpty(v) ? cfg : v;
        }

        static List<string> Split(string s) =>
            (s ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();

        void Note(string s)
        {
            Plugin.Log.LogInfo("[6] " + s);
            report.AppendLine($"{DateTime.Now:HH:mm:ss} {s}");
            try { File.WriteAllText(Path.Combine(outDir, "shots.txt"), report.ToString()); } catch { }
        }

        // Frame timing per view (env TERRAINLAB_AUTOSHOT_FRAMETIME=1): vsync off and no frame cap while measuring
        // (runtime only, nothing saved), then frame time over 240 frames, logged as mean / p50 / p95 / max ms.
        bool frameTiming = Autoshot_Setting("TERRAINLAB_AUTOSHOT_FRAMETIME", "0") == "1";

        IEnumerator MeasureFrames(string id)
        {
            int vs = QualitySettings.vSyncCount, fr = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            for (int i = 0; i < 30; i++) yield return null;
            var dt = new List<float>(240);
            for (int i = 0; i < 240; i++) { yield return null; dt.Add(Time.unscaledDeltaTime * 1000f); }
            QualitySettings.vSyncCount = vs;
            Application.targetFrameRate = fr;
            dt.Sort();
            float mean = dt.Average();
            Note($"frametime {id}: mean {mean:F2} ms ({1000f / mean:F0} fps) p50 {dt[dt.Count / 2]:F2} p95 {dt[(int)(dt.Count * 0.95f)]:F2} max {dt[dt.Count - 1]:F2} ms over {dt.Count} frames (vSync was {vs}, targetFrameRate {fr})");
        }

        void Finish()
        {
            Note("done, quitting");
            Application.Quit();
        }

        void HideCanvases()
        {
            foreach (var c in FindObjectsOfType<Canvas>())
                if (c.enabled && c.isRootCanvas) { c.enabled = false; hiddenCanvases.Add(c); }
        }

        void Capture(Autoshot.View v, string mode, CameraStateManager cam)
        {
            try
            {
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                string name = $"{v.Id}_{mode}.png";
                File.WriteAllBytes(Path.Combine(outDir, name), tex.EncodeToPNG());
                cam.GetCameraPosition(out GlobalPosition gp, out Quaternion rot);
                string ground = "no hit";
                var p = cam.transform.position;
                if (Physics.Raycast(p + Vector3.up * 5000f, Vector3.down, out var hit, 20000f))
                    ground = $"{hit.point.ToGlobalPosition().y:F1} m on '{hit.collider.name}'";
                Note($"{name} {tex.width}x{tex.height} cam {gp} euler {rot.eulerAngles} vfov {cam.mainCamera.fieldOfView:F1} far {cam.mainCamera.farClipPlane:F0} ground {ground} trees {TreeCount()}");
                Destroy(tex);
            }
            catch (Exception e) { Note($"ERROR capture {v.Id}/{mode}: {e.Message}"); }
        }

        static string TreeCount()
        {
            var dr = DetailRenderer.i;
            return dr == null ? "no DetailRenderer" : "renderer present";
        }

        // ---------------------------------------------------------------- modes

        void BuildModes(MapContext ctx)
        {
            var patched = TerrainShaderPatch.OurMaterial;
            var urp = ctx.OriginalTileMaterial;
            Texture macro = urp != null && urp.HasProperty("_BaseMap") ? urp.GetTexture("_BaseMap") : OurAssets.Get<Texture2D>("macro_basecolor");
            if (patched != null) modeMaterials["stock"] = patched;
            if (urp != null) modeMaterials["urp"] = urp;

            // Unlit macro basecolor.
            foreach (var sn in new[] { "Universal Render Pipeline/Unlit", "Unlit/Texture", "Sprites/Default", "UI/Default" })
            {
                var sh = Shader.Find(sn);
                if (sh == null) continue;
                var m = new Material(sh) { name = "TerrainLab_DebugUnlit" };
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", macro);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", macro);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
                if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
                modeMaterials["unlit"] = m;
                Note($"unlit mode uses shader '{sn}'");
                break;
            }

            if (patched == null) return;
            var grey = SolidTex(new Color(0.5f, 0.5f, 0.5f, 0.5f), false, "grey50");
            var flatN = Texture2D.normalTexture;

            // Stock shader, every stock detail texture neutral (albedo 50 % grey, normals flat): shows how the
            // shader combines macro and detail.
            var greyMat = new Material(patched) { name = "TerrainLab_GreyDetail" };
            int nd = 0;
            foreach (var prop in greyMat.GetTexturePropertyNames())
            {
                if (!prop.StartsWith("_SampleTexture2D_")) continue;
                var t = greyMat.GetTexture(prop);
                bool isNormal = t != null && t.name.EndsWith("_n");
                greyMat.SetTexture(prop, isNormal ? (Texture)flatN : grey);
                nd++;
            }
            modeMaterials["greydetail"] = greyMat;
            Note($"greydetail: {nd} detail textures neutralised");

            // Stock shader with every splat zero: the base layer.
            var black = new Material(patched) { name = "TerrainLab_SplatBlack" };
            foreach (var p in SplatProps) black.SetTexture(p, Texture2D.blackTexture);
            modeMaterials["splatblack"] = black;

            // The first binding (before stock-layout splats): packed splat_a..d straight into the four slots.
            var packed = new Material(patched) { name = "TerrainLab_PackedSplats" };
            string[] packedNames = { "splat_a", "splat_b", "splat_c", "splat_d" };
            for (int i = 0; i < 4; i++)
            {
                var t = OurAssets.Get<Texture2D>(packedNames[i]);
                packed.SetTexture(SplatProps[i], t != null ? (Texture)t : Texture2D.blackTexture);
            }
            modeMaterials["packedsplats"] = packed;

            // Stock shader with a flat grey macro: the detail layer alone.
            var nomacro = new Material(patched) { name = "TerrainLab_NoMacro" };
            nomacro.SetTexture("_macro_basecolor", grey);
            modeMaterials["nomacro"] = nomacro;

            // Probe: one splat white, the others black, to see which detail set each splat drives.
            foreach (var solo in SplatProps)
            {
                var m = new Material(patched) { name = "TerrainLab_Solo" + solo };
                foreach (var p in SplatProps) m.SetTexture(p, p == solo ? Texture2D.whiteTexture : Texture2D.blackTexture);
                modeMaterials["solo" + solo.Replace("_splat", "")] = m;
            }
            // Probe: each RGBA channel of the first splat alone (does the shader read more than .r?).
            string[] ch = { "r", "g", "b", "a" };
            for (int i = 0; i < 4; i++)
            {
                var c = new Color(i == 0 ? 1 : 0, i == 1 ? 1 : 0, i == 2 ? 1 : 0, i == 3 ? 1 : 0);
                var m = new Material(patched) { name = "TerrainLab_Channel" + ch[i] };
                foreach (var p in SplatProps) m.SetTexture(p, SolidTex(c, true, "chan_" + ch[i]));
                modeMaterials["allsplats_" + ch[i]] = m;
            }
        }

        static readonly string[] SplatProps = { "_splat_rock", "_splat_grass", "_splat_lush", "_splat_fields" };

        static Texture2D SolidTex(Color c, bool linear, string name)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false, linear) { name = name };
            var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = c;
            t.SetPixels(px);
            t.Apply(false, true);
            return t;
        }

        // ---------------------------------------------------------------- water probe modes
        // TERRAINLAB_AUTOSHOT_WATER_MODES = "name:Prop=value;Prop=value|name2:..." defines extra modes that change
        // the shared water material (LevelInfo.waterMaterial) for one capture, with the stock terrain material.
        // Values: a float, a #RRGGBB or #RRGGBBAA colour, or for texture properties "debugmacro" (a coarse colour
        // checker, to see whether and how strongly the shader shows _macro_basecolor), "flatdepth:<0-1>", or
        // "png:<file>" / "pnglin:<file>" (a PNG, sRGB or linear, path relative to the game folder or absolute).
        // Every other mode restores the water material as it was when the first water mode ran.
        Dictionary<string, List<KeyValuePair<string, string>>> waterModes;
        readonly Dictionary<string, object> waterBaseline = new Dictionary<string, object>();

        Dictionary<string, List<KeyValuePair<string, string>>> WaterModes()
        {
            if (waterModes != null) return waterModes;
            waterModes = new Dictionary<string, List<KeyValuePair<string, string>>>();
            string spec = Autoshot_Setting("TERRAINLAB_AUTOSHOT_WATER_MODES", "");
            foreach (var part in spec.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = part.IndexOf(':');
                if (c <= 0) continue;
                var kv = new List<KeyValuePair<string, string>>();
                foreach (var a in part.Substring(c + 1).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int e = a.IndexOf('=');
                    if (e > 0) kv.Add(new KeyValuePair<string, string>(a.Substring(0, e).Trim(), a.Substring(e + 1).Trim()));
                }
                waterModes[part.Substring(0, c).Trim()] = kv;
            }
            return waterModes;
        }

        static Material WaterMat()
        {
            var li = NetworkSceneSingleton<LevelInfo>.i;
            return li == null ? null : HarmonyLib.AccessTools.Field(typeof(LevelInfo), "waterMaterial")?.GetValue(li) as Material;
        }

        void RestoreWater()
        {
            var m = WaterMat();
            if (m == null) return;
            foreach (var kv in waterBaseline)
            {
                if (kv.Value is Color col) m.SetColor(kv.Key, col);
                else if (kv.Value is float f) m.SetFloat(kv.Key, f);
                else if (kv.Value is Texture t) m.SetTexture(kv.Key, t);
            }
        }

        bool ApplyWaterMode(string mode)
        {
            if (!WaterModes().TryGetValue(mode, out var kvs)) return false;
            var m = WaterMat();
            if (m == null) { Note("water mode: no water material"); return false; }
            RestoreWater();
            var sb = new StringBuilder($"water mode {mode}:");
            foreach (var kv in kvs)
            {
                string p = kv.Key, v = kv.Value;
                if (!m.HasProperty(p)) { sb.Append($" {p}=?(missing)"); continue; }
                try
                {
                    if (v.StartsWith("#"))
                    {
                        if (!waterBaseline.ContainsKey(p)) waterBaseline[p] = m.GetColor(p);
                        ColorUtility.TryParseHtmlString(v, out Color col);
                        if (v.Length == 7) col.a = ((Color)waterBaseline[p]).a;
                        m.SetColor(p, col);
                    }
                    else if (v.StartsWith("png:") || v.StartsWith("pnglin:"))
                    {
                        if (!waterBaseline.ContainsKey(p)) waterBaseline[p] = m.GetTexture(p);
                        bool lin = v.StartsWith("pnglin:");
                        string f = v.Substring(lin ? 7 : 4);
                        if (!Path.IsPathRooted(f)) f = Path.Combine(Path.GetDirectoryName(Application.dataPath), f);
                        var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, lin) { name = "tl_probe_" + Path.GetFileName(f) };
                        if (!ImageConversion.LoadImage(t, File.ReadAllBytes(f), false)) throw new Exception("decode " + f);
                        t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
                        m.SetTexture(p, t);
                    }
                    else if (v == "debugmacro" || v.StartsWith("flatdepth"))
                    {
                        if (!waterBaseline.ContainsKey(p)) waterBaseline[p] = m.GetTexture(p);
                        m.SetTexture(p, v == "debugmacro" ? DebugChecker() : SolidTex(new Color(1, 1, 1, 1) * float.Parse(v.Substring(10), CultureInfo.InvariantCulture), false, "flatdepth"));
                    }
                    else
                    {
                        if (!waterBaseline.ContainsKey(p)) waterBaseline[p] = m.GetFloat(p);
                        m.SetFloat(p, float.Parse(v, CultureInfo.InvariantCulture));
                    }
                    sb.Append($" {p}={v}");
                }
                catch (Exception e) { sb.Append($" {p}=!{e.Message}"); }
            }
            Note(sb.ToString());
            return true;
        }

        Texture2D debugChecker;
        Texture2D DebugChecker()
        {
            if (debugChecker != null) return debugChecker;
            var cols = new[] { new Color(0.9f, 0.1f, 0.1f), new Color(0.1f, 0.8f, 0.2f), new Color(0.1f, 0.2f, 0.9f), new Color(0.95f, 0.95f, 0.95f) };
            debugChecker = new Texture2D(64, 64, TextureFormat.RGBA32, false) { name = "tl_debug_checker", filterMode = FilterMode.Point };
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    debugChecker.SetPixel(x, y, cols[((x / 8) + (y / 8)) % 4]);
            debugChecker.Apply();
            return debugChecker;
        }

        bool ApplyMode(MapContext ctx, string mode)
        {
            if (WaterModes().ContainsKey(mode))
            {
                if (!ApplyWaterMode(mode)) return false;
                mode = "stock";
            }
            else RestoreWater();
            if (ctx == null) return mode == "stock";  // stock map: nothing to swap
            if (!modeMaterials.TryGetValue(mode, out var mat) || mat == null) return false;
            foreach (var mr in ctx.Tiles)
            {
                if (mr == null) continue;
                var mats = mr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                mr.sharedMaterials = mats;
            }
            return true;
        }

        // ---------------------------------------------------------------- auto views

        static float GroundY(Vector3 local, float fromY)
        {
            var o = new Vector3(local.x, fromY + 4000f, local.z);
            return Physics.Raycast(o, Vector3.down, out var hit, 12000f) ? hit.point.y : fromY;
        }

        Autoshot.View MakeView(string id, Vector3 target, float bearing, float dist, float agl, float hfov)
        {
            float b = bearing * Mathf.Deg2Rad;
            var c = new Vector3(target.x + dist * Mathf.Sin(b), 0f, target.z + dist * Mathf.Cos(b));
            float gCam = GroundY(c, target.y);
            c.y = Mathf.Max(target.y + agl, gCam + 30f);
            float aim = target.y + 5f;
            float pitch = Mathf.Atan2(aim - c.y, dist) * Mathf.Rad2Deg;
            var gp = c.ToGlobalPosition();
            var v = new Autoshot.View { Id = id, Pos = new Vector3(gp.x, gp.y, gp.z), Heading = (bearing + 180f) % 360f, Pitch = pitch, HFov = hfov };
            Note(string.Format(CultureInfo.InvariantCulture, "auto view: {0} {1:F1} {2:F1} {3:F1} {4:F3} {5:F3} {6:F1}", v.Id, v.Pos.x, v.Pos.y, v.Pos.z, v.Heading, v.Pitch, v.HFov));
            return v;
        }

        Autoshot.View OverView(string id, Vector3 target, float bearing, float agl)
        {
            var c = new Vector3(target.x, 0f, target.z);
            c.y = GroundY(c, target.y) + agl;
            var gp = c.ToGlobalPosition();
            var v = new Autoshot.View { Id = id, Pos = new Vector3(gp.x, gp.y, gp.z), Heading = (bearing + 180f) % 360f, Pitch = -10f, HFov = 65f };
            Note(string.Format(CultureInfo.InvariantCulture, "auto view: {0} {1:F1} {2:F1} {3:F1} {4:F3} {5:F3} {6:F1}", v.Id, v.Pos.x, v.Pos.y, v.Pos.z, v.Heading, v.Pitch, v.HFov));
            return v;
        }

        List<Autoshot.View> AutoViews()
        {
            float bearing = float.Parse(Autoshot_Setting("TERRAINLAB_AUTOSHOT_AUTO_BEARING", "225"), CultureInfo.InvariantCulture);
            var list = new List<Autoshot.View>();

            // Town: densest MapBuilding cluster (count within 800 m, on a 200 m grid), centroid of that cluster.
            var pts = FindObjectsOfType<MapBuilding>().Select(m => m.transform.position).ToList();
            Note($"auto: {pts.Count} MapBuildings");
            if (pts.Count > 0)
            {
                const float cell = 200f, rad = 800f;
                var bins = new Dictionary<(int, int), int>();
                foreach (var p in pts)
                {
                    var k = (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.z / cell));
                    bins[k] = bins.TryGetValue(k, out var n) ? n + 1 : 1;
                }
                int r = Mathf.CeilToInt(rad / cell), best = -1; (int, int) bk = (0, 0);
                foreach (var k in bins.Keys)
                {
                    int sum = 0;
                    for (int dx = -r; dx <= r; dx++)
                        for (int dz = -r; dz <= r; dz++)
                            if (dx * dx + dz * dz <= r * r && bins.TryGetValue((k.Item1 + dx, k.Item2 + dz), out var n)) sum += n;
                    if (sum > best) { best = sum; bk = k; }
                }
                var c0 = new Vector3((bk.Item1 + 0.5f) * cell, 0f, (bk.Item2 + 0.5f) * cell);
                var near = pts.Where(p => new Vector2(p.x - c0.x, p.z - c0.z).magnitude <= rad).ToList();
                var town = new Vector3(near.Average(p => p.x), near.Average(p => p.y), near.Average(p => p.z));
                town.y = GroundY(town, town.y);
                Note($"auto town: {near.Count} buildings within {rad} m of global {town.ToGlobalPosition()}");
                list.Add(MakeView("auto_town_2km", town, bearing, 2000f, 300f, 60f));
                list.Add(MakeView("auto_town_500m", town, bearing, 500f, 80f, 70f));
                list.Add(OverView("ft_town_low", town, bearing, 100f));
            }

            // Airbase: longest runway, carriers skipped.
            Airbase bestAb = null; Airbase.Runway bestRw = null; float bestLen = -1f;
            foreach (var ab in FindObjectsOfType<Airbase>())
            {
                if (ab == null || ab.AttachedAirbase || ab.runways == null) continue;
                foreach (var rw in ab.runways)
                {
                    if (rw == null || rw.Start == null || rw.End == null) continue;
                    float len = Vector3.Distance(rw.Start.position, rw.End.position);
                    if (len > bestLen) { bestLen = len; bestAb = ab; bestRw = rw; }
                }
            }
            if (bestAb != null)
            {
                var mid = (bestRw.Start.position + bestRw.End.position) * 0.5f;
                var tgt = bestAb.center != null ? bestAb.center.position : mid;
                if (new Vector2(tgt.x - mid.x, tgt.z - mid.z).magnitude > 1500f) tgt = mid;
                tgt.y = GroundY(tgt, tgt.y);
                var dir = bestRw.End.position - bestRw.Start.position;
                float rh = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                // camera bearing: 60 deg off the runway axis (any of the four), the one closest to the sun-behind bearing
                float camB = new[] { rh + 60f, rh - 60f, rh + 120f, rh - 120f }
                    .Select(x => (x % 360f + 360f) % 360f)
                    .OrderBy(x => Mathf.Abs(Mathf.DeltaAngle(x, bearing))).First();
                Note($"auto airbase: '{bestAb.name}' ({bestAb.SavedAirbase?.DisplayName}) runway {bestLen:F0} m heading {(rh + 360f) % 360f:F0}, target global {tgt.ToGlobalPosition()}, camera bearing {camB:F0}");
                list.Add(MakeView("auto_airbase_5km", tgt, camB, 5000f, 500f, 50f));
                list.Add(MakeView("auto_airbase_1km", tgt, camB, 1000f, 150f, 65f));
                list.Add(OverView("ft_airbase_low", tgt, camB, 100f));
            }
            else Note("auto: no airbase with runways found");
            return list;
        }

        // ---------------------------------------------------------------- environment facts

        void LogEnvironment(MapContext ctx, CameraStateManager cam)
        {
            var sb = new StringBuilder("environment:");
            sb.Append($"\n  fog {RenderSettings.fog} mode {RenderSettings.fogMode} density {RenderSettings.fogDensity} color {RenderSettings.fogColor} start {RenderSettings.fogStartDistance} end {RenderSettings.fogEndDistance}");
            sb.Append($"\n  ambient {RenderSettings.ambientMode} sky {RenderSettings.ambientSkyColor} intensity {RenderSettings.ambientIntensity}");
            if (cam.sunLight != null) sb.Append($"\n  sun intensity {cam.sunLight.intensity} color {cam.sunLight.color} euler {cam.sunLight.transform.eulerAngles}");
            sb.Append($"\n  camera far {cam.mainCamera.farClipPlane} near {cam.mainCamera.nearClipPlane} hdr {cam.mainCamera.allowHDR} res {Screen.width}x{Screen.height}");
            var lvl = NetworkSceneSingleton<LevelInfo>.i;
            if (lvl != null) sb.Append($"\n  LevelInfo.timeOfDay {lvl.timeOfDay}");
            var thm = SceneSingleton<TerrainHeightMap>.i;
            if (thm != null) sb.Append($"\n  TerrainHeightMap.height {thm.height.Min}..{thm.height.Max} on '{thm.name}'");
            sb.Append($"\n  _Datum_WorldExtent {Shader.GetGlobalVector("_Datum_WorldExtent")}");
            Note(sb.ToString());

            // Channel statistics of the stock and our splats (what the stock shader expects vs what we bind).
            var stock = Stock.TerrainMaterial();
            if (stock != null)
                foreach (var p in SplatProps)
                    Note($"splat stats stock {p}: {Stats(stock.GetTexture(p))}");
            var ours = TerrainShaderPatch.OurMaterial;
            if (ours != null)
            {
                foreach (var p in SplatProps)
                    Note($"splat stats ours  {p}: {Stats(ours.GetTexture(p))}");
                Note($"macro stats ours: {Stats(ours.GetTexture("_macro_basecolor"))}");
                if (stock != null) Note($"macro stats stock: {Stats(stock.GetTexture("_macro_basecolor"))}");
                foreach (var prop in stock != null ? stock.GetTexturePropertyNames() : new string[0])
                    if (prop.StartsWith("_SampleTexture2D_"))
                    {
                        var t = stock.GetTexture(prop);
                        if (t != null && !t.name.EndsWith("_n"))
                            Note($"detail stats {t.name}: {Stats(t)}");
                    }
            }
        }

        /// <summary>Mean, and share above 0.5, of each channel over a 256x256 downsample (linear values).</summary>
        static string Stats(Texture t)
        {
            if (t == null) return "null";
            RenderTexture rt = null;
            try
            {
                rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(t, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var r = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
                r.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                r.Apply();
                RenderTexture.active = prev;
                var px = r.GetPixels32();
                UnityEngine.Object.Destroy(r);
                double[] sum = new double[4]; int[] hi = new int[4];
                foreach (var c in px)
                {
                    byte[] v = { c.r, c.g, c.b, c.a };
                    for (int i = 0; i < 4; i++) { sum[i] += v[i]; if (v[i] > 127) hi[i]++; }
                }
                string[] n = { "R", "G", "B", "A" };
                return $"'{t.name}' " + string.Join(" ", Enumerable.Range(0, 4).Select(i => $"{n[i]} mean {sum[i] / px.Length / 255.0:F3} >.5 {100.0 * hi[i] / px.Length:F1}%"));
            }
            catch (Exception e) { return $"'{t.name}' stats failed: {e.Message}"; }
            finally { if (rt != null) RenderTexture.ReleaseTemporary(rt); }
        }
    }
}
