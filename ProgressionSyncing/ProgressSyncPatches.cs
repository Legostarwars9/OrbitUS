using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    [HarmonyPatch(typeof(player), "PotentiallyCollectData")]
    public static class SharedDataCollectionPatch
    {
        public static bool Prefix(dataBlob __0)
        {
            if(ProgressReplicator.Instance==null) return true;
            ProgressReplicator.Instance.Collect(__0);return false;
        }
    }
    [HarmonyPatch(typeof(dataBlob), "OnDisable")]
    public static class DataDisableCollectionPatch
    {
        public static bool Prefix() => ProgressReplicator.Instance==null;
    }
    [HarmonyPatch(typeof(item), "FixedUpdate")]
    public static class ClientDataMotionPatch
    {
        public static bool Prefix(item __instance)
        {
            var sync=ProgressReplicator.Instance;
            if(sync==null || !(__instance is dataBlob)) return true;
            sync.StepData((dataBlob)__instance);return false;
        }
    }
    [HarmonyPatch(typeof(dataBlob), "Extra")]
    public static class ClientDataExtraPatch
    {
        public static bool Prefix() => ProgressReplicator.Instance?.IsClient!=true;
    }
    [HarmonyPatch(typeof(storyManager), "StartTransmission")]
    public static class SharedTransmissionPatch
    {
        public static bool Prefix() => ProgressReplicator.Instance==null || ProgressReplicator.Instance.BeginTransmission();
    }
    [HarmonyPatch(typeof(storyManager), "OpenComms")]
    public static class SharedOpenCommsPatch
    {
        public static bool Prefix()
        {
            if(ProgressReplicator.Instance?.IsClient!=true) return true;
            ProgressReplicator.Instance.Request(ProgressActionKind.OpenComms);return false;
        }
    }
    [HarmonyPatch(typeof(storyManager), "CloseComms")]
    public static class SharedCloseCommsPatch
    {
        public static bool Prefix(storyManager __instance)
        {
            if(ProgressReplicator.Instance==null) return true;
            if(ProgressReplicator.Instance.IsClient)
            {ProgressReplicator.Instance.Request(ProgressActionKind.CloseComms);return false;}
            // The native method indexes a seven-element list after the final upload.
            if(__instance.transmissionIndex<7) return true;
            __instance.transmitButton.SetActive(false);__instance.terminalBox.SetActive(false);
            __instance.dialogue.StopYapping();__instance.dialogue.gameObject.SetActive(false);
            __instance.timeManager.sloMo=false;return false;
        }
    }
    [HarmonyPatch]
    public static class ClientStoryAuthorityPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(string name in new[]{"StartPlanetBossAftermath","StartHunterDeathAftermath","ResetForRespawn"})
                yield return AccessTools.DeclaredMethod(typeof(storyManager),name);
            foreach(string name in new[]{"FixedUpdate","ResetForRespawn"})
                yield return AccessTools.DeclaredMethod(typeof(bossManager),name);
            yield return AccessTools.DeclaredMethod(typeof(player),"CollectData");
            yield return AccessTools.DeclaredMethod(typeof(bossHealthBar),"Update");
        }
        public static bool Prefix() => ProgressReplicator.Instance?.IsClient!=true;
    }
    [HarmonyPatch]
    public static class ClientBossSpawningPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var method in AccessTools.GetDeclaredMethods(typeof(bossManager)))
                if(method.Name.StartsWith("Summon") && method.ReturnType==typeof(GameObject)) yield return method;
        }
        public static bool Prefix(MethodBase __originalMethod, object[] __args, ref GameObject __result)
        {
            if(ProgressReplicator.Instance?.IsClient!=true) return true;
            if(__originalMethod.Name=="SummonPlantBoss" && __args.Length==3)
            {
                var planet=__args[2] as GameObject;
                int id=PlanetReplicator.Instance?.GetObjectId(planet)??0;
                if(id>0) ProgressReplicator.Instance.Request(ProgressActionKind.PlantBoss,id);
                // The player's trigger callback immediately reads the result's transform.
                __result=planet;
            }
            else __result=player.Instance!=null?player.Instance.gameObject:null;
            return false;
        }
    }
    [HarmonyPatch(typeof(devourerWarper),"MergePlayer")]
    public static class SharedBlackHoleEntryPatch
    {
        public static bool Prefix(player __0)
        {
            if(ProgressReplicator.Instance?.IsClient!=true || !__0.hasFinalData) return true;
            ProgressReplicator.Instance.Request(ProgressActionKind.EnterBlackHole);return false;
        }
    }
    [HarmonyPatch]
    public static class SharedEndingPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(singularityManager),"StartGoodEnding");
            yield return AccessTools.DeclaredMethod(typeof(singularityManager),"StartBadEnding");
        }
        public static bool Prefix(MethodBase __originalMethod)
        {
            var sync=ProgressReplicator.Instance;if(sync==null) return true;
            bool good=__originalMethod.Name=="StartGoodEnding";
            if(sync.IsClient) {sync.Request(good?ProgressActionKind.GoodEnding:ProgressActionKind.BadEnding);return false;}
            if(!sync.CanEnd || sync.EndingStarted) return false;
            sync.RecordEnding(good?1:2);return true;
        }
    }
}
