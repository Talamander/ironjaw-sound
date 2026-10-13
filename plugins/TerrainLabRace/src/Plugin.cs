using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.Chat;
using NuclearOption.SavedMission;
using UnityEngine;

namespace TerrainLab.Race
{
    /// <summary>
    /// Air race mode for Terrain Lab maps (see docs/RACE-MODE.md).
    ///
    /// The mission "&lt;map&gt; - Air Race &lt;course name&gt;" selects courses/&lt;variant&gt;/&lt;id&gt;.json next to the DLL.
    /// On the host the plugin runs the race: it puts every player spawn onto the rolling start grid in the air
    /// (any aircraft, any airbase), detects gate passes for every racer, applies the rules and posts the start,
    /// splits and results as server chat messages, which every client shows even without the plugin.
    /// On any machine with the plugin it also draws the gates and a timer for the local aircraft.
    /// </summary>
    [BepInPlugin("talamander.terrainlab.race", "TerrainLabRace", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> CoursesDir;
        internal static ConfigEntry<bool> ShowGates;
        internal static ConfigEntry<bool> ShowHud;
        internal static ConfigEntry<float> GridSpeedFraction;
        internal static ConfigEntry<bool> AutoGridOnFirstSpawn;
        internal static ConfigEntry<bool> DedicatedAnyoneCommands;
        internal static ConfigEntry<KeyboardShortcut> KeyGrid, KeyGo, KeyReset;
        internal static Plugin Instance;

        internal static List<Course> Courses = new List<Course>();
        internal static Course Active;
        internal static RaceHost Host;
        internal static RaceView View;
        static Mission seenMission;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Run air races on missions named '<map> - Air Race <course name>'.");
            CoursesDir = Config.Bind("General", "CoursesDir", "courses", "Course folder, relative to the plugin folder or absolute.");
            ShowGates = Config.Bind("Display", "ShowGates", true, "Draw the gate rings in the world.");
            ShowHud = Config.Bind("Display", "ShowHud", true, "Show the race timer box.");
            GridSpeedFraction = Config.Bind("Host", "GridSpeedFraction", 0.8f,
                "Air-start speed is the course grid speed, capped at this fraction of the aircraft's top speed (helicopters).");
            AutoGridOnFirstSpawn = Config.Bind("Host", "AutoGridOnFirstSpawn", false,
                "Open the start grid automatically when the first player spawns (instead of '!race grid').");
            DedicatedAnyoneCommands = Config.Bind("Host", "DedicatedAnyoneCommands", true,
                "On a dedicated server (no host player) any player may use '!race grid|go|end|reset'.");
            KeyGrid = Config.Bind("Host", "KeyOpenGrid", new KeyboardShortcut(KeyCode.F7), "Host key: open the start grid (same as '!race grid').");
            KeyGo = Config.Bind("Host", "KeyGoNow", new KeyboardShortcut(KeyCode.None), "Host key: start the race now (same as '!race go').");
            KeyReset = Config.Bind("Host", "KeyReset", new KeyboardShortcut(KeyCode.F9), "Host key: reset to the lobby (same as '!race reset').");

            string dir = CoursesDir.Value;
            if (!Path.IsPathRooted(dir)) dir = Path.Combine(Path.GetDirectoryName(Info.Location) ?? ".", dir);
            Courses = Course.LoadAll(dir, Log);
            foreach (var c in Courses)
                Log.LogInfo($"[Race] course '{c.Name}' ({c.Id}, {c.Variant}, {c.MapPrefab}): {c.Gates.Count} gates, " +
                            $"{c.LengthM / 1000f:F1} km, grid {c.Slots.Count} slots at {c.GridSpeed:F0} m/s, GO after {c.GoAfter:F0} s");

            new Harmony("talamander.terrainlab.race").PatchAll(typeof(Plugin).Assembly);
            Patches.PatchChat(new Harmony("talamander.terrainlab.race.chat"));
            // Our own runner object: the BepInEx manager object (this component) is destroyed on the first scene load
            // in a plain BepInEx install, so Update and OnGUI must not live here.
            var go = new GameObject("TerrainLabRace");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<RaceRunner>();
            if (TestDriver.Enabled) go.AddComponent<TestDriver>();
            if (MissionProbe.Enabled) go.AddComponent<MissionProbe>();
            Log.LogInfo($"[Race] ready: {Courses.Count} course(s){(TestDriver.Enabled ? ", TEST MODE" : "")}{(MissionProbe.Enabled ? ", MISSION PROBE" : "")}");
        }

        internal static bool IsServer
        {
            get
            {
                var s = NetworkSceneSingleton<Spawner>.i;
                return s != null && s.IsServer;
            }
        }

        internal static float Now => MissionManager.i != null ? MissionManager.i.MissionTime : Time.timeSinceLevelLoad;

        internal static void Tick()
        {
            if (!Enabled.Value) return;
            Mission m = MissionManager.IsRunning ? MissionManager.CurrentMission : null;
            if (m != seenMission)
            {
                seenMission = m;
                Teardown();
                if (m != null) Setup(m);
            }
            if (Active == null) return;
            try
            {
                if (IsServer)
                {
                    if (Host == null) Host = new RaceHost(Active);
                    if (KeyGrid.Value.IsDown()) Host.Command("grid", "host key");
                    if (KeyGo.Value.IsDown()) Host.Command("go", "host key");
                    if (KeyReset.Value.IsDown()) Host.Command("reset", "host key");
                    Host.Tick();
                }
                View?.Tick();
            }
            catch (Exception e)
            {
                Log.LogError($"[Race] update failed: {e}");
            }
        }

        static void Setup(Mission m)
        {
            string name = m.Name ?? "";
            string map = m.MapKey.Path ?? "";
            string forced = Environment.GetEnvironmentVariable("TERRAINLAB_RACE_COURSE");
            Course c = Courses.FirstOrDefault(x => x.MapPrefab == map &&
                (!string.IsNullOrEmpty(forced) ? x.Id == forced : name.EndsWith("Air Race " + x.Name, StringComparison.Ordinal)));
            if (c == null)
            {
                if (name.Contains("Air Race"))
                    Log.LogWarning($"[Race] mission '{name}' on '{map}' looks like a race, but no course matches it");
                return;
            }
            Active = c;
            View = new RaceView(c);
            Log.LogInfo($"[Race] mission '{name}': course '{c.Name}' from {c.File}");
        }

        static void Teardown()
        {
            if (Active != null) Log.LogInfo($"[Race] mission ended: course '{Active.Name}' unloaded");
            View?.Destroy();
            View = null;
            Host = null;
            Active = null;
        }

        internal static void Gui()
        {
            if (Active != null && ShowHud.Value) View?.OnGUI();
        }

        /// <summary>Server chat message to everyone. Clients without the plugin see the text; the [race ...] tag
        /// at the end carries the numbers for clients with the plugin.</summary>
        internal static void Broadcast(string text)
        {
            Log.LogInfo("[Race] say: " + text);
            var chat = NetworkSceneSingleton<ChatManager>.i;
            if (chat == null) return;
            try { chat.RpcServerMessage(text, false); }
            catch (Exception e) { Log.LogWarning($"[Race] chat send failed: {e.Message}"); }
        }
    }

    /// <summary>Hidden DontDestroyOnLoad host for the race loop and the HUD.</summary>
    internal class RaceRunner : MonoBehaviour
    {
        void Update() => Plugin.Tick();
        void OnGUI() => Plugin.Gui();
    }
}
