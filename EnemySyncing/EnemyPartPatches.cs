using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    [HarmonyPatch]
    public static class RemoteWormPartLifecyclePatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(string name in new[]{"Awake","GrowStart","Update","HandOver"})yield return AccessTools.DeclaredMethod(typeof(wormPart),name);
        }
        public static bool Prefix(wormPart __instance)=>!EnemyReplicator.IsReplica(__instance);
    }
}
