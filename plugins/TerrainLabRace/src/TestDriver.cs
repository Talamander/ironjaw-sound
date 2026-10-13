using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using NOPlayer = NuclearOption.Networking.Player;

namespace TerrainLab.Race
{
    /// <summary>
    /// Headless single-player test (environment TERRAINLAB_RACE_TEST=1, never on by config).
    /// Joins Boscali, opens the grid, spawns the local player through the same Spawner.SpawnAircraft path a hangar
    /// uses (so the air-start patch places it), flies the course with the game's autopilot standing in for the
    /// stick, and saves screenshots at the spawn, at gate passes and at the finish.
    ///
    /// Environment:
    ///   TERRAINLAB_RACE_TEST=1               turn the test on
    ///   TERRAINLAB_RACE_TEST_AIRCRAFT=key    aircraft jsonKey (default Multirole1)
    ///   TERRAINLAB_RACE_TEST_DELAY=s         seconds after the grid opens before the spawn (default 8)
    ///   TERRAINLAB_RACE_TEST_FLY=0           only spawn, no autopilot
    ///   TERRAINLAB_RACE_TEST_MAXTIME=s       quit this long after GO even if not finished (default 600)
    ///   TERRAINLAB_RACE_TEST_SPEED=m/s       autopilot cruise speed cap (default 160)
    ///   TERRAINLAB_RACE_SHOTS=dir            screenshot root (default race-shots in the game folder)
    /// </summary>
    internal class TestDriver : MonoBehaviour
    {
        internal static TestDriver I;
        public static bool Enabled => Environment.GetEnvironmentVariable("TERRAINLAB_RACE_TEST") == "1";

        public bool AutopilotOn;
        public Aircraft Flying;
        string outDir;
        readonly StringBuilder report = new StringBuilder();
        int shotNo;
        float speedCap = 160f;
        int aimGate;            // gate the autopilot steers for; moves on past a gate it overshot (judged as missed)
        bool goAround;          // missed the finish: fly back out along the finish axis and try again
        float nextFlyLog;
        bool? spawnResult;      // answer to a hangar spawn request (null while pending)

        /// <summary>TERRAINLAB_RACE_TEST_ROLE=client: this machine joined someone else's race (local multiplayer test).</summary>
        static bool IsClient => Env("TERRAINLAB_RACE_TEST_ROLE", "host") == "client";

        static string[] SafeTypes(Airbase a)
        {
            try { return a.GetAvailableAircraft().Where(d => d != null).Select(d => d.jsonKey).ToArray(); }
            catch { return new string[0]; }
        }

        async Cysharp.Threading.Tasks.UniTaskVoid RequestSpawn(Spawner spawner, Airbase ab, AircraftDefinition def)
        {
            try { spawnResult = await spawner.RequestSpawnAtAirbase(ab, def, default(LiveryKey), new NuclearOption.SavedMission.Loadout(), 0.8f); }
            catch (Exception e) { Note($"hangar spawn request threw: {e.Message}"); spawnResult = false; }
        }

        static string Env(string k, string d)
        {
            var v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? d : v;
        }

