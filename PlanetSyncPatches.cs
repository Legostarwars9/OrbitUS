using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    [HarmonyPatch]
    public static class PlanetSpawnIdentityPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(spawner)))
                if (method.Name == "Spawn" && method.ReturnType == typeof(GameObject) &&
                    method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == typeof(GameObject))
                    yield return method;
        }

        public static void Postfix(GameObject __0, GameObject __result)
        {
            // Record before SpawnPlanet/SpawnShopPlanet replace the clone name.
            // This runs in single player too, for worlds hosted after generation.
            if (__0 != null && __result != null)
                PlanetReplicator.RegisterSpawn(__0, __result);
        }
    }

    [HarmonyPatch]
    public static class ClientPlanetGenerationPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "Start", "FixedUpdate" })
            {
                var method = AccessTools.DeclaredMethod(typeof(MapProceeding), name);
                if (method != null) yield return method;
            }
        }

        public static bool Prefix()
        {
            return !OrbitUs.MultiplayerActive || OrbitUs.IsHost;
        }
    }

    [HarmonyPatch]
    public static class RemotePlanetSimulationPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new Dictionary<Type, HashSet<string>>
            {
                { typeof(kickStart), new HashSet<string> { "Start", "AutoKick" } },
                { typeof(gravity), new HashSet<string> { "Deactivate", "Unload" } },
                { typeof(star), new HashSet<string> { "Start", "FixedUpdate", "Develop", "Collapse",
                    "Supernova", "BecomeBlackHole", "AddUnloaded", "ReloadObjects" } },
                { typeof(temperature), new HashSet<string> { "Start", "FixedUpdate", "Feed", "CheckToExplode",
                    "Explode", "Disintegrate", "ChangeColor" } },
                { typeof(atmosphere), new HashSet<string> { "Start", "FixedUpdate", "Feed", "AddHeat",
                    "BurnEverything", "BurnAway" } },
                { typeof(livelyPlanet), new HashSet<string> { "Start", "BurnEverything" } },
                { typeof(colorRandomizer), new HashSet<string> { "Start" } },
                { typeof(radarPlanet), new HashSet<string> { "Start", "FixedUpdate", "CheckAtmo" } },
                { typeof(dragBody), new HashSet<string> { "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D" } }
            };
            var seen = new HashSet<MethodBase>();
            foreach (Type type in typeof(gravity).Assembly.GetTypes())
            {
                foreach (var target in targets)
                {
                    if (!target.Key.IsAssignableFrom(type)) continue;
                    foreach (var method in AccessTools.GetDeclaredMethods(type))
                        if (target.Value.Contains(method.Name) && seen.Add(method))
                            yield return method;
                }
            }
        }

        public static bool Prefix(Component __instance)
        {
            return !OrbitUs.MultiplayerActive || OrbitUs.IsHost || !PlanetReplicator.IsReplica(__instance);
        }
    }

    [HarmonyPatch]
    public static class RemotePlanetShopInitializationPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in typeof(shopField).Assembly.GetTypes())
            {
                if (!typeof(shopField).IsAssignableFrom(type)) continue;
                var method = AccessTools.DeclaredMethod(type, "Start");
                if (method != null) yield return method;
            }
        }

        public static bool Prefix(shopField __instance)
        {
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost || !PlanetReplicator.IsReplica(__instance)) return true;
            // Keep the shop trigger usable, but never reroll the host's offers
            // or reset synchronized price multipliers on first activation.
            __instance.shop = UnityEngine.Object.FindObjectOfType<shop>(true);
            return false;
        }
    }

    [HarmonyPatch]
    public static class RemotePlanetCollisionPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "OnCollisionEnter2D", "OnCollisionStay2D" })
            {
                var method = AccessTools.DeclaredMethod(typeof(collisionBody), name);
                if (method != null) yield return method;
            }
        }

        public static bool Prefix(collisionBody __instance, Collision2D __0)
        {
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost) return true;
            // A non-replica's callback can also merge/destroy its replica target.
            return !PlanetReplicator.IsReplica(__instance) &&
                   (__0 == null || !PlanetReplicator.IsReplica(__0.transform));
        }
    }

    [HarmonyPatch(typeof(devourer), "OnCollisionEnter2D")]
    public static class RemotePlanetDevourerPatch
    {
        public static bool Prefix(devourer __instance, Collision2D __0)
        {
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost || __0 == null) return true;
            if (PlanetReplicator.IsReplica(__0.transform)) return false;
            // Black holes still affect the local pilot. Celestial mergers and
            // consumption of world objects are decided by the host.
            return !PlanetReplicator.IsReplica(__instance) || __0.gameObject.GetComponent<player>() != null;
        }
    }
}
