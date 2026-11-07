using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    [HarmonyPatch(typeof(menuButton),"PressReplay")]
    public static class TeamWipeReplayPatch
    {
        public static bool Prefix()=>OrbitUs.Instance==null || !OrbitUs.Instance.ReplayAfterTeamWipe();
    }
    [HarmonyPatch(typeof(player),"Die")]
    public static class MultiplayerPlayerDeathPatch
    {
        public static bool Prefix(player __instance)
            =>!OrbitUs.MultiplayerActive || PlayerReplicator.Instance==null || !PlayerReplicator.Instance.EnterSpectator(__instance);
    }
    [HarmonyPatch]
    public static class MultiplayerRunResetPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(gameManager),"ResetEverything");
            yield return AccessTools.DeclaredMethod(typeof(resetManager),"ResetEverything");
            yield return AccessTools.DeclaredMethod(typeof(menuButton),"PressReplay");
            foreach(var type in new[]{typeof(player),typeof(shooterWithLoadout),typeof(storyManager),typeof(waveManager),typeof(bossManager),typeof(tracker)})
                yield return AccessTools.DeclaredMethod(type,"ResetForRespawn");
        }
        public static bool Prefix()=>!OrbitUs.MultiplayerActive || PlayerReplicator.Instance==null;
    }
    [HarmonyPatch]
    public static class SpectatorGameplayPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var type in new[]{typeof(player),typeof(playerController),typeof(shooterWithLoadout)})
                foreach(var method in AccessTools.GetDeclaredMethods(type))
                    if(method.Name=="Update" || method.Name=="FixedUpdate" || method.Name.StartsWith("OnTrigger") || method.Name.StartsWith("OnCollision") ||
                        method.Name=="Shoot" || method.Name=="ShootTeleport" || method.Name=="ToggleInventory")yield return method;
            yield return AccessTools.DeclaredMethod(typeof(shop),"Buy");
            yield return AccessTools.DeclaredMethod(typeof(fuelInsurance),"PurchaseDelivery");
        }
        public static bool Prefix()=>!PlayerLifeController.Spectating;
    }
    [HarmonyPatch]
    public static class SpectatorDamagePatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var method in AccessTools.GetDeclaredMethods(typeof(player)))
                if(method.Name=="HandleDamageImpulse" || method.Name=="HandleDamageContinuous")yield return method;
        }
        public static bool Prefix()=>PlayerReplicator.Instance?.Life.IgnoreDamage!=true;
    }
    [HarmonyPatch(typeof(menuManager),"Update")]
    public static class SpectatorDeathScreenPatch
    {
        public static void Postfix(menuManager __instance)
        {if(PlayerLifeController.Spectating && __instance.deathScreen!=null)__instance.deathScreen.SetActive(false);}
    }
    [HarmonyPatch(typeof(temperature),"Explode")]
    public static class MultiplayerPlayerHeatDeathPatch
    {
        public static bool Prefix(temperature __instance)
        {
            var local=player.Instance;
            if(!OrbitUs.MultiplayerActive || PlayerReplicator.Instance==null || local==null || __instance.gameObject!=local.gameObject)return true;
            if(!PlayerReplicator.Instance.Life.IgnoreDamage){local.hp=0;local.CheckIfDead();}
            return false;
        }
    }
    [HarmonyPatch(typeof(temperature),"Disintegrate")]
    public static class SpectatorDisintegrationPatch
    {
        public static bool Prefix(temperature __instance,ref float __result)
        {
            if(player.Instance!=null && __instance.gameObject==player.Instance.gameObject && PlayerReplicator.Instance?.Life.IgnoreDamage==true)
            {__result=0;return false;}
            return true;
        }
    }
}
