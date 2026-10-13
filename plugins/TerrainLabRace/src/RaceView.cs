using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TerrainLab.Race
{
    /// <summary>
    /// What one machine shows: the gate rings in the world and a timer box for the local aircraft.
    /// It tracks the local aircraft's gates on its own, so the timer runs on clients too. The race clock comes from
    /// the host's chat tags ("[race grid go=...]"); with no host race running it times a practice run from the
    /// start gate. The host's chat messages stay the official result.
    /// </summary>
    internal class RaceView
    {
        readonly Course c;
        GameObject root;
        readonly List<LineRenderer> rings = new List<LineRenderer>();
        Material mat;

        public float GoTime = float.NaN;    // mission time of GO from the host, NaN if unknown
        public bool Practice = true;
        public int Next;
        public float Penalty;
        public int Missed;
        public float Finish = -1f;
        public float PracticeStart = float.NaN;
        public string LastEvent = "";
        public float LastEventAt = -100f;
        public string LastServer = "";
        Aircraft tracked;
        Vector3 last;
        bool hasLast;

        public event Action<int, string> GatePassed;   // gate index, text (used by the test driver)

        public RaceView(Course course) { c = course; }

        static readonly Color NextColor = new Color(1f, 0.55f, 0.05f, 1f);
        static readonly Color AfterColor = new Color(1f, 0.95f, 0.6f, 0.85f);
        static readonly Color LaterColor = new Color(0.55f, 0.85f, 1f, 0.45f);
        static readonly Color FinishColor = new Color(0.2f, 1f, 0.35f, 0.9f);

        public void Destroy()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            rings.Clear();
        }

        void Build()
        {
            if (Datum.origin == null) return;
            root = new GameObject("TerrainLabRace_Gates");
            root.transform.SetParent(Datum.origin, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("UI/Default");
            if (sh == null) Plugin.Log.LogWarning("[Race] no line shader found; gates will not draw");
            mat = sh != null ? new Material(sh) { name = "TerrainLabRace_Ring" } : null;
            const int seg = 64;
            foreach (var g in c.Gates)
            {
                var go = new GameObject($"Gate {g.Index} {g.Name}");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = g.P;   // children of Datum.origin: local position = global position
                go.transform.localRotation = Quaternion.LookRotation(g.N, Vector3.up);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.loop = true;
                lr.positionCount = seg;
                for (int i = 0; i < seg; i++)
                {
                    float a = i * Mathf.PI * 2f / seg;
                    lr.SetPosition(i, new Vector3(Mathf.Cos(a) * g.Radius, Mathf.Sin(a) * g.Radius, 0f));
                }
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.numCornerVertices = 2;
                lr.alignment = LineAlignment.View;
                if (mat != null) lr.sharedMaterial = mat;
                rings.Add(lr);
            }
            Plugin.Log.LogInfo($"[Race] drew {rings.Count} gate rings under Datum.origin with shader '{(sh != null ? sh.name : "none")}'");
        }

        public void Tick()
        {
            if (Plugin.ShowGates.Value && root == null) Build();
            if (root != null && root.activeSelf != Plugin.ShowGates.Value) root.SetActive(Plugin.ShowGates.Value);

            // Host: take the clock from the race itself.
            var host = Plugin.Host;
            if (host != null)
            {
                Practice = host.Phase == Phase.Lobby;
                GoTime = host.Phase == Phase.Lobby ? float.NaN : host.TGo;
            }

            TrackLocal();
            UpdateRings();
        }

        void TrackLocal()
        {
            if (!GameManager.GetLocalAircraft(out Aircraft a) || a == null || a.disabled)
            {
                hasLast = false;
                return;
            }
            if (a != tracked)
            {
                tracked = a;
                hasLast = false;
            }
            Vector3 p = a.GlobalPosition().AsVector3();
            if (hasLast && Finish < 0f)
            {
                for (int k = Next; k < c.Gates.Count; k++)
                {
                    var g = c.Gates[k];
                    if (!Course.Crosses(g, last, p, out _)) continue;
                    if (k > Next && c.MissedPenalty < 0f) break;
                    for (int m = Next; m < k; m++) { Missed++; Penalty += Mathf.Max(0f, c.MissedPenalty); }
                    Next = k + 1;
                    OnPass(g);
                    break;
                }
            }
            last = p;
            hasLast = true;
        }

        void OnPass(Gate g)
        {
            float now = Plugin.Now;
            if (g.Index == 0 && float.IsNaN(GoTime)) PracticeStart = now;
            float rt = RaceClock(now);
            string text;
            if (g.Index == c.Gates.Count - 1)
            {
                Finish = rt + Penalty;
                text = $"FINISH {Course.Clock(Finish)}" + (Penalty > 0f ? $" (incl. +{Penalty:F0} s)" : "");
            }
            else if (g.Index == 0)
            {
                bool jumped = rt < -0.5f;
                if (jumped) Penalty += c.JumpPenalty;
                text = jumped ? $"Jump start ({-rt:F1} s early, +{c.JumpPenalty:F0} s)" : "Start";
            }
            else
                text = $"{g.Name} {Course.Clock(rt)}  ({g.Index}/{c.Gates.Count - 1})";
            LastEvent = text;
            LastEventAt = Time.unscaledTime;
            Plugin.Log.LogInfo($"[Race] local pass gate {g.Index} '{g.Name}': {text}");
            try { SceneSingleton<AircraftActionsReport>.i?.ReportText(text, 3f); } catch { }
            GatePassed?.Invoke(g.Index, text);
        }

        float RaceClock(float now)
        {
            if (!float.IsNaN(GoTime)) return now - GoTime;
            if (!float.IsNaN(PracticeStart)) return now - PracticeStart;
            return float.NaN;
        }

        void UpdateRings()
        {
            if (root == null || !root.activeSelf) return;
            var cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            for (int i = 0; i < rings.Count; i++)
            {
                var lr = rings[i];
                bool passed = i < Next;
                if (lr.gameObject.activeSelf == passed) lr.gameObject.SetActive(!passed);
                if (passed) continue;
                Color col = i == Next ? NextColor : i == Next + 1 ? AfterColor : LaterColor;
                if (i == c.Gates.Count - 1 && i != Next) col = FinishColor;
                lr.startColor = lr.endColor = col;
                float d = cam != null ? Vector3.Distance(camPos, lr.transform.position) : 1000f;
                float w = Mathf.Clamp(d * (i == Next ? 0.006f : 0.004f), 2.5f, 80f);
                lr.widthMultiplier = w;
            }
        }

        static readonly Regex TagRe = new Regex(@"\[race (\w+)([^\]]*)\]\s*$");
        static readonly Regex KvRe = new Regex(@"(\w+)=([^\s\]]+)");

        /// <summary>Server chat message seen on this machine (host or client). Returns the text without the tag.</summary>
        public string OnServerMessage(string msg)
        {
            var m = TagRe.Match(msg ?? "");
            if (!m.Success) return msg;
            string kind = m.Groups[1].Value;
            string args = m.Groups[2].Value;
            // "who=" is always the last field and may contain spaces (player names).
            string who = null;
            int wi = args.IndexOf(" who=", StringComparison.Ordinal);
            if (wi >= 0) { who = args.Substring(wi + 5).Trim(); args = args.Substring(0, wi); }
            var kv = new Dictionary<string, string>();
            foreach (Match x in KvRe.Matches(args)) kv[x.Groups[1].Value] = x.Groups[2].Value;
            LastServer = msg.Substring(0, m.Index).Trim();
            bool me = who != null && who == LocalName();
            switch (kind)
            {
                case "grid":
                    ResetLocal();
                    Practice = false;
                    if (TryF(kv, "go", out var go)) GoTime = go;
                    break;
                case "go":
                    Practice = false;
                    if (TryF(kv, "go", out var go2)) GoTime = go2;
                    break;
                case "sync":
                    // Joined while a race runs: take the host's clock. Local gate progress starts when this player
                    // spawns (the host puts them on the grid or at their last gate and says so).
                    Practice = false;
                    if (TryF(kv, "go", out var go3)) GoTime = go3;
                    break;
                case "reset":
                    ResetLocal();
                    Practice = true;
                    GoTime = float.NaN;
                    break;
                case "respawn":
                    // The host's count is official: take its next gate and penalty for this player.
                    if (me && TryF(kv, "gate", out var ng))
                    {
                        Next = Mathf.Clamp((int)ng, 0, c.Gates.Count - 1);
                        if (TryF(kv, "pen", out var pen)) Penalty = pen;
                        if (TryF(kv, "missed", out var mi)) Missed = (int)mi;
                        hasLast = false;
                    }
                    break;
                case "finish":
                    if (me && TryF(kv, "t", out var ft)) { Finish = ft; Next = c.Gates.Count; }
                    break;
            }
            return LastServer;
        }

        static bool TryF(Dictionary<string, string> kv, string key, out float v)
        {
            v = 0f;
            return kv.TryGetValue(key, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        static string LocalName()
        {
            try
            {
                if (GameManager.GetLocalPlayer<NuclearOption.Networking.Player>(out var p) && p != null)
                    return p.GetPlayerName()?.SanitizedName;
            }
            catch { }
            return null;
        }

        /// <summary>Speed that brings an aircraft at pos to the start line in timeToGo seconds.</summary>
        public static float PaceSpeed(Course c, Vector3 pos, float timeToGo)
        {
            float d = Vector3.Dot(c.Gates[0].P - pos, c.Gates[0].N);
            return Mathf.Max(0f, d) / Mathf.Max(timeToGo, 0.5f);
        }

        void ResetLocal()
        {
            Next = 0; Penalty = 0f; Missed = 0; Finish = -1f; PracticeStart = float.NaN; hasLast = false;
        }

        // ------------------------------------------------------------------ HUD

        GUIStyle box, big;

        public void OnGUI()
        {
            if (box == null)
            {
                box = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.UpperLeft, richText = true };
                box.normal.textColor = Color.white;
                big = new GUIStyle(box) { fontSize = 30, alignment = TextAnchor.MiddleCenter };
            }
            float now = Plugin.Now;
            float rt = RaceClock(now);
            string clock;
            if (Finish >= 0f) clock = $"FINISHED {Course.Clock(Finish)}";
            else if (!float.IsNaN(GoTime) && rt < 0f) clock = $"GO in {-rt:F0} s";
            else if (!float.IsNaN(rt)) clock = Course.Clock(rt + Penalty);
            else clock = Practice ? "practice: fly through Start" : "--:--.-";

            string next = "";
            if (Finish < 0f && Next < c.Gates.Count)
            {
                var g = c.Gates[Next];
                string dist = "";
                if (GameManager.GetLocalAircraft(out Aircraft a) && a != null)
                {
                    float d = Vector3.Distance(a.GlobalPosition().AsVector3(), g.P);
                    dist = d >= 1000f ? $"{d / 1000f:F1} km" : $"{d:F0} m";
                }
                next = $"Next: {g.Name} ({Next}/{c.Gates.Count - 1}) {dist}";
            }
            if (Next == 0 && !float.IsNaN(GoTime) && rt < 0f && GameManager.GetLocalAircraft(out Aircraft pa) && pa != null)
            {
                // Rolling start: the speed that reaches the start line exactly at GO.
                float pace = PaceSpeed(c, pa.GlobalPosition().AsVector3(), -rt);
                next += $"\nPace {pace:F0} m/s to the line (now {pa.speed:F0})";
            }
            string pen = Penalty > 0f ? $"   +{Penalty:F0} s ({Missed} missed)" : "";
            string ev = Time.unscaledTime - LastEventAt < 6f ? $"\n{LastEvent}" : "";
            string text = $"<b>{c.Name}</b>   {clock}{pen}\n{next}{ev}";
            float w = 460f, h = 12f + 24f * text.Split('\n').Length;
            GUI.Box(new Rect((Screen.width - w) * 0.5f, 8f, w, h), text, box);
            if (!float.IsNaN(GoTime) && rt < 0f && rt > -5.5f)
                GUI.Box(new Rect((Screen.width - 200f) * 0.5f, Screen.height * 0.3f, 200f, 60f), $"{Mathf.CeilToInt(-rt)}", big);
            else if (!float.IsNaN(GoTime) && rt >= 0f && rt < 2f)
                GUI.Box(new Rect((Screen.width - 200f) * 0.5f, Screen.height * 0.3f, 200f, 60f), "GO", big);
        }
    }
}
