using System;
using System.Linq;
using HarmonyLib;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Outcomes;

namespace TerrainLab.Race
{
    /// <summary>
    /// Mission probe only: logs every mission outcome as it fires ("OUTCOME name type by objective"), with the text
    /// of ShowMessage outcomes, so a probe report shows which objectives completed and which messages a player saw.
    /// Patched at runtime from MissionProbe (never with PatchAll), one postfix per concrete Outcome.Complete.
    /// </summary>
    internal static class OutcomeDiag
    {
        static bool patched;

        internal static void Patch()
        {
            if (patched) return;
            patched = true;
            var h = new Harmony("talamander.terrainlab.race.outcomediag");
            var post = new HarmonyMethod(typeof(OutcomeDiag), nameof(Post));
            int n = 0;
            foreach (var t in typeof(Outcome).Assembly.GetTypes().Where(t => typeof(Outcome).IsAssignableFrom(t) && !t.IsAbstract))
            {
                var m = AccessTools.DeclaredMethod(t, "Complete", new[] { typeof(Objective) });
                if (m == null) continue;
                try { h.Patch(m, postfix: post); n++; }
                catch (Exception e) { MissionProbe.NoteStatic($"outcome diag: could not patch {t.Name}: {e.Message}"); }
            }
            MissionProbe.NoteStatic($"outcome diag on {n} outcome types");
        }

        static void Post(Outcome __instance, Objective completedObjective)
        {
            try
            {
                var s = __instance.SavedOutcome;
                string by = completedObjective?.SavedObjective?.UniqueName ?? "?";
                string extra = s is ShowMessageSavedOutcome sm ? $" message \"{sm.Message}\"" :
                               s is GiveScoreSavedOutcome gs ? $" rank {gs.rankType} {gs.rank}" :
                               s is EndGameSavedOutcome eg ? $" end {eg.endType}" : "";
                MissionProbe.NoteStatic($"OUTCOME '{s.UniqueName}' {s.OutcomeTypeEnum} by '{by}'{extra}");
            }
            catch { }
        }
    }
}