        static float EnvF(string k, float d) =>
            float.TryParse(Env(k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : d;

        void Awake() { I = this; }

        void Start()
        {
            outDir = Path.GetFullPath(Path.Combine(Env("TERRAINLAB_RACE_SHOTS", "race-shots"), DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(outDir);
            speedCap = EnvF("TERRAINLAB_RACE_TEST_SPEED", 160f);
            Note($"test driver on, shots in {outDir}");
            StartCoroutine(Run());
        }

        void Note(string s)
        {
            Plugin.Log.LogInfo("[Race] TEST " + s);
            report.AppendLine($"{DateTime.Now:HH:mm:ss} {s}");
            try { File.WriteAllText(Path.Combine(outDir, "test.txt"), report.ToString()); } catch { }
        }

        IEnumerator Run()
        {
            float t0 = Time.realtimeSinceStartup;
            NOPlayer player = null;
            bool client = IsClient;
            Note($"role {(client ? "client" : "host")}");
            while (Time.realtimeSinceStartup - t0 < 300f)
            {
                if (MissionManager.IsRunning && Plugin.Active != null && (client ? Plugin.View != null : Plugin.Host != null) &&
                    GameManager.GetLocalPlayer<NOPlayer>(out player) && player != null) break;
                yield return new WaitForSecondsRealtime(1f);
            }
            if ((client ? Plugin.View == null : Plugin.Host == null) || player == null)
            {
                Note($"FAIL: no race mission or no local player after {Time.realtimeSinceStartup - t0:F0} s " +
                     $"(running {MissionManager.IsRunning}, course {(Plugin.Active != null ? Plugin.Active.Name : "none")}, server {Plugin.IsServer})");
                yield break;
            }
            var c = Plugin.Active;
            Note($"mission running, course '{c.Name}', player '{player.GetPlayerName()?.SanitizedName}'");
            LogAircraftTable();
            yield return new WaitForSecondsRealtime(5f);

            var hq = FactionRegistry.HqFromName("Boscali");
            if (player.HQ == null && hq != null)
            {
                player.SetFaction(hq);
                Note($"joined faction {hq.faction.factionName}");
            }
            // A real player clicks through the join menu; close it so the screenshots show the flight view.
            try { SceneSingleton<GameplayUI>.i.CloseJoinMenu(); Note("join menu closed"); }
            catch (Exception e) { Note($"join menu close failed: {e.Message}"); }
            yield return new WaitForSecondsRealtime(1f);

            if (client)
            {
                // A client cannot open the grid; wait for the host's grid tag in chat, as a real racer would.
                float tg = Time.realtimeSinceStartup;
                float waitGrid = EnvF("TERRAINLAB_RACE_TEST_GRID_WAIT", 600f);
                while (float.IsNaN(Plugin.View.GoTime) && Time.realtimeSinceStartup - tg < waitGrid)
                    yield return new WaitForSecondsRealtime(0.5f);
                if (float.IsNaN(Plugin.View.GoTime))
                {
                    Note($"FAIL: no grid from the host after {waitGrid:F0} s");
                    yield break;
                }
                Note($"grid seen from the host: GO in {Plugin.View.GoTime - Plugin.Now:F1} s, faction {(player.HQ != null ? player.HQ.faction.factionName : "none")}");
            }
            else
            {
                // Local multiplayer test: wait for the other test instance to join before opening the grid, so it
                // sees the grid message in chat like a racer in the lobby would.
                int wantPlayers = Mathf.RoundToInt(EnvF("TERRAINLAB_RACE_TEST_WAIT_PLAYERS", 1f));
                if (wantPlayers > 1)
                {
                    float tp = Time.realtimeSinceStartup;
                    while (FindObjectsOfType<NOPlayer>().Length < wantPlayers && Time.realtimeSinceStartup - tp < 400f)
                        yield return new WaitForSecondsRealtime(1f);
                    Note($"players in session: {FindObjectsOfType<NOPlayer>().Length} (waited {Time.realtimeSinceStartup - tp:F0} s for {wantPlayers}): " +
                         string.Join(", ", FindObjectsOfType<NOPlayer>().Select(x => x.GetPlayerName()?.SanitizedName + "/" + (x.HQ != null ? x.HQ.faction.factionName : "no faction"))));
                    yield return new WaitForSecondsRealtime(EnvF("TERRAINLAB_RACE_TEST_GRID_DELAY", 20f));
                }
                Plugin.Host.Command("grid", "test driver");
            }
            yield return new WaitForSecondsRealtime(EnvF("TERRAINLAB_RACE_TEST_DELAY", 8f));

            string key = Env("TERRAINLAB_RACE_TEST_AIRCRAFT", "Multirole1");
            var def = Encyclopedia.i.aircraft.FirstOrDefault(a => a != null && a.jsonKey == key);
            if (def == null)
            {
                Note($"FAIL: no aircraft '{key}'; known: {string.Join(", ", Encyclopedia.i.aircraft.Where(a => a != null).Select(a => a.jsonKey))}");
                yield break;
            }
            var spawner = NetworkSceneSingleton<Spawner>.i;
            Aircraft ac = null;
            if (client || Env("TERRAINLAB_RACE_TEST_SPAWN", "direct") == "hangar")
            {
                // The real player path: ask the server for a hangar spawn at one of our airbases (the same request the
                // spawn menu sends). The host's air-start patch then moves the new aircraft onto the grid.
                Airbase ab = FactionRegistry.airbaseLookup.Values.FirstOrDefault(a => a != null && !a.disabled &&
                    a.CurrentHQ == player.HQ && SafeTypes(a).Contains(def.jsonKey));
                if (ab == null)
                {
                    Note($"FAIL: no {(player.HQ != null ? player.HQ.faction.factionName : "?")} airbase can spawn {def.jsonKey}");
                    yield break;
                }
                // The server only spawns airframes the player owns, so buy one first like the spawn menu's buy
                // button does (race missions give every player 99999 to spend).
                if (!player.OwnsAirframe(def, true))
                {
                    player.CmdPurchaseAirframe(def);
                    float tb = Time.realtimeSinceStartup;
                    while (!player.OwnsAirframe(def, true) && Time.realtimeSinceStartup - tb < 10f) yield return null;
                    Note($"bought airframe {def.jsonKey}: {(player.OwnsAirframe(def, true) ? "owned" : "FAIL, still not owned")} " +
                         $"(allocation now {player.Allocation:F0})");
                }
                spawnResult = null;
                RequestSpawn(spawner, ab, def).Forget();
                float tr = Time.realtimeSinceStartup;
                while (spawnResult == null && Time.realtimeSinceStartup - tr < 20f) yield return null;
                Note($"hangar spawn request at '{ab.SavedAirbase?.DisplayName ?? ab.name}': {(spawnResult == null ? "no answer" : spawnResult.Value ? "allowed" : "refused")}");
                if (spawnResult != true) yield break;
            }
            else
            {
                // Stand-in for a hangar spawn: any position, zero speed, a hangar-like call. The race patch moves it.
                var start = new GlobalPosition(0f, 2000f, 0f);
                spawner.RpcOnGivenAircraftControl(player.Owner);
                ac = spawner.SpawnAircraft(player, def.unitPrefab, null, 0.8f, default(LiveryKey), start, Quaternion.identity,
                    Vector3.zero, null, player.HQ, null, 1f, 0.5f);
            }
            Note($"spawn requested: {def.unitName} ({def.jsonKey}) for {player.GetPlayerName()?.SanitizedName}");

            float tw = Time.realtimeSinceStartup;
            Aircraft local = null;
            while (Time.realtimeSinceStartup - tw < 20f)
            {
                if (GameManager.GetLocalAircraft(out local) && local != null) break;
                yield return null;
            }
            if (local == null) local = ac;
            if (local == null) { Note("FAIL: no local aircraft after spawn"); yield break; }
            Flying = local;
            var p = local.GlobalPosition().AsVector3();
            var g0 = c.Gates[0];
            Note($"AIR START check: pos {RaceHost.Fmt(p)} heading {local.transform.eulerAngles.y:F0} speed {local.speed:F0} m/s " +
                 $"radarAlt {local.radarAlt:F0} m, {Vector3.Dot(g0.P - p, c.GridDir):F0} m before the start line, " +
                 $"GO in {Plugin.View.GoTime - Plugin.Now:F1} s, pilot state {(local.pilots != null && local.pilots.Length > 0 ? local.pilots[0].currentState?.GetType().Name : "?")}");

            try { SceneSingleton<DynamicMap>.i.Minimize(); } catch { }
            Chase(local);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Shot("spawn");
            p = local.GlobalPosition().AsVector3();
            Note($"after 1.5 s: pos {RaceHost.Fmt(p)} speed {local.speed:F0} m/s radarAlt {local.radarAlt:F0} m");

            var view = Plugin.View;
            bool finished = false;
            int lastShotGate = -1;
            if (view != null)
                view.GatePassed += (i, text) =>
                {
                    Note($"GATE {i} '{c.Gates[i].Name}': {text}");
                    if (i == c.Gates.Count - 1) finished = true;
                    if (i <= 2 || c.Gates[i].Split || i == c.Gates.Count - 1) lastShotGate = i;
                };

            if (Env("TERRAINLAB_RACE_TEST_FLY", "1") == "1")
            {
                AutopilotOn = true;
                Note("autopilot on");
            }

            float maxT = EnvF("TERRAINLAB_RACE_TEST_MAXTIME", 600f);
            int shotGate = -1;
            float nextAmbient = Time.realtimeSinceStartup + 20f;
            while (!finished && Plugin.View != null && (float.IsNaN(Plugin.View.GoTime) || Plugin.Now - Plugin.View.GoTime < maxT))
            {
                if (local == null || local.disabled)
                {
                    Note("aircraft lost (crash?)");
                    yield return new WaitForSecondsRealtime(2f);
                    yield return Shot("lost");
                    break;
                }
                // The host's finish tag can arrive before the local tracker sees the last ring (on the host it
                // always can: the judge runs in the same frame). The tag then closes the course without a
                // GatePassed event, so check the view's finish time too.
                if (view != null && view.Finish >= 0f)
                {
                    Note($"finish tag received: {view.Finish:F2} s");
                    finished = true;
                    break;
                }
                if (lastShotGate != shotGate)
                {
                    shotGate = lastShotGate;
                    yield return new WaitForSecondsRealtime(0.4f);
                    yield return Shot($"gate{shotGate:00}");
                }
                // A look ahead at the next ring from behind the aircraft, once early on.
                if (Time.realtimeSinceStartup >= nextAmbient)
                {
                    nextAmbient = Time.realtimeSinceStartup + 45f;
                    yield return Shot("cruise");
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot(finished ? "finish" : "end");
            // The host's quit would end the session for everyone: on a host, wait for the results (all racers
            // finished or the time limit) before quitting. A client keeps the session alive for its log too.
            float tw2 = Time.realtimeSinceStartup;
            while (Plugin.Host != null && Plugin.Host.Phase != Phase.Done && Time.realtimeSinceStartup - tw2 < maxT)
                yield return new WaitForSecondsRealtime(1f);
            Note($"race state: {(Plugin.Host != null ? Plugin.Host.Phase.ToString() : "?")}, local next gate {view?.Next}, finish {view?.Finish:F2}, " +
                 $"penalty {view?.Penalty:F0}, missed {view?.Missed}");
            if (Plugin.Host != null)
                foreach (var r in Plugin.Host.Racers)
                    Note($"host racer {r.Name}: next {r.Next}, finish {r.Finish:F2}, penalty {r.Penalty:F0}, missed {r.Missed}, jumped {r.Jumped}");
            yield return new WaitForSecondsRealtime(6f);
            Note("done, quitting");
            Application.Quit();
        }

        void Chase(Aircraft a)
        {
            try
            {
                var cam = SceneSingleton<CameraStateManager>.i;
                if (cam == null) return;
                cam.followingUnit = a;
                cam.SwitchState(cam.chaseState);
                Note("camera: chase");
            }
            catch (Exception e) { Note($"camera switch failed: {e.Message}"); }
        }

        IEnumerator Shot(string tag)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                string name = $"{++shotNo:00}-{tag}.png";
                File.WriteAllBytes(Path.Combine(outDir, name), tex.EncodeToPNG());
                string where = Flying != null ? $"aircraft {RaceHost.Fmt(Flying.GlobalPosition().AsVector3())} speed {Flying.speed:F0} radarAlt {Flying.radarAlt:F0}" : "";
                Note($"shot {name} {tex.width}x{tex.height} t={Plugin.Now:F1} {where}");
                Destroy(tex);
            }
            catch (Exception e) { Note($"shot {tag} failed: {e.Message}"); }
        }

        void LogAircraftTable()
        {
            try
            {
                foreach (var d in Encyclopedia.i.aircraft)
                {
                    if (d == null || d.aircraftParameters == null) continue;
                    var ap = d.aircraftParameters;
                    Note($"aircraft {d.jsonKey,-14} '{d.unitName}': maxSpeed {ap.maxSpeed:F0} cornerSpeed {ap.cornerSpeed:F0} " +
                         $"turningRadius {ap.turningRadius:F0} gLimit {ap.aircraftGLimit:F1} cruiseThrottle {ap.cruiseThrottle:F2} " +
                         $"landingSpeed {ap.landingSpeed:F0} takeoffDistance {ap.takeoffDistance:F0}");
                }
            }
            catch (Exception e) { Note($"aircraft table failed: {e.Message}"); }
        }

        /// <summary>One physics step of the test autopilot. Returns true when it set the inputs.</summary>
        public bool Fly(Aircraft a)
        {
            if (a != Flying || a.autopilot == null) return false;
            var c = Plugin.Active;
            var view = Plugin.View;
            if (c == null || view == null) return false;
            Vector3 pos = a.GlobalPosition().AsVector3();
            // The game's autopilot cannot always make a tight gate. Once it is past a gate's plane without passing
            // through, steer for the next one; the judge then counts the skipped gate as missed (+10 s).
            if (aimGate < view.Next) aimGate = view.Next;
            while (aimGate < c.Gates.Count - 1 && Vector3.Dot(pos - c.Gates[aimGate].P, c.Gates[aimGate].N) > 50f)
            {
                Plugin.Log.LogInfo($"[Race] TEST autopilot overshot gate {aimGate} '{c.Gates[aimGate].Name}', steering for the next");
                aimGate++;
            }
            int k = Mathf.Min(aimGate, c.Gates.Count - 1);
            var g = c.Gates[k];
            bool done = view.Finish >= 0f;

            Vector3 to = g.P - pos; to.y = 0f;
            float dist = to.magnitude;
            // The finish cannot be skipped. If the autopilot flies past it (often too high after the gates before),
            // it turns back out 2.5 km along the finish axis, settles at gate height and runs in again.
            bool finishGate = k == c.Gates.Count - 1 && !done;
            if (finishGate && !goAround && Vector3.Dot(pos - g.P, g.N) > 50f)
            {
                goAround = true;
                Plugin.Log.LogInfo($"[Race] TEST autopilot missed the finish, going around");
            }
            Vector3 runIn = g.P - g.N * 2500f;
            if (goAround && Vector3.Distance(Flat(pos), Flat(runIn)) < 700f) goAround = false;

            Vector3 aim;
            if (done)
                aim = pos + a.transform.forward * 2000f;
            else if (finishGate && goAround)
                aim = runIn;
            else if (dist > 900f)
                aim = g.P - g.N * Mathf.Min(dist * 0.35f, 600f);   // come in along the gate axis
            else
                aim = g.P + g.N * 500f;                             // through the ring

            // Height: blend from the previous gate's height to this one's, hold it above the ground under us.
            float yPrev = k > 0 ? c.Gates[k - 1].P.y : g.P.y;
            float legLen = k > 0 ? Vector3.Distance(Flat(c.Gates[k - 1].P), Flat(g.P)) : dist + 1f;
            float frac = Mathf.Clamp01(1f - dist / Mathf.Max(legLen, 1f));
            float wantY = finishGate && goAround ? g.P.y : Mathf.Lerp(yPrev, g.P.y, frac);
            float ground = pos.y - a.radarAlt;
            float hold = Mathf.Max(35f, wantY - ground);

            var ap = a.GetAircraftParameters();
            float vt = speedCap;
            if (!done && g.VmaxMs > 1f) vt = Mathf.Min(vt, g.VmaxMs * 0.9f);
            // Above corner speed the game's autopilot turns by climbing; slow down for a finish go-around.
            if (finishGate && (goAround || Mathf.Abs(pos.y - g.P.y) > 150f)) vt = Mathf.Min(vt, 120f);
            // Before GO, pace to the start line like a racer would: arrive as the clock hits zero.
            float toGo = view.GoTime - Plugin.Now;
            if (k == 0 && !float.IsNaN(toGo) && toGo > 0.5f)
                vt = Mathf.Clamp(RaceView.PaceSpeed(c, pos, toGo), 0.7f * c.GridSpeed, 1.15f * c.GridSpeed);
            if (ap != null && ap.maxSpeed > 1f) vt = Mathf.Min(vt, ap.maxSpeed * 0.85f);
            float cruise = ap != null ? ap.cruiseThrottle : 0.8f;
            var inputs = a.GetInputs();
            inputs.throttle = Mathf.Clamp01(cruise + (vt - a.speed) * 0.03f);

            var dest = new GlobalPosition(aim.x, pos.y, aim.z);
            if (a.autopilot is AutopilotHelo)
                a.autopilot.AutoAim(dest, hold, Vector3.zero, Vector3.zero, followTerrain: true);
            else
                a.autopilot.AutoAim(dest, aimVelocity: true, ignoreCollisions: false, runwayAlign: false, effort: 1f,
                    bankAllowed: 110f, followTerrain: true, altitudeHold: hold, Vector3.zero);

            if (Time.unscaledTime >= nextFlyLog)
            {
                nextFlyLog = Time.unscaledTime + 5f;
                Plugin.Log.LogInfo($"[Race] TEST fly t={Plugin.Now:F1} pos={RaceHost.Fmt(pos)} speed={a.speed:F0}/{vt:F0} " +
                                   $"radarAlt={a.radarAlt:F0} hold={hold:F0} next={k} '{g.Name}' dist={dist:F0} gateY={g.P.y:F0} throttle={inputs.throttle:F2}");
            }
            return true;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
