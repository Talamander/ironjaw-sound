using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using NOPlayer = NuclearOption.Networking.Player;

namespace TerrainLab.Race
{
    /// <summary>
    /// Headless mission smoke test (environment TERRAINLAB_MISSION_PROBE=1, never on by config; any mission, not
    /// only races). It logs what a mission actually produced in the game: every airbase with its owner, hangar count
    /// and the aircraft types its hangars can spawn (this is how carrier decks are checked), unit counts per faction,
    /// then every 30 s the aircraft in the air per faction and per nearest airbase, and every change of airbase owner.
    /// Optionally it joins a faction and spawns the local player at one airbase.
    ///
    /// Environment:
    ///   TERRAINLAB_MISSION_PROBE=1                 turn the probe on
    ///   TERRAINLAB_MISSION_PROBE_MINUTES=m         how long to watch after the mission starts (default 8)
    ///   TERRAINLAB_MISSION_PROBE_SPAWN=base:key    spawn the local player at the airbase whose display name or
    ///                                              unique name contains 'base' (or the first attached carrier
    ///                                              airbase for 'carrier'), aircraft jsonKey 'key'
    ///   TERRAINLAB_MISSION_PROBE_DIR=dir           report and screenshot folder (default mission-probe in the game folder)
    ///   TERRAINLAB_MISSION_PROBE_ROLE=client       this instance joined a host (-autoConnect): the spawn goes through
    ///                                              the real player path (buy the airframe, then ask the server for a
    ///                                              hangar spawn), as the spawn menu does
    ///   TERRAINLAB_MISSION_PROBE_SPAWN_MODE=hangar same player path on the host (default: direct server-side spawn)
    ///   TERRAINLAB_MISSION_PROBE_WAIT_PLAYERS=n    before spawning, wait up to 400 s until n players are in the
    ///                                              session, then log every player's name and faction
    /// Output: BepInEx log lines tagged [Probe], plus probe.txt and screenshots in the report folder.
    /// </summary>
    internal class MissionProbe : MonoBehaviour
    {
        public static bool Enabled => Environment.GetEnvironmentVariable("TERRAINLAB_MISSION_PROBE") == "1";

        string outDir;
        readonly StringBuilder report = new StringBuilder();
        readonly Dictionary<string, string> owners = new Dictionary<string, string>();
        readonly HashSet<int> seenAircraft = new HashSet<int>();
        readonly HashSet<int> airborne = new HashSet<int>();
        readonly HashSet<string> seenPlayers = new HashSet<string>();
        bool? spawnResult;
        static bool IsClientRole => Env("TERRAINLAB_MISSION_PROBE_ROLE", "host") == "client";

        static string Env(string k, string d)
        {
            var v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? d : v;
        }

        static MissionProbe current;

        /// <summary>Log line from a Harmony patch (TaxiDiag); dropped when no probe is running.</summary>
        internal static void NoteStatic(string s)
        {
            if (current != null) current.Note(s);
        }

