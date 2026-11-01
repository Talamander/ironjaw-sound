using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab.Race
{
    /// <summary>
    /// Mission probe only, with TERRAINLAB_MISSION_PROBE_TARGETS=1: logs what an AI aircraft's HQ target search picks
    /// ("TARGET searcher -> target"), once per change of target, plus how many enemy units the searcher's faction
    /// tracks and which of them match TERRAINLAB_MISSION_PROBE_TARGETS_WATCH (a UniqueName prefix, for example
    /// "fenwick-farm"). It answers "why did the strikers bomb that and not the farm". Patched at runtime from
    /// MissionProbe (never with PatchAll), a postfix on CombatAI.ChooseHQTarget.
    /// </summary>
    internal static class TargetDiag
    {
        static bool patched;
        static string watch = "";
        static readonly Dictionary<int, string> last = new Dictionary<int, string>();

        internal static void Patch()
        {
            if (patched || Environment.GetEnvironmentVariable("TERRAINLAB_MISSION_PROBE_TARGETS") != "1") return;
            patched = true;
            watch = Environment.GetEnvironmentVariable("TERRAINLAB_MISSION_PROBE_TARGETS_WATCH") ?? "";
            var m = AccessTools.Method(typeof(CombatAI), nameof(CombatAI.ChooseHQTarget));
            if (m == null) { MissionProbe.NoteStatic("target diag: CombatAI.ChooseHQTarget not found"); return; }
            new Harmony("talamander.terrainlab.race.targetdiag").Patch(m, postfix: new HarmonyMethod(typeof(TargetDiag), nameof(Post)));
            MissionProbe.NoteStatic($"target diag on (watch '{watch}')");
        }

        static string Fac(FactionHQ hq) => hq != null && hq.faction != null ? hq.faction.factionName : "None";

        static string Label(Unit u) => u == null ? "none" :
            (string.IsNullOrEmpty(u.UniqueName) ? u.unitName : u.UniqueName) + " [" + (u.definition != null ? u.definition.jsonKey : "?") + "]";

        static void Post(Unit searcher, CombatAI.TargetSearchResults __result)
        {
            try
            {
                if (searcher == null || !(searcher is Aircraft)) return;
                var t = __result.target;
                string key = Label(t) + (__result.chosenWeaponStation != null ? " with " + __result.chosenWeaponStation.WeaponInfo.weaponName : "");
                int id = searcher.GetInstanceID();
                if (last.TryGetValue(id, out var old) && old == key) return;
                last[id] = key;
                var hq = searcher.NetworkHQ;
                int enemies = 0;
                var watched = new List<string>();
                if (hq != null)
                    foreach (var kv in hq.trackingDatabase)
                    {
                        if (!kv.Value.TryGetUnit(out var u) || u == null || u.NetworkHQ == null || u.NetworkHQ == hq) continue;
                        enemies++;
                        if (watch.Length > 0 && (u.UniqueName ?? "").StartsWith(watch, StringComparison.Ordinal))
                            watched.Add(u.UniqueName);
                    }
                Vector3 p = searcher.transform.position;
                string range = t != null ? (Vector3.Distance(t.transform.position, p) / 1000f).ToString("F1", CultureInfo.InvariantCulture) + " km" : "-";
                MissionProbe.NoteStatic($"TARGET {Fac(hq)} {Label(searcher)} -> {key} at {range}, opp " +
                                        $"{__result.opportunity.ToString("F3", CultureInfo.InvariantCulture)}, out of ammo {__result.outOfAmmo}; " +
                                        $"tracks {enemies} enemy units" + (watch.Length > 0 ? $", {watched.Count} '{watch}': {string.Join(",", watched.OrderBy(s => s))}" : ""));
            }
            catch (Exception e) { MissionProbe.NoteStatic("target diag failed: " + e.Message); }
        }
    }
}
