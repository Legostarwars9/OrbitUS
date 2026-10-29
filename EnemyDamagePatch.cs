using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Orbit_Us
{
    [HarmonyPatch]
    public static class RemoteEnemyDeathPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // Overrides must be patched too, including hunterHealth.CheckIfDead.
            foreach (Type type in typeof(health).Assembly.GetTypes())
            {
                if (!typeof(health).IsAssignableFrom(type)) continue;
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                    if ((method.Name == "CheckIfDead" || method.Name == "Die") && method.GetParameters().Length == 0)
                        yield return method;
            }
        }

        public static bool Prefix(health __instance)
        {
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost || EnemyReplicator.Instance == null)
                return true;
            return !EnemyReplicator.Instance.CaptureRemoteDamage(__instance);
        }
    }

    [HarmonyPatch]
    public static class RemoteWormSimulationPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // Worm is both health and AI: retain damage callbacks while stopping
            // Start from growing another local tail and FixedUpdate from moving it.
            foreach (Type type in new[] { typeof(worm), typeof(wormShadow) })
                foreach (string name in new[] { "Start", "FixedUpdate" })
                {
                    var method = AccessTools.DeclaredMethod(type, name);
                    if (method != null) yield return method;
                }
        }

        public static bool Prefix(worm __instance)
        {
            return !OrbitUs.MultiplayerActive || OrbitUs.IsHost || EnemyReplicator.Instance == null ||
                   !EnemyReplicator.Instance.TryGetRemoteEnemyId(__instance.gameObject, out _);
        }
    }

    [HarmonyPatch]
    public static class RemoteEnemyCollisionPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // Unity can deliver collision/trigger messages to disabled behaviours.
            // Suppress these on replicas too, while health keeps receiving hits.
            foreach (Type type in typeof(health).Assembly.GetTypes())
            {
                if (!typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type) ||
                    typeof(health).IsAssignableFrom(type)) continue;
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                    if (method.Name.StartsWith("OnCollision", StringComparison.Ordinal) ||
                        method.Name.StartsWith("OnTrigger", StringComparison.Ordinal) ||
                        (typeof(temperature).IsAssignableFrom(type) &&
                         (method.Name == "Explode" || method.Name == "Disintegrate")))
                        yield return method;
            }
        }

        public static bool Prefix(UnityEngine.MonoBehaviour __instance)
        {
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost || EnemyReplicator.Instance == null)
                return true;
            var current = __instance.transform;
            while (current != null)
            {
                if (EnemyReplicator.Instance.TryGetRemoteEnemyId(current.gameObject, out _))
                    return false;
                current = current.parent;
            }
            return true;
        }
    }
}