        void Start()
        {
            current = this;
            outDir = Path.GetFullPath(Path.Combine(Env("TERRAINLAB_MISSION_PROBE_DIR", "mission-probe"), DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(outDir);
            Note($"probe on, report in {outDir}");
            StartCoroutine(Run());
        }

        void Note(string s)
        {
            Plugin.Log.LogInfo("[Probe] " + s);
            report.AppendLine($"{DateTime.Now:HH:mm:ss} {s}");
            try { File.WriteAllText(Path.Combine(outDir, "probe.txt"), report.ToString()); } catch { }
        }

        static string Fac(FactionHQ hq) => hq != null && hq.faction != null ? hq.faction.factionName : "None";

        static string Name(Airbase a)
        {
            var s = a.SavedAirbase;
            string dn = s != null ? s.DisplayName : null;
            string un = s != null ? s.UniqueName : null;
            return string.IsNullOrEmpty(dn) ? (un ?? a.name) : dn;
        }

        static string P(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:F0}, {1:F0}, {2:F0})", v.x, v.y, v.z);

        IEnumerator Run()
        {
            float t0 = Time.realtimeSinceStartup;
            NOPlayer player = null;
            while (Time.realtimeSinceStartup - t0 < 300f)
            {
                if (MissionManager.IsRunning && GameManager.GetLocalPlayer<NOPlayer>(out player) && player != null) break;
                yield return new WaitForSecondsRealtime(1f);
            }
            if (!MissionManager.IsRunning)
            {
                Note($"FAIL: no mission running after {Time.realtimeSinceStartup - t0:F0} s");
                yield break;
            }
            var m = MissionManager.CurrentMission;
            Note($"mission '{m?.Name}' on '{m?.MapKey.Path}' running after {Time.realtimeSinceStartup - t0:F0} s, local player {(player != null ? "yes" : "no")}, " +
                 $"role {(IsClientRole ? "client" : "host")}, server {Plugin.IsServer}, player '{player?.GetPlayerName()?.SanitizedName}'");
            yield return new WaitForSecondsRealtime(10f);
            int wantPlayers = 1;
            int.TryParse(Env("TERRAINLAB_MISSION_PROBE_WAIT_PLAYERS", "1"), out wantPlayers);
            if (wantPlayers > 1)
            {
                float tp = Time.realtimeSinceStartup;
                Note($"waiting up to 400 s for {wantPlayers} players");
                while (FindObjectsOfType<NOPlayer>().Length < wantPlayers && Time.realtimeSinceStartup - tp < 400f)
                    yield return new WaitForSecondsRealtime(1f);
                int have = FindObjectsOfType<NOPlayer>().Length;
                Note($"{(have >= wantPlayers ? "PLAYERS OK" : "PLAYERS FAIL")}: {have} of {wantPlayers} after {Time.realtimeSinceStartup - tp:F0} s");
            }
            LogPlayers();
            LogAirbases();
            LogUnits();
            try { SceneSingleton<DynamicMap>.i.Minimize(); } catch { }
            yield return Shot("start");

            string spawn = Env("TERRAINLAB_MISSION_PROBE_SPAWN", "");
            if (spawn.Contains(":") && player != null)
                yield return SpawnAt(player, spawn.Split(':')[0], spawn.Split(':')[1]);

            float minutes = 8f;
            float.TryParse(Env("TERRAINLAB_MISSION_PROBE_MINUTES", "8"), NumberStyles.Float, CultureInfo.InvariantCulture, out minutes);
            float tEnd = Time.realtimeSinceStartup + minutes * 60f;
            float nextTick = 0f;
            while (Time.realtimeSinceStartup < tEnd && MissionManager.IsRunning)
            {
                try { WatchAirbases(); WatchAircraft(); WatchPlayers(); } catch (Exception e) { Note($"watch failed: {e.Message}"); }
                if (Time.realtimeSinceStartup >= nextTick)
                {
                    nextTick = Time.realtimeSinceStartup + 30f;
                    try { Tick(); } catch (Exception e) { Note($"tick failed: {e.Message}"); }
                }
                yield return new WaitForSecondsRealtime(2f);
            }
            Note($"probe done after {minutes:F0} min: {seenAircraft.Count} aircraft seen, {airborne.Count} got airborne");
        }

        void LogAirbases()
        {
            foreach (var kv in FactionRegistry.airbaseLookup.OrderBy(k => k.Key))
            {
                var a = kv.Value;
                if (a == null) continue;
                string types;
                try { types = string.Join(" ", a.GetAvailableAircraft().Where(d => d != null).Select(d => d.jsonKey).OrderBy(k => k)); }
                catch (Exception e) { types = "error " + e.Message; }
                owners[kv.Key] = Fac(a.CurrentHQ);
                Note($"AIRBASE '{Name(a)}' [{kv.Key}] owner {Fac(a.CurrentHQ)} at {P(a.transform.GlobalPosition().AsVector3())} " +
                     $"hangars {a.hangars.Count} disabled {a.disabled} spawns: {(types.Length > 0 ? types : "nothing")}");
            }
        }

        void LogUnits()
        {
            var counts = UnitRegistry.allUnits.Where(u => u != null && !u.disabled)
                .GroupBy(u => $"{Fac(u.NetworkHQ)} {u.GetType().Name}")
                .OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
            Note("UNITS " + string.Join(", ", counts));
        }

        static string Who(NOPlayer p) =>
            $"'{p.GetPlayerName()?.SanitizedName}' faction {(p.HQ != null ? Fac(p.HQ) : "none")}";

        static string Pilot(Aircraft ac) => ac.Player != null ? "player '" + ac.Player.GetPlayerName()?.SanitizedName + "'" : "AI";

        void LogPlayers()
        {
            var ps = FindObjectsOfType<NOPlayer>();
            Note($"PLAYERS {ps.Length}: {string.Join(", ", ps.Select(Who))}");
            foreach (var p in ps) seenPlayers.Add(Who(p));
        }

        void WatchPlayers()
        {
            foreach (var p in FindObjectsOfType<NOPlayer>())
            {
                string w = Who(p);
                if (seenPlayers.Add(w)) Note($"PLAYER {w}");
            }
        }

        Airbase Nearest(Vector3 p, out float dist)
        {
            Airbase best = null;
            dist = float.MaxValue;
            foreach (var a in FactionRegistry.airbaseLookup.Values)
            {
                if (a == null) continue;
                float d = Vector3.Distance(a.transform.GlobalPosition().AsVector3(), p);
                if (d < dist) { dist = d; best = a; }
            }
            return best;
        }

        void WatchAirbases()
        {
            foreach (var kv in FactionRegistry.airbaseLookup)
            {
                if (kv.Value == null) continue;
                string f = Fac(kv.Value.CurrentHQ);
                if (owners.TryGetValue(kv.Key, out var old) && old != f)
                    Note($"CAPTURE '{Name(kv.Value)}': {old} -> {f} at mission time {Plugin.Now:F0} s");
                owners[kv.Key] = f;
            }
        }

        // Per-aircraft track, to explain aircraft that vanish before take-off: where they were last seen, how far and
        // how fast they moved, and which colliders were next to them at the end.
        class Track
        {
            public Aircraft ac; public string fac, key, pilot; public Vector3 first, last, lastLocal;
            public float t0, maxSpeed, lastSpeed, lastAlt, path; public bool wasAirborne;
        }
        readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();

        void WatchGone()
        {
            var gone = new List<int>();
            foreach (var kv in tracks)
            {
                var t = kv.Value;
                bool alive = t.ac != null && !t.ac.disabled;
                if (alive) continue;
                gone.Add(kv.Key);
                string near = "";
                try
                {
                    var names = Physics.OverlapSphere(t.lastLocal, 20f)
                        .Select(c => c != null ? c.transform.root.name + "/" + c.name : null)
                        .Where(n => n != null && !n.Contains(t.key ?? "\u0000")).Distinct().Take(6);
                    near = string.Join("; ", names);
                }
                catch { }
                Note($"GONE {t.fac} {t.key} {t.pilot} ({(t.ac == null ? "destroyed object" : "disabled")}) after {Plugin.Now - t.t0:F0} s, " +
                     $"{(t.wasAirborne ? "had flown" : "never airborne")}: last at {P(t.last)}, {Vector3.Distance(t.first, t.last):F0} m from spawn " +
                     $"(path {t.path:F0} m), last speed {t.lastSpeed:F1} m/s, max {t.maxSpeed:F1}, radarAlt {t.lastAlt:F1} m; near: {near}");
            }
            foreach (var id in gone) tracks.Remove(id);
        }

        void WatchAircraft()
        {
            WatchGone();
            foreach (var ac in UnitRegistry.allAircraft)
            {
                if (ac == null || ac.disabled) continue;
                int id = ac.GetInstanceID();
                var p = ac.GlobalPosition().AsVector3();
                if (!tracks.TryGetValue(id, out var tr))
                    tracks[id] = tr = new Track { ac = ac, fac = Fac(ac.NetworkHQ), key = ac.definition?.jsonKey, pilot = Pilot(ac),
                                                  first = p, last = p, t0 = Plugin.Now };
                tr.path += Vector3.Distance(tr.last, p);
                tr.last = p; tr.lastLocal = ac.transform.position; tr.lastSpeed = ac.speed; tr.lastAlt = ac.radarAlt;
                tr.maxSpeed = Mathf.Max(tr.maxSpeed, ac.speed);
                if (ac.radarAlt > 60f) tr.wasAirborne = true;
                if (seenAircraft.Add(id))
                {
                    var nb = Nearest(p, out float d);
                    string tko = "";
                    try
                    {
                        var prm = ac.GetAircraftParameters();
                        if (prm != null) tko = $", takeoffDistance {prm.takeoffDistance:F0} m, takeoffSpeed {prm.takeoffSpeed:F0} m/s";
                    }
                    catch { }
                    Note($"SPAWN {Fac(ac.NetworkHQ)} {ac.definition?.jsonKey} {Pilot(ac)} at {P(p)}, " +
                         $"{d:F0} m from '{(nb != null ? Name(nb) : "?")}' at mission time {Plugin.Now:F0} s{tko}");
                }
                if (!airborne.Contains(id) && ac.radarAlt > 60f && ac.speed > 30f)
                {
                    airborne.Add(id);
                    Note($"AIRBORNE {Fac(ac.NetworkHQ)} {ac.definition?.jsonKey} {Pilot(ac)} at {P(p)}, " +
                         $"speed {ac.speed:F0} m/s, radarAlt {ac.radarAlt:F0} m, mission time {Plugin.Now:F0} s");
                }
            }
        }

        void Tick()
        {
            var live = UnitRegistry.allAircraft.Where(a => a != null && !a.disabled).ToList();
            var by = live.GroupBy(a => Fac(a.NetworkHQ)).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} {g.Count()} ({g.Count(a => a.radarAlt > 60f)} flying)");
            Note($"t={Plugin.Now:F0} s aircraft: {(live.Count > 0 ? string.Join(", ", by) : "none")}; " +
                 $"seen {seenAircraft.Count}, airborne so far {airborne.Count}");
            var ground = live.Where(a => a.radarAlt < 10f).Select(a =>
                $"{Fac(a.NetworkHQ)} {a.definition?.jsonKey} at {P(a.GlobalPosition().AsVector3())} {a.speed:F1} m/s");
            if (ground.Any()) Note("  on ground: " + string.Join("; ", ground));
        }

