using System;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Patch 9: nuclear threshold music (GitHub issue #1, "Game breaks at Strategic Nuclear Threshold").
    ///
    /// When the escalation score crosses the tactical or strategic threshold, FactionHQ.CheckEscalation sends
    /// MessageManager.RpcAllHQMessage. Its handler queues the map's threshold music:
    ///   MusicManager.i.QueueMusicClip(LoadedMapSettings.GetTacticalMusic(faction), 2f)
    /// Our map bundle has an empty MapSettings.factionMusic (NOMapLoader maps cannot set it without Blueprinter), so
    /// GetTacticalMusic / GetStrategicMusic return null, and QueueMusicClip logs audioClip.name without a null check:
    /// NullReferenceException. On the host the RPC runs inline, so the exception climbs back through
    /// FactionHQ.AddScore, Unit.ReportKilled and Aircraft.ServerDisableUnit (seen in the 1.1.1 Escalation probe log)
    /// and aborts the kill or landing that raised the score. When that is the player's aircraft, the player is left
    /// half in it: no airbase choice, respawn at the last base, the old HUD over the new one. Stock maps have the
    /// music, so Heartland never hits it.
    ///
    /// Fix, without touching the map bundle (multiplayer compatibility stays):
    ///   - on our map, a null threshold clip falls back to the same clip from a stock map (Heartland) when its
    ///     MapSettings is loaded, so players still hear the cue;
    ///   - QueueMusicClip skips a null clip instead of throwing, on any map. Without music nothing else happens there.
    /// </summary>
    static class NuclearMusicFix
    {
        static int fallbackLogged, skipLogged;

        internal static AudioClip Fallback(MapSettings ours, Faction faction, bool strategic)
        {
            if (ours == null || ours != MapPatcher.Current) return null;
            try
            {
                foreach (var ms in Resources.FindObjectsOfTypeAll<MapSettings>())
                {
                    if (ms == null || ms == ours || MapPatcher.PrefixOf(ms) == Plugin.MapPrefix.Value) continue;
                    var clip = strategic ? ms.GetStrategicMusic(faction) : ms.GetTacticalMusic(faction);
                    if (clip == null) continue;
                    if (fallbackLogged++ < 4)
                        Plugin.Log.LogInfo($"[9 nuclear music] our map has no {(strategic ? "strategic" : "tactical")} music; " +
                                           $"using '{clip.name}' from '{ms.name}'.");
                    return clip;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[9 nuclear music] stock music lookup failed: {e.Message}"); }
            return null;
        }

        internal static bool SkipNull(AudioClip clip)
        {
            if (clip != null) return false;
            if (skipLogged++ < 4)
                Plugin.Log.LogInfo("[9 nuclear music] no threshold music for this map; skipped it instead of throwing.");
            return true;
        }
    }

    [HarmonyPatch(typeof(MapSettings), nameof(MapSettings.GetTacticalMusic))]
    static class TacticalMusicPatch
    {
        static void Postfix(MapSettings __instance, Faction faction, ref AudioClip __result)
        {
            if (__result == null && Plugin.NuclearMusicEnabled.Value)
                __result = NuclearMusicFix.Fallback(__instance, faction, strategic: false);
        }
    }

    [HarmonyPatch(typeof(MapSettings), nameof(MapSettings.GetStrategicMusic))]
    static class StrategicMusicPatch
    {
        static void Postfix(MapSettings __instance, Faction faction, ref AudioClip __result)
        {
            if (__result == null && Plugin.NuclearMusicEnabled.Value)
                __result = NuclearMusicFix.Fallback(__instance, faction, strategic: true);
        }
    }

    /// The guard that stops the crash. Not behind the config switch: a null clip always throws in the stock method.
    [HarmonyPatch(typeof(MusicManager), nameof(MusicManager.QueueMusicClip))]
    static class QueueMusicClipPatch
    {
        static bool Prefix(AudioClip audioClip) => !NuclearMusicFix.SkipNull(audioClip);
    }
}
