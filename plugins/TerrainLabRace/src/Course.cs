using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TerrainLab.Race
{
    /// <summary>One gate of a course, in NO global metres (x east, y up, z north).</summary>
    internal class Gate
    {
        public int Index;
        public string Name;
        public Vector3 P;          // ring centre, global
        public Vector3 N;          // unit normal along the direction of travel (horizontal)
        public float HeadingDeg;
        public float Radius;
        public bool Split;
        public float Ground;       // ground or sea height under the ring (from the generator's check), m
        public float VmaxMs;       // turn speed limit at this gate from the generator (0 = none)
    }

    /// <summary>
    /// A race course written by src/terrainlab/race.py (format terrainlab.race.course/1).
    /// Coordinates in the file are already NO global metres.
    /// </summary>
    internal class Course
    {
        public string Id, Name, Variant, MapPrefab, File;
        public float GridHeading, GridSpeed, GridApproach, GoAfter;
        public readonly List<Vector3> Slots = new List<Vector3>();
        public readonly List<Gate> Gates = new List<Gate>();
        public float MissedPenalty = 10f, JumpPenalty = 5f, RespawnPenalty = 0f, TimeLimit = 1200f;
        public float LengthM;
        public float EtaJet;

        public Vector3 GridDir => Dir(GridHeading);

        public static Vector3 Dir(float headingDeg)
        {
            float h = headingDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(h), 0f, Mathf.Cos(h));
        }

        public static Course Load(string path)
        {
            var o = JObject.Parse(System.IO.File.ReadAllText(path));
            if ((string)o["format"] != "terrainlab.race.course/1")
                throw new Exception($"unknown course format '{o["format"]}'");
            var c = new Course
            {
                File = path,
                Id = (string)o["id"],
                Name = (string)o["name"],
                Variant = (string)o["variant"] ?? "",
                MapPrefab = (string)o["map_prefab"] ?? "",
            };
            var g = (JObject)o["grid"];
            c.GridHeading = (float)g["heading"];
            c.GridSpeed = (float)g["speed_ms"];
            c.GridApproach = (float)g["approach_m"];
            c.GoAfter = (float?)g["go_after_s"] ?? c.GridApproach / Mathf.Max(c.GridSpeed, 1f);
            foreach (JObject s in (JArray)g["slots"])
                c.Slots.Add(new Vector3((float)s["x"], (float)s["y"], (float)s["z"]));
            var check = o["check"] as JArray;
            int i = 0;
            foreach (JObject j in (JArray)o["gates"])
            {
                var gate = new Gate
                {
                    Index = i,
                    Name = (string)j["name"] ?? $"Gate {i}",
                    P = new Vector3((float)j["x"], (float)j["y"], (float)j["z"]),
                    HeadingDeg = (float)j["heading"],
                    Radius = (float)j["radius"],
                    Split = (bool?)j["split"] ?? false,
                };
                gate.N = Dir(gate.HeadingDeg);
                if (check != null && i < check.Count)
                {
                    gate.Ground = (float?)check[i]["ground"] ?? 0f;
                    var v = check[i]["vmax_ms"];
                    gate.VmaxMs = v == null || v.Type == JTokenType.Null ? 0f : (float)v;
                }
                c.Gates.Add(gate);
                i++;
            }
            if (c.Gates.Count < 2) throw new Exception("a course needs at least two gates");
            if (o["rules"] is JObject r)
            {
                c.MissedPenalty = (float?)r["missed_gate_penalty_s"] ?? c.MissedPenalty;
                c.JumpPenalty = (float?)r["jump_start_penalty_s"] ?? c.JumpPenalty;
                c.RespawnPenalty = (float?)r["respawn_penalty_s"] ?? c.RespawnPenalty;
                c.TimeLimit = (float?)r["time_limit_s"] ?? c.TimeLimit;
            }
            if (o["stats"] is JObject st)
            {
                c.LengthM = (float?)st["length_m"] ?? 0f;
                c.EtaJet = (float?)st["eta_s"]?["fast jet"] ?? 0f;
            }
            return c;
        }

        /// <summary>
        /// True when the segment a-b crosses the gate plane in the direction of travel inside the ring.
        /// </summary>
        public static bool Crosses(Gate g, Vector3 a, Vector3 b, out float frac)
        {
            frac = 0f;
            float da = Vector3.Dot(a - g.P, g.N);
            float db = Vector3.Dot(b - g.P, g.N);
            if (!(da < 0f && db >= 0f)) return false;
            frac = da / (da - db);
            Vector3 x = Vector3.Lerp(a, b, frac);
            return (x - g.P).magnitude <= g.Radius;
        }

        public static string Clock(float s)
        {
            if (float.IsNaN(s) || float.IsInfinity(s)) return "--:--.-";
            string sign = s < 0f ? "-" : "";
            s = Mathf.Abs(s);
            int m = (int)(s / 60f);
            return $"{sign}{m}:{s - 60f * m:00.0}";
        }

        /// <summary>Courses in the plugin's courses folder (any subfolder), by file.</summary>
        public static List<Course> LoadAll(string dir, BepInEx.Logging.ManualLogSource log)
        {
            var list = new List<Course>();
            if (!Directory.Exists(dir))
            {
                log.LogWarning($"[Race] no courses folder at {dir}");
                return list;
            }
            foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                try { list.Add(Load(f)); }
                catch (Exception e) { log.LogError($"[Race] cannot read course {f}: {e.Message}"); }
            }
            return list;
        }
    }
}
