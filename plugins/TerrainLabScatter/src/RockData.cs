using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TerrainLab.Scatter
{
    /// <summary>One rock from rocks_&lt;variant&gt;.bin (format: src/terrainlab/rocks.py, docs/SCATTER.md).</summary>
    struct Rock
    {
        public Vector3 Pos;      // map space, y = master ground
        public float Yaw, Scale, Sink;
        public byte Type, Variant, Flags, Class;
        public sbyte TiltX, TiltZ;
        public ushort Tile;
        public bool Solid => (Flags & 1) != 0;
    }

    class RockData
    {
        public string Variant = "", VariantSafe = "", Git = "", Created = "";
        public string[] Types = new string[0];
        public string[] Classes = new string[0];
        public float[][] LodM;          // per type: LOD0 end, LOD1 end, cull distance (m)
        public int TilesPerSide = 16;
        public float TileM = 5120f;
        public Rock[] Rocks = new Rock[0];

        public static RockData Load(string path)
        {
            var d = new RockData();
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                var magic = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (magic != "TLRK") throw new InvalidDataException($"{path}: not a rocks file (magic '{magic}')");
                uint version = br.ReadUInt32();
                if (version != 1) throw new InvalidDataException($"{path}: format version {version}, plugin reads 1");
                int hl = (int)br.ReadUInt32();
                string header = Encoding.UTF8.GetString(br.ReadBytes(hl));
                d.ParseHeader(header);
                int n = (int)br.ReadUInt32();
                d.Rocks = new Rock[n];
                for (int i = 0; i < n; i++)
                {
                    Rock r;
                    r.Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    r.Yaw = br.ReadSingle();
                    r.Scale = br.ReadSingle();
                    r.Sink = br.ReadSingle();
                    r.Type = br.ReadByte();
                    r.Variant = br.ReadByte();
                    r.Flags = br.ReadByte();
                    r.Class = br.ReadByte();
                    r.TiltX = br.ReadSByte();
                    r.TiltZ = br.ReadSByte();
                    r.Tile = br.ReadUInt16();
                    d.Rocks[i] = r;
                }
            }
            return d;
        }

        // The header is small, flat JSON written by rocks.py; read the few fields we need without a JSON library.
        void ParseHeader(string h)
        {
            Variant = Str(h, "variant");
            VariantSafe = Str(h, "variant_safe");
            Git = Str(h, "git");
            Created = Str(h, "created");
            Types = StrList(h, "types");
            Classes = StrList(h, "classes");
            var tps = Regex.Match(h, "\"tiles_per_side\":(\\d+)");
            if (tps.Success) TilesPerSide = int.Parse(tps.Groups[1].Value);
            var tm = Regex.Match(h, "\"tile_m\":([0-9.]+)");
            if (tm.Success) TileM = float.Parse(tm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            LodM = new float[Types.Length][];
            for (int t = 0; t < Types.Length; t++)
            {
                var m = Regex.Match(h, "\"" + Regex.Escape(Types[t]) + "\":\\[([0-9.]+),([0-9.]+),([0-9.]+)\\]");
                LodM[t] = m.Success
                    ? new[] { F(m.Groups[1].Value), F(m.Groups[2].Value), F(m.Groups[3].Value) }
                    : new[] { 150f, 600f, 1500f };
                if (!m.Success) Plugin.Log.LogWarning($"no LOD distances for '{Types[t]}' in the data header; using 150/600/1500 m");
            }
        }

        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        static string Str(string h, string key)
        {
            var m = Regex.Match(h, "\"" + key + "\":\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        static string[] StrList(string h, string key)
        {
            var m = Regex.Match(h, "\"" + key + "\":\\[([^\\]]*)\\]");
            if (!m.Success) return new string[0];
            var list = new List<string>();
            foreach (Match s in Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\"")) list.Add(s.Groups[1].Value);
            return list.ToArray();
        }
    }
}
