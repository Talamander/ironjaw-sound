using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab.Race
{
    /// <summary>
    /// Mission probe only (TERRAINLAB_MISSION_PROBE=1): log why AI pilots give up while taxiing or starting the
    /// take-off roll. AIPilotTaxiState sets its private 'disembarking' flag from three places (the 5 s roll check
    /// EjectCheck, any damage, and the stuck timers in IsStuck), then brakes and calls StartEjectionSequence once
    /// stopped; AIPilotTakeoffState ejects on damage or after 12 s below 1 m/s. These patches only read state and
    /// write log lines; they change no game behaviour.
    /// </summary>
    internal static class TaxiDiag
    {
        static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> Disembarking =
            AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("disembarking");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, float> StuckSpeed =
            AccessTools.FieldRefAccess<AIPilotTaxiState, float>("stuckTimerSpeed");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, float> StuckYaw =
            AccessTools.FieldRefAccess<AIPilotTaxiState, float>("stuckTimerYaw");
        static readonly HashSet<int> logged = new HashSet<int>();

        static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);

        static string Describe(Aircraft ac)
        {
            if (ac == null) return "(no aircraft)";
            var p = ac.GlobalPosition().AsVector3();
            string fac = ac.NetworkHQ != null && ac.NetworkHQ.faction != null ? ac.NetworkHQ.faction.factionName : "None";
            float roll = 0f, pitch = 0f;
            try
            {
                var x = ac.cockpit != null ? ac.cockpit.xform : ac.transform;
                roll = Mathf.Asin(Mathf.Clamp(Vector3.Dot(x.right, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg;
                pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(x.forward, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg;
            }
            catch { }
            string ground = "?";
            try
            {
                // First collider below the aircraft that is not part of it (the airfield collider or the terrain).
                var root = ac.transform.root;
                float best = float.MaxValue;
                foreach (var hit in Physics.RaycastAll(ac.transform.position + Vector3.up * 3f, Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.transform.root == root || hit.distance >= best) continue;
                    best = hit.distance;
                    ground = hit.collider.transform.root.name + "/" + hit.collider.name;
                }
            }
            catch { }
            return string.Format(CultureInfo.InvariantCulture,
                "{0} {1} id {2} at ({3:F0}, {4:F0}, {5:F0}) speed {6} m/s radarAlt {7} m roll {8} deg pitch {9} deg over '{10}'",
                fac, ac.definition != null ? ac.definition.jsonKey : "?", ac.GetInstanceID(), p.x, p.y, p.z,
                F(ac.speed), F(ac.radarAlt), F(roll), F(pitch), ground);
        }

        static void Log(string s)
        {
            if (!MissionProbe.Enabled) return;
            MissionProbe.NoteStatic(s);
        }

        [HarmonyPatch(typeof(AIPilotTaxiState), "EjectCheck")]
        internal static class EjectCheckPatch
        {
            static void Prefix(AIPilotTaxiState __instance, out bool __state) => __state = Disembarking(__instance);

            static void Postfix(AIPilotTaxiState __instance, bool __state)
            {
                try
                {
                    if (!__state && Disembarking(__instance))
                        Log("TAXI GIVE-UP roll check (|cockpit.right . up| > 0.05, about 2.9 deg): " + Describe(Traverse.Create(__instance).Field("aircraft").GetValue<Aircraft>()));
                }
                catch (Exception e) { Log("TaxiDiag EjectCheck: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(AIPilotTaxiState), "Taxi_OnTakeDamage")]
        internal static class TaxiDamagePatch
        {
            static void Prefix(AIPilotTaxiState __instance)
            {
                try
                {
                    if (!Disembarking(__instance))
                        Log("TAXI GIVE-UP damage: " + Describe(Traverse.Create(__instance).Field("aircraft").GetValue<Aircraft>()));
                }
                catch (Exception e) { Log("TaxiDiag damage: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(AIPilotTaxiState), "IsStuck")]
        internal static class IsStuckPatch
        {
            static void Postfix(AIPilotTaxiState __instance, Aircraft aircraft, bool __result)
            {
                try
                {
                    if (!__result || aircraft == null || !logged.Add(aircraft.GetInstanceID() * 7 + 1)) return;
                    Log($"TAXI GIVE-UP stuck (speed timer {F(StuckSpeed(__instance))} s of 30, yaw timer {F(StuckYaw(__instance))} s of 60, " +
                        $"state '{__instance.stateDisplayName}'): " + Describe(aircraft));
                }
                catch (Exception e) { Log("TaxiDiag stuck: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(AIPilotTakeoffState), "Takeoff_OnTakeDamage")]
        internal static class TakeoffDamagePatch
        {
            static void Prefix(AIPilotTakeoffState __instance)
            {
                try { Log("TAKEOFF EJECT damage: " + Describe(Traverse.Create(__instance).Field("aircraft").GetValue<Aircraft>())); }
                catch (Exception e) { Log("TaxiDiag takeoff damage: " + e.Message); }
            }
        }

        /// <summary>Every ejection with the pilot state it came from (taxi, take-off, parked, combat...).</summary>
        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.StartEjectionSequence))]
        internal static class EjectPatch
        {
            static void Prefix(Aircraft __instance)
            {
                try
                {
                    if (!MissionProbe.Enabled || __instance == null || __instance.HasEjected()) return;
                    if (!logged.Add(__instance.GetInstanceID() * 7 + 2)) return;
                    var pilot = __instance.pilots != null && __instance.pilots.Length > 0 ? __instance.pilots[0] : null;
                    var st = pilot != null ? pilot.currentState : null;
                    Log($"EJECT from {(st != null ? st.GetType().Name : "no state")} '{(st != null ? st.stateDisplayName : "")}': " + Describe(__instance));
                }
                catch (Exception e) { Log("TaxiDiag eject: " + e.Message); }
            }
        }
    }
}
