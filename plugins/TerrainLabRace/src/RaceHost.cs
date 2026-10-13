using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using NOPlayer = NuclearOption.Networking.Player;

namespace TerrainLab.Race
{
    internal enum Phase { Lobby, Grid, Racing, Done }

    internal class Racer
    {
        public NOPlayer Player;
        public string Name;
        public int Slot = -1;
        public Aircraft Aircraft;
        public string AircraftName = "";
        public Vector3 Last;
        public bool HasLast;
        public int Next;               // index of the next gate to pass
        public float Penalty;
        public int Missed;
        public bool Jumped;
        public int Respawns;
        public float Finish = -1f;     // race time incl. penalties, or -1
        public int FinishSeq;          // order of crossing the line (tie-break for equal times)
        public bool Left;              // disconnected; kept for the results and a rejoin under the same name
        public readonly Dictionary<int, float> GateTimes = new Dictionary<int, float>();
        public bool Finished => Finish >= 0f;
    }

    /// <summary>
    /// Server side of the race: start grid, air-start spawns, gate detection for every racer, rules and results.
    /// Player aircraft are client-authoritative in Nuclear Option, so the host cannot move them after spawn. It
    /// changes where they spawn instead (<see cref="Patches.SpawnAircraftPatch"/>), and judges gates from the
    /// positions every client already sends.
    /// </summary>
    internal class RaceHost
    {
        readonly Course c;
        public Phase Phase = Phase.Lobby;
        public float TOpen = float.NaN, TGo = float.NaN;
        public readonly List<Racer> Racers = new List<Racer>();
        float nextStatusLog;
        int finishSeq;
        readonly HashSet<NOPlayer> seen = new HashSet<NOPlayer>();

        public RaceHost(Course course) { c = course; }

        public float RaceTime => Plugin.Now - TGo;

        // ------------------------------------------------------------------ commands

        public void Command(string cmd, string from)
        {
            Plugin.Log.LogInfo($"[Race] command '{cmd}' from {from} in phase {Phase}");
            switch (cmd)
            {
                case "grid":
                    OpenGrid();
                    break;
                case "go":
                    if (Phase == Phase.Lobby) OpenGrid();
                    if (Phase == Phase.Grid) Go(Plugin.Now);
                    break;
                case "reset":
                    Reset();
                    break;
                case "status":
                    Plugin.Broadcast(Status());
                    break;
                case "end":
                    if (Phase == Phase.Racing) Results("ended by the host");
                    else Plugin.Broadcast($"No race running ({Phase}).");
                    break;
            }
        }

        void OpenGrid()
        {
            if (Phase == Phase.Grid || Phase == Phase.Racing)
            {
                Plugin.Broadcast($"Race already {(Phase == Phase.Grid ? "on the grid" : "running")}; '!race reset' first.");
                return;
            }
            Racers.Clear();
            TOpen = Plugin.Now;
            TGo = TOpen + c.GridApproach / Mathf.Max(c.GridSpeed, 1f);
            Phase = Phase.Grid;
            Plugin.Broadcast($"Grid open: {c.Name}. Spawn now in any aircraft, you start in the air on the grid. " +
                             $"GO in {TGo - TOpen:F0} s. [race grid go={Inv(TGo)} open={Inv(TOpen)} course={c.Id}]");
        }

        void Go(float t)
        {
            TGo = t;
            Phase = Phase.Racing;
            Plugin.Broadcast($"GO! {c.Name}, {c.Gates.Count} gates. [race go go={Inv(TGo)}]");
        }

        void Reset()
        {
            Phase = Phase.Lobby;
            Racers.Clear();
            TOpen = TGo = float.NaN;
            Plugin.Broadcast("Race reset. Host: '!race grid' opens the start grid. [race reset]");
        }

