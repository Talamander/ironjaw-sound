using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mirage;
using NuclearOption.Chat;
using NuclearOption.Networking;
using UnityEngine;
using NOPlayer = NuclearOption.Networking.Player;

namespace TerrainLab.Race
{
    internal static class Patches
    {
        /// <summary>
        /// Air start: every player spawn on the host goes through Spawner.SpawnAircraft. During a race the prefix
        /// replaces the hangar position with a grid slot (or the last gate passed), sets the starting velocity and
        /// clears the hangar, so clients place the new aircraft at startPosition with startingVelocity, exactly
        /// like a stock mission air start. The aircraft type, loadout and livery stay the player's choice.
        /// </summary>
        [HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnAircraft))]
        internal static class SpawnAircraftPatch
        {
            static void Prefix(NOPlayer player, GameObject prefab, ref GlobalPosition globalPosition, ref Quaternion rotation,
                               ref Vector3 startingVel, ref Hangar spawningHangar, FactionHQ HQ)
            {
                try
                {
                    var host = Plugin.Host;
                    if (host == null || player == null || HQ == null || !Plugin.Enabled.Value) return;
                    if (!host.PlaceSpawn(player, prefab, out var pos, out var rot, out var speed, out var why)) return;
                    var from = globalPosition.AsVector3();
                    globalPosition = new GlobalPosition(pos.x, pos.y, pos.z);
                    rotation = rot;
                    startingVel = rot * Vector3.forward * speed;
                    spawningHangar = null;
                    Plugin.Log.LogInfo($"[Race] AIR START {SafeName(player)} {(prefab != null ? prefab.name : "?")}: {why}; " +
                                       $"{RaceHost.Fmt(from)} -> {RaceHost.Fmt(pos)} heading {rot.eulerAngles.y:F0} speed {speed:F0} m/s");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Race] air start failed, stock spawn used: {e}");
                }
            }
        }

        static string SafeName(NOPlayer p)
        {
            try { return p.GetPlayerName()?.SanitizedName ?? "player"; } catch { return "player"; }
        }

        /// <summary>Host chat commands ("!race grid|go|reset|status") and client parsing of the race tags.</summary>
        public static void PatchChat(Harmony h)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var cmd = typeof(ChatManager).GetMethods(all).FirstOrDefault(m => m.Name.StartsWith("UserCode_CmdSendChatMessage", StringComparison.Ordinal));
            var rpc = typeof(ChatManager).GetMethods(all).FirstOrDefault(m => m.Name.StartsWith("UserCode_RpcServerMessage", StringComparison.Ordinal));
            if (cmd != null) h.Patch(cmd, postfix: new HarmonyMethod(typeof(Patches), nameof(ChatCommandPostfix)));
            else Plugin.Log.LogWarning("[Race] chat command hook not found; use the host keys");
            if (rpc != null) h.Patch(rpc, prefix: new HarmonyMethod(typeof(Patches), nameof(ServerMessagePrefix)));
            else Plugin.Log.LogWarning("[Race] server message hook not found; clients will not see the race clock");
        }

        static void ChatCommandPostfix(object[] __args)
        {
            try
            {
                if (Plugin.Host == null || __args == null || __args.Length < 3) return;
                string msg = (__args[0] as string ?? "").Trim();
                if (!msg.StartsWith("!race", StringComparison.OrdinalIgnoreCase)) return;
                var sender = __args[2] as INetworkPlayer;
                if (sender == null || !sender.TryGetPlayer<NOPlayer>(out var p)) return;
                string sub = msg.Length > 5 ? msg.Substring(5).Trim().ToLowerInvariant() : "status";
                if (sub == "") sub = "status";
                // The host's own player (listen server). A dedicated server has no host player, so any player may
                // run the race there unless the host turned that off.
                bool dedicated = false;
                try { dedicated = NuclearOption.DedicatedServer.DedicatedServerManager.IsRunning; } catch { }
                bool isHost = p.IsLocalPlayer || p.IsHostPlayer || (dedicated && Plugin.DedicatedAnyoneCommands.Value);
                if (sub != "status" && !isHost)
                {
                    Plugin.Broadcast($"Only the host can '{sub}' the race.");
                    return;
                }
                Plugin.Host.Command(sub, SafeName(p));
            }
            catch (Exception e) { Plugin.Log.LogError($"[Race] chat command failed: {e}"); }
        }

        static void ServerMessagePrefix(ref string message)
        {
            try
            {
                if (message == null || Plugin.View == null) return;
                // Read the tag, then hide it: players with the plugin see only the plain text.
                message = Plugin.View.OnServerMessage(message);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Race] server message parse failed: {e}"); }
        }

        /// <summary>Test autopilot (TERRAINLAB_RACE_TEST only): replaces the player's stick and throttle.</summary>
        [HarmonyPatch(typeof(PilotPlayerState), nameof(PilotPlayerState.FixedUpdateState))]
        internal static class PlayerFixedPatch
        {
            static bool Prefix(Pilot pilot)
            {
                var d = TestDriver.I;
                if (d == null || !d.AutopilotOn || pilot == null || pilot.aircraft == null) return true;
                try { return !d.Fly(pilot.aircraft); }
                catch (Exception e) { Plugin.Log.LogError($"[Race] test autopilot failed: {e}"); d.AutopilotOn = false; return true; }
            }
        }

        [HarmonyPatch(typeof(PilotPlayerState), nameof(PilotPlayerState.UpdateState))]
        internal static class PlayerUpdatePatch
        {
            static bool Prefix(Pilot pilot)
            {
                var d = TestDriver.I;
                return d == null || !d.AutopilotOn || pilot == null || pilot.aircraft == null || pilot.aircraft != d.Flying;
            }
        }
    }
}
