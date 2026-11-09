using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    [HarmonyPatch]
    public static class CombatProjectileImpactPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var type in typeof(projectile).Assembly.GetTypes())
                if(typeof(projectile).IsAssignableFrom(type))
                    foreach(var method in AccessTools.GetDeclaredMethods(type))
                        if(method.Name=="ProjectileExplode" || method.Name=="ProjectileExplodeLight")yield return method;
        }
        public static void Prefix(projectile __instance)=>CombatReplicator.Instance?.CaptureProjectileImpact(__instance);
    }
    [HarmonyPatch]
    public static class CombatProjectileLifecyclePatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var type in typeof(projectile).Assembly.GetTypes())
                if(typeof(projectile).IsAssignableFrom(type))
                {
                    var m=AccessTools.DeclaredMethod(type,"Start");if(m!=null)yield return m;
                }
        }
        public static bool Prefix(projectile __instance)=>__instance.GetComponent<CombatReplicaMarker>()==null;
        public static void Postfix(projectile __instance)=>CombatReplicator.Instance?.RegisterProjectile(__instance);
    }
    [HarmonyPatch]
    public static class CombatReplicaSimulationPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var type in typeof(projectile).Assembly.GetTypes())
                if(typeof(projectile).IsAssignableFrom(type) || type==typeof(decay))
                    foreach(var m in AccessTools.GetDeclaredMethods(type))
                        if(m.Name=="Update" || m.Name=="FixedUpdate" || m.Name.StartsWith("OnCollision") || m.Name.StartsWith("OnTrigger") || (type==typeof(decay) && m.Name=="Start"))yield return m;
        }
        public static bool Prefix(Component __instance)=>__instance.GetComponentInParent<CombatReplicaMarker>()==null;
    }
    [HarmonyPatch(typeof(item),"Start")]
    public static class ResourceDropRegistrationPatch
    {
        public static void Postfix(item __instance)=>CombatReplicator.Instance?.RegisterPickup(__instance);
    }
    [HarmonyPatch(typeof(item),"FixedUpdate")]
    public static class ResourceDropMovementPatch
    {
        public static bool Prefix(item __instance)=>!(CombatReplicator.Instance?.StepPickup(__instance)??false);
    }
    [HarmonyPatch(typeof(player),"OnTriggerEnter2D")]
    public static class ResourceDropCollectionPatch
    {
        public static void Prefix(Collider2D __0)
        {if(__0!=null && !PlayerLifeController.Spectating)CombatReplicator.Instance?.Claim(__0.GetComponent<CombatPickupMarker>());}
    }
    [HarmonyPatch]
    public static class NearestPilotTargetPatch
    {
        public static readonly Type[] ActorTypes={typeof(enemyController),typeof(worm),typeof(wormGun),typeof(stationaryController),typeof(satelliteController),typeof(plantBoss),typeof(hunterController),typeof(plantWalker),typeof(orbiter),typeof(planetBoss),typeof(boxController),typeof(tentacleJoint),typeof(materiaphageBurrow),typeof(matBoss)};
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var type in typeof(enemyController).Assembly.GetTypes())
            {
                bool actor=false;foreach(var parent in ActorTypes)if(parent.IsAssignableFrom(type)){actor=true;break;}
                if(!actor)continue;
                foreach(var m in AccessTools.GetDeclaredMethods(type))
                    if(m.Name=="FixedUpdate" || m.Name=="Update" || m.Name=="Start")yield return m;
            }
        }
        public static bool Prefix(MonoBehaviour __instance,MethodBase __originalMethod)
        {
            if(!OrbitUs.MultiplayerActive || !OrbitUs.IsHost || CombatReplicator.Instance==null)return true;
            bool available=CombatReplicator.Instance.Pilots.Bind(__instance);
            return available || __originalMethod.Name=="Start" || (__instance is wormShadow && player.Instance!=null && player.Instance.dead);
        }
        public static void Postfix(MonoBehaviour __instance,MethodBase __originalMethod)
        {
            if(__originalMethod.Name=="Start" && OrbitUs.IsHost)CombatReplicator.Instance?.Pilots.Bind(__instance);
        }
    }
    [HarmonyPatch]
    public static class NearestPilotHomingPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var type in typeof(projectile).Assembly.GetTypes())
                if(typeof(projectile).IsAssignableFrom(type))
                    foreach(var m in AccessTools.GetDeclaredMethods(type))
                        if(m.Name=="Home" || m.Name=="AntiHome")yield return m;
        }
        public static void Prefix(projectile __instance)
        {
            if(!OrbitUs.IsHost || __instance.playerSafe || CombatReplicator.Instance==null)return;
            // Retain a living target for this shot. Re-selecting the nearest pilot
            // every frame can turn a projectile away just before impact.
            if(CombatReplicator.Instance.Pilots.IsLivingTarget(__instance.homingTargetRb) &&
                __instance.homingTarget==__instance.homingTargetRb.transform)return;
            var target=CombatReplicator.Instance.Pilots.Nearest(__instance.transform.position);
            __instance.homingTarget=target!=null?target.transform:null;__instance.homingTargetRb=target;
        }
    }
    [HarmonyPatch(typeof(wormShadow),"FixedUpdate")]
    public static class SharedShadowSurvivalPatch
    {
        public static bool Prefix(wormShadow __instance)
        {
            if(!OrbitUs.IsHost || CombatReplicator.Instance==null || player.Instance==null || !player.Instance.dead)return true;
            var nearest=CombatReplicator.Instance.Pilots.Nearest(__instance.transform.position);
            if(nearest==null)
            {
                if(PlayerLifeController.Spectating)return false;
                // The native defeat branch still needs a valid transform before
                // it checks playerScript.dead and starts the shared bad ending.
                __instance.target=player.Instance.transform;return true;
            }
            // The native branch assumes the host is the only pilot. Keep fighting
            // while a remote pilot is alive instead of ending on the host's death.
            __instance.target=nearest.transform;
            __instance.movement.BurnForward(__instance.angry?1.5f:1f);
            Vector2 direction=(nearest.position-(Vector2)__instance.transform.position).normalized;
            var rotation=Quaternion.AngleAxis(Mathf.Atan2(direction.y,direction.x)*Mathf.Rad2Deg-90,Vector3.forward);
            __instance.transform.rotation=Quaternion.Slerp(__instance.transform.rotation,rotation,__instance.turnSpeed*90*Time.deltaTime);
            return false;
        }
    }

    [HarmonyPatch]
    public static class CombatReplicaDamagePatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(var type in typeof(health).Assembly.GetTypes())
                if(typeof(health).IsAssignableFrom(type))
                    foreach(var method in AccessTools.GetDeclaredMethods(type))
                        if((method.Name=="OnTriggerEnter2D" || method.Name=="OnTriggerStay2D") && method.GetParameters().Length==1 && method.GetParameters()[0].ParameterType==typeof(Collider2D))yield return method;
        }
        public static bool Prefix(Collider2D __0)=>__0==null || __0.GetComponentInParent<CombatReplicaMarker>()==null;
    }

}
