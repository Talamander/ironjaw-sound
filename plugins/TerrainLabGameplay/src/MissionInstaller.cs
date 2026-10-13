using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

namespace TerrainLab.Gameplay
{
    /// <summary>
    /// Copies the release's missions (Missions/"Ironjaw Sound - *" next to this DLL) into the game's user mission
    /// folder, so a mod manager install gives players the missions without a manual copy.
    ///
    /// Rules, per mission folder:
    ///   - not there yet: copy it;
    ///   - every file equal to the shipped file: nothing to do;
    ///   - every differing file is one this installer wrote before (state file in BepInEx/config) or one shipped in an
    ///     earlier public release (Missions/known-old-hashes.txt): update it;
    ///   - otherwise the player edited the mission: leave the whole folder alone and log how to get the new one.
    /// It never deletes anything and only touches folders whose name starts with "Ironjaw Sound - ".
    /// Dedicated servers that set their own mission directory get a second pass when the game switches to it.
    /// </summary>
    static class MissionInstaller
    {
        const string Prefix = "Ironjaw Sound - ";
        static string pluginDir;
        static readonly HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string StatePath => Path.Combine(Paths.ConfigPath, "talamander.terrainlab.missions.state");

        internal static void Install(Harmony harmony)
        {
            pluginDir = Path.GetDirectoryName(typeof(MissionInstaller).Assembly.Location);
            string target = MissionGroup.UserGroup.UserMissionDirectory;
            if (string.IsNullOrEmpty(target)) target = Path.Combine(Application.persistentDataPath, "Missions");
            Run(target);
            var setDir = AccessTools.Method(typeof(MissionGroup.UserGroup), nameof(MissionGroup.UserGroup.SetDirectory));
            if (setDir != null)
                harmony.Patch(setDir, postfix: new HarmonyMethod(typeof(MissionInstaller), nameof(SetDirectoryPostfix)));
        }

        static void SetDirectoryPostfix(string path)
        {
            try { Run(path); }
            catch (Exception e) { Plugin.Log.LogWarning($"[TLGameplay] mission install into {path} failed: {e.Message}"); }
        }

        static void Run(string target)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(pluginDir)) return;
            target = Path.GetFullPath(target);
            if (!done.Add(target)) return;
            string source = Path.Combine(pluginDir, "Missions");
            if (!Directory.Exists(source)) return;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string knownFile = Path.Combine(source, "known-old-hashes.txt");
            if (File.Exists(knownFile))
                foreach (var line in File.ReadAllLines(knownFile))
                {
                    string h = line.Split('#')[0].Trim();
                    if (h.Length == 64) known.Add(h);
                }
            var state = LoadState();
            foreach (var h in state.Values) known.Add(h);

            int installed = 0, updated = 0, same = 0, kept = 0;
            foreach (var src in Directory.GetDirectories(source).OrderBy(d => d, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(src);
                if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                string dst = Path.Combine(target, name);
                var files = Directory.GetFiles(src).Select(Path.GetFileName).ToList();

                bool exists = Directory.Exists(dst);
                bool allSame = exists, userEdited = false;
                foreach (var f in files)
                {
                    string d = Path.Combine(dst, f);
                    if (!File.Exists(d)) { allSame = false; continue; }
                    string have = Sha256(d), want = Sha256(Path.Combine(src, f));
                    if (have == want) continue;
                    allSame = false;
                    if (!known.Contains(have)) userEdited = true;
                }
                if (allSame) { same++; continue; }
                if (userEdited)
                {
                    kept++;
                    Plugin.Log.LogWarning($"[TLGameplay] kept your edited mission '{name}' in {target}. " +
                                          "Rename or delete that folder and restart the game to get this release's version.");
                    continue;
                }
                Directory.CreateDirectory(dst);
                foreach (var f in files)
                {
                    string s = Path.Combine(src, f), d = Path.Combine(dst, f);
                    File.Copy(s, d, true);
                    state[name + "/" + f] = Sha256(d);
                }
                if (exists) updated++; else installed++;
            }
            SaveState(state);
            Plugin.Log.LogInfo($"[TLGameplay] missions in {target}: {installed} installed, {updated} updated, " +
                               $"{same} already current, {kept} kept (edited by you).");
        }

        static Dictionary<string, string> LoadState()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(StatePath))
                    foreach (var line in File.ReadAllLines(StatePath))
                    {
                        int i = line.IndexOf('\t');
                        if (i == 64) d[line.Substring(65)] = line.Substring(0, 64);
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[TLGameplay] mission state file unreadable: {e.Message}"); }
            return d;
        }

        static void SaveState(Dictionary<string, string> state)
        {
            try
            {
                var lines = new List<string> { "# Missions the Ironjaw Sound release installed: sha256<TAB>mission/file. Safe to delete." };
                lines.AddRange(state.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value + "\t" + kv.Key));
                File.WriteAllLines(StatePath, lines);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[TLGameplay] could not write {StatePath}: {e.Message}"); }
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