        IEnumerator SpawnAt(NOPlayer player, string baseName, string key)
        {
            // 'carrier': the ship-attached airbase with the most hangars (a carrier rather than a destroyer's helipad).
            Airbase ab = baseName == "carrier"
                ? FactionRegistry.airbaseLookup.Values.Where(a => a != null && a.SavedAirbase != null &&
                        (a.SavedAirbase.UniqueName ?? "").StartsWith(Airbase.ATTACHED_AIRBASE_PREFIX))
                    .OrderByDescending(a => a.hangars.Count).FirstOrDefault()
                : FactionRegistry.airbaseLookup.Values.FirstOrDefault(a => a != null &&
                    (Name(a).IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     (a.SavedAirbase?.UniqueName ?? "").IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0));
            if (ab == null)
            {
                Note($"SPAWN TEST FAIL: no airbase matching '{baseName}' (known: {string.Join(", ", FactionRegistry.airbaseLookup.Keys)})");
                yield break;
            }
            var def = Encyclopedia.i.aircraft.FirstOrDefault(a => a != null && a.jsonKey == key);
            if (def == null) { Note($"SPAWN TEST FAIL: no aircraft '{key}'"); yield break; }
            if (ab.CurrentHQ != null && player.HQ != ab.CurrentHQ)
            {
                player.SetFaction(ab.CurrentHQ);
                Note($"joined {Fac(ab.CurrentHQ)}");
            }
            try { SceneSingleton<GameplayUI>.i.CloseJoinMenu(); } catch { }
            yield return new WaitForSecondsRealtime(2f);
            Note($"SPAWN TEST '{Name(ab)}' {key}: can spawn {ab.CanSpawnAircraft(def)}; " +
                 $"hangar types {string.Join(" ", ab.GetAvailableAircraft().Where(d => d != null).Select(d => d.jsonKey))}");
            if (IsClientRole || Env("TERRAINLAB_MISSION_PROBE_SPAWN_MODE", "direct") == "hangar")
            {
                // The real player path: the server spawns only airframes the player owns, so buy one first (the
                // spawn menu's buy button), then send the spawn menu's hangar spawn request.
                Note($"SPAWN TEST player path: allocation {player.Allocation:F0}, {key} costs {def.value:F0}, owned {player.OwnsAirframe(def, true)}");
                if (!player.OwnsAirframe(def, true))
                {
                    player.CmdPurchaseAirframe(def);
                    float tb = Time.realtimeSinceStartup;
                    while (!player.OwnsAirframe(def, true) && Time.realtimeSinceStartup - tb < 10f) yield return null;
                    Note($"SPAWN TEST bought {key}: {(player.OwnsAirframe(def, true) ? "owned" : "FAIL, still not owned")} (allocation now {player.Allocation:F0})");
                }
                spawnResult = null;
                RequestSpawn(NetworkSceneSingleton<Spawner>.i, ab, def).Forget();
                float tr = Time.realtimeSinceStartup;
                while (spawnResult == null && Time.realtimeSinceStartup - tr < 20f) yield return null;
                Note($"SPAWN TEST request result: {(spawnResult == null ? "no answer in 20 s" : spawnResult.Value ? "allowed" : "refused")}");
                if (spawnResult != true) yield break;
            }
            else
            {
                Airbase.TrySpawnResult r = default(Airbase.TrySpawnResult);
                try { r = ab.TrySpawnAircraft(player, def, default(LiveryKey), null, 0.8f); }
                catch (Exception e) { Note($"SPAWN TEST exception: {e.Message}"); yield break; }
                Note($"SPAWN TEST result: allowed {r.Allowed}, delayed {r.DelayedSpawn}");
                if (!r.Allowed) yield break;
            }
            float tw = Time.realtimeSinceStartup;
            Aircraft local = null;
            while (Time.realtimeSinceStartup - tw < 40f)
            {
                if (GameManager.GetLocalAircraft(out local) && local != null) break;
                yield return null;
            }
            if (local == null) { Note("SPAWN TEST FAIL: no local aircraft 40 s after the spawn"); yield break; }
            var p = local.GlobalPosition().AsVector3();
            Note($"SPAWN TEST OK: local {key} at {P(p)}, {Vector3.Distance(p, ab.transform.GlobalPosition().AsVector3()):F0} m from the airbase centre, " +
                 $"radarAlt {local.radarAlt:F1} m, speed {local.speed:F1} m/s");
            yield return new WaitForSecondsRealtime(3f);
            yield return Shot("spawn");
        }

        async Cysharp.Threading.Tasks.UniTaskVoid RequestSpawn(Spawner spawner, Airbase ab, AircraftDefinition def)
        {
            try { spawnResult = await spawner.RequestSpawnAtAirbase(ab, def, default(LiveryKey), new NuclearOption.SavedMission.Loadout(), 0.8f); }
            catch (Exception e) { Note($"SPAWN TEST request threw: {e.Message}"); spawnResult = false; }
        }

        IEnumerator Shot(string tag)
        {
            yield return new WaitForEndOfFrame();
            string path = Path.Combine(outDir, $"{tag}.png");
            try { ScreenCapture.CaptureScreenshot(path); Note($"shot {path}"); }
            catch (Exception e) { Note($"shot {tag} failed: {e.Message}"); }
            yield return new WaitForSecondsRealtime(1f);
        }
    }
}