        string Status()
        {
            var sb = new StringBuilder($"{c.Name}: {Phase}");
            if (Phase == Phase.Grid) sb.Append($", GO in {TGo - Plugin.Now:F0} s");
            if (Phase == Phase.Racing) sb.Append($", {Course.Clock(RaceTime)}");
            foreach (var r in Racers)
                sb.Append($"; {r.Name} {(r.Finished ? Course.Clock(r.Finish) : $"gate {r.Next}/{c.Gates.Count - 1}")}{(r.Left ? " (left)" : "")}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ spawns

        Racer Get(NOPlayer p, bool create)
        {
            var r = Racers.FirstOrDefault(x => !x.Left && x.Player == p);
            if (r == null && create)
            {
                string name = SafeName(p);
                // A player who dropped out and came back under the same name keeps their progress.
                r = Racers.FirstOrDefault(x => x.Left && x.Name == name);
                if (r != null)
                {
                    r.Left = false;
                    r.Player = p;
                    r.Aircraft = null;
                    r.HasLast = false;
                    Plugin.Broadcast($"{name} is back in the race at gate {r.Next}/{c.Gates.Count - 1}.");
                }
                else
                {
                    r = new Racer { Player = p, Name = name };
                    Racers.Add(r);
                }
            }
            return r;
        }

        int NextSlot()
        {
            for (int k = 0; ; k++)
                if (!Racers.Any(r => r.Slot == k && !r.Left)) return k;
        }

        static string SafeName(NOPlayer p)
        {
            try { return p.GetPlayerName()?.SanitizedName ?? "player"; }
            catch { return "player"; }
        }

        /// <summary>
        /// Decides where a player's new aircraft appears. Returns false to leave the stock spawn alone (lobby).
        /// </summary>
        public bool PlaceSpawn(NOPlayer player, GameObject prefab, out Vector3 pos, out Quaternion rot, out float speed, out string why)
        {
            pos = Vector3.zero; rot = Quaternion.identity; speed = 0f; why = "";
            if (Phase == Phase.Lobby && Plugin.AutoGridOnFirstSpawn.Value) OpenGrid();
            if (Phase == Phase.Lobby || Phase == Phase.Done) return false;

            float t = Plugin.Now;
            var r = Get(player, create: true);
            if (r.Slot < 0) r.Slot = NextSlot();
            var ac = prefab != null ? prefab.GetComponent<Aircraft>() : null;
            float vmax = ac != null && ac.definition != null && ac.definition.aircraftParameters != null
                ? ac.definition.aircraftParameters.maxSpeed : 0f;
            speed = c.GridSpeed;
            if (vmax > 1f) speed = Mathf.Min(speed, Plugin.GridSpeedFraction.Value * vmax);

            if (r.Finished)
            {
                // Finished racers who respawn fly a lap of honour from the start grid position.
                why = "finished, grid start";
            }
            if (r.Next > 0 && !r.Finished)
            {
                // Respawn at the last gate passed, heading for the next one.
                var g = c.Gates[r.Next - 1];
                var n = c.Gates[Mathf.Min(r.Next, c.Gates.Count - 1)];
                Vector3 d = n.P - g.P; d.y = 0f;
                if (d.sqrMagnitude < 1f) d = g.N;
                pos = g.P;
                rot = Quaternion.LookRotation(d.normalized, Vector3.up);
                r.Penalty += c.RespawnPenalty;
                r.Respawns++;
                r.HasLast = false;
                why = $"respawn at {g.Name} (gate {g.Index}) for gate {r.Next}";
                Plugin.Broadcast($"{r.Name} respawns at {g.Name} (+{c.RespawnPenalty:F0} s). " +
                                 $"[race respawn gate={r.Next} pen={Inv(r.Penalty)} missed={r.Missed} who={r.Name}]");
                return true;
            }
            // Rolling grid: the slots move along the grid heading at grid speed from TOpen and reach gate 0 at TGo.
            // A late spawn (grid gone) takes its slot as it was 10 s before GO, so it still starts behind the line.
            int k = r.Slot % Math.Max(1, c.Slots.Count);
            float moved = c.GridSpeed * Mathf.Clamp(t - TOpen, 0f, Mathf.Max(0f, TGo - TOpen - 10f));
            // Extra rows beyond the slot table go further back.
            float rowBack = (r.Slot / Math.Max(1, c.Slots.Count)) * 300f;
            pos = c.Slots[k] + c.GridDir * (moved - rowBack);
            rot = Quaternion.LookRotation(c.GridDir, Vector3.up);
            r.HasLast = false;
            why = $"grid slot {r.Slot + 1}, {moved:F0} m rolled, {Vector3.Dot(c.Gates[0].P - pos, c.GridDir):F0} m to the start line";
            return true;
        }

        // ------------------------------------------------------------------ judging

        public void Tick()
        {
            float t = Plugin.Now;
            if (Phase == Phase.Grid && t >= TGo) Go(TGo);

            // Players who left: their Player object is gone. Keep them for the results, out of "all finished".
            foreach (var r in Racers)
            {
                if (r.Left || r.Player != null) continue;
                r.Left = true;
                r.Aircraft = null;
                r.HasLast = false;
                if (Phase == Phase.Grid || Phase == Phase.Racing)
                    Plugin.Broadcast($"{r.Name} left the race{(r.Finished ? "" : $" at gate {r.Next}/{c.Gates.Count - 1}")}.");
            }
            seen.RemoveWhere(p => p == null);

            // Late joiners: tell their plugin (if any) where the race stands. Clients without the plugin just read it.
            foreach (var p in UnitRegistry.playerLookup.Values)
            {
                if (p == null || seen.Contains(p)) continue;
                seen.Add(p);
                if ((Phase == Phase.Grid || Phase == Phase.Racing) && !p.IsLocalPlayer) Sync(SafeName(p));
            }

            // Pick up players who fly in the race without a grid spawn (stock air-start slots, joined mid-grid).
            if (Phase == Phase.Grid || Phase == Phase.Racing)
            {
                foreach (var p in UnitRegistry.playerLookup.Values)
                {
                    if (p == null || p.Aircraft == null) continue;
                    var r = Get(p, create: true);
                    if (r.Aircraft != p.Aircraft)
                    {
                        r.Aircraft = p.Aircraft;
                        r.AircraftName = p.Aircraft.definition != null ? p.Aircraft.definition.unitName : "?";
                        r.HasLast = false;
                        Plugin.Log.LogInfo($"[Race] racer {r.Name} in {r.AircraftName} at {Fmt(p.Aircraft.GlobalPosition().AsVector3())} " +
                                           $"speed {p.Aircraft.speed:F0} m/s, next gate {r.Next}");
                    }
                }
            }

            foreach (var r in Racers)
            {
                var a = r.Aircraft;
                if (r.Left || a == null || a.disabled)
                {
                    r.HasLast = false;
                    continue;
                }
                Vector3 p = a.GlobalPosition().AsVector3();
                if (r.HasLast && !r.Finished) Judge(r, r.Last, p, t);
                r.Last = p;
                r.HasLast = true;
            }

            if (Phase == Phase.Racing)
            {
                bool allDone = Racers.Any(x => x.Finished) && Racers.All(x => x.Finished || x.Left);
                if (allDone || RaceTime > c.TimeLimit) Results(allDone ? "all finished" : "time limit");
            }

            if (Time.unscaledTime >= nextStatusLog && Phase != Phase.Lobby)
            {
                nextStatusLog = Time.unscaledTime + 5f;
                foreach (var r in Racers)
                {
                    if (r.Left || r.Aircraft == null) continue;
                    var g = c.Gates[Mathf.Min(r.Next, c.Gates.Count - 1)];
                    Plugin.Log.LogInfo($"[Race] t={t:F1} {r.Name} pos={Fmt(r.Last)} speed={r.Aircraft.speed:F0} " +
                                       $"radarAlt={r.Aircraft.radarAlt:F0} next={r.Next} ({g.Name}) dist={(g.P - r.Last).magnitude:F0}");
                }
            }
        }

        void Judge(Racer r, Vector3 a, Vector3 b, float t)
        {
            // Strict order with skips: passing a later gate counts the gates in between as missed.
            for (int k = r.Next; k < c.Gates.Count; k++)
            {
                var g = c.Gates[k];
                if (!Course.Crosses(g, a, b, out float f)) continue;
                if (k > r.Next && c.MissedPenalty < 0f) return;   // skipping not allowed: ignore the later gate
                float when = t - (1f - f) * Time.deltaTime;
                for (int m = r.Next; m < k; m++)
                {
                    r.Missed++;
                    r.Penalty += Mathf.Max(0f, c.MissedPenalty);
                    Plugin.Broadcast($"{r.Name} missed {c.Gates[m].Name} (+{c.MissedPenalty:F0} s). [race miss gate={m} who={r.Name}]");
                }
                r.Next = k + 1;
                Pass(r, g, when);
                return;
            }
        }

        void Pass(Racer r, Gate g, float when)
        {
            float rt = when - TGo;
            r.GateTimes[g.Index] = rt;
            Plugin.Log.LogInfo($"[Race] PASS {r.Name} gate {g.Index} '{g.Name}' at race time {rt:F2} s (mission {when:F2}), " +
                               $"penalty {r.Penalty:F0} s, aircraft {r.AircraftName}");
            if (g.Index == 0)
            {
                if (Phase == Phase.Grid || rt < -0.5f)
                {
                    r.Jumped = true;
                    r.Penalty += c.JumpPenalty;
                    Plugin.Broadcast($"{r.Name} jumped the start ({-rt:F1} s early, +{c.JumpPenalty:F0} s). [race jump who={r.Name}]");
                }
                return;
            }
            if (g.Index == c.Gates.Count - 1)
            {
                r.Finish = rt + r.Penalty;
                r.FinishSeq = ++finishSeq;
                // Equal times (to the hundredth) share a place.
                int place = 1 + Racers.Count(x => x != r && x.Finished && x.Finish < r.Finish - 0.005f);
                Plugin.Broadcast($"{r.Name} finished {Ordinal(place)}: {Course.Clock(r.Finish)}" +
                                 (r.Penalty > 0f ? $" (incl. +{r.Penalty:F0} s)" : "") +
                                 $" in the {r.AircraftName}. [race finish t={Inv(r.Finish)} pen={Inv(r.Penalty)} who={r.Name}]");
                return;
            }
            if (g.Split)
            {
                float best = Racers.Where(x => x != r && x.GateTimes.ContainsKey(g.Index)).Select(x => x.GateTimes[g.Index]).DefaultIfEmpty(float.NaN).Min();
                string delta = float.IsNaN(best) ? "leads" : (rt - best >= 0f ? $"+{rt - best:F1}" : $"{rt - best:F1}");
                Plugin.Broadcast($"{r.Name} {g.Name} {Course.Clock(rt)} ({delta}). [race split gate={g.Index} t={Inv(rt)} who={r.Name}]");
            }
        }

        void Results(string why)
        {
            Phase = Phase.Done;
            var sb = new StringBuilder($"Results {c.Name} ({why}):");
            int n = 0, place = 0;
            float prev = float.NaN;
            foreach (var r in Racers.Where(x => x.Finished).OrderBy(x => x.Finish).ThenBy(x => x.FinishSeq))
            {
                n++;
                if (float.IsNaN(prev) || r.Finish > prev + 0.005f) place = n;   // equal times share a place
                prev = r.Finish;
                sb.Append($" {place}. {r.Name} {Course.Clock(r.Finish)} {r.AircraftName}" + (r.Missed > 0 ? $" ({r.Missed} missed)" : "") + ";");
            }
            foreach (var r in Racers.Where(x => !x.Finished))
                sb.Append($" DNF {r.Name} (gate {r.Next}/{c.Gates.Count - 1}{(r.Left ? ", left" : "")});");
            sb.Append(" [race results]");
            Plugin.Broadcast(sb.ToString());
        }

        /// <summary>One chat line for a player who joined while the grid or race is on: the clock for their plugin.</summary>
        void Sync(string name)
        {
            string state = Phase == Phase.Grid ? $"grid open, GO in {TGo - Plugin.Now:F0} s" : $"race running, {Course.Clock(RaceTime)}";
            Plugin.Broadcast($"Welcome {name}: {c.Name} {state}. Spawn to join; '!race status' for the standings. " +
                             $"[race sync phase={(Phase == Phase.Grid ? "grid" : "racing")} go={Inv(TGo)} open={Inv(TOpen)} course={c.Id}]");
        }

        static string Ordinal(int n) => n == 1 ? "1st" : n == 2 ? "2nd" : n == 3 ? "3rd" : $"{n}th";
        // Tags are parsed on other machines: always a dot as decimal separator, whatever the host's locale.
        static string Inv(float f) => f.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        internal static string Fmt(Vector3 v) => $"({v.x:F0},{v.y:F0},{v.z:F0})";
    }
}
