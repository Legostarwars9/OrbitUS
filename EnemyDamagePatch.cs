using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit_Us
{
    // Clients still apply the local hit visually, but they do not execute the
    // game's death logic for a network-owned enemy. The host decides whether
    // the enemy actually dies.
    [HarmonyPatch(typeof(health), "HandleDamageImpulse", new[] { typeof(projectile) })]
    public static class HealthHandleDamageImpulsePatch
    {
        public static bool Prefix(health __instance, projectile projectile)
        {
            if (!ShouldHandleRemotely(__instance, out int enemyId) || projectile == null)
                return true;

            if (!TryConsumeProjectile(__instance, projectile))
                return false;

            float damage = projectile.damage;
            ApplyLocalHit(__instance, projectile, damage);
            EnemyReplicator.Instance.SendEnemyDamage(enemyId, damage);

            // Do not run Orbitous' CheckIfDead/Die locally. The host owns death.
            return false;
        }

        private static void ApplyLocalHit(health target, projectile projectile, float damage)
        {
            if (target.ouch != "")
                target.audioManager?.Play(target.ouch, target.transform.position);

            target.hp -= damage;

            SpriteRenderer renderer = target.GetComponent<SpriteRenderer>();
            if (renderer != null)
                projectile.EmitParticles(projectile.transform, renderer.color, 2f);

            if (projectile.explosionRadius > 0f)
                projectile.ProjectileExplode();
        }

        public static bool TryConsumeProjectile(health target, projectile projectile)
        {
            List<projectile> deadProjectiles =
                Traverse.Create(target).Field("deadProjectiles")
                    .GetValue<List<projectile>>();

            if (deadProjectiles == null)
                return true;

            if (deadProjectiles.Contains(projectile))
                return false;

            deadProjectiles.Add(projectile);
            return true;
        }

        private static bool ShouldHandleRemotely(health health, out int enemyId)
        {
            enemyId = -1;
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost || health == null)
                return false;

            return EnemyReplicator.Instance != null &&
                   EnemyReplicator.Instance.TryGetRemoteEnemyId(
                       health.gameObject, out enemyId);
        }
    }

    // worm overrides the projectile overload, so the base health patch does not
    // see it. Preserve its headHp behavior on the client and send the same
    // damage amount to the host.
    [HarmonyPatch(typeof(worm), "HandleDamageImpulse", new[] { typeof(projectile) })]
    public static class WormHandleDamageImpulsePatch
    {
        public static bool Prefix(worm __instance, projectile projectile)
        {
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost ||
                __instance == null || projectile == null ||
                EnemyReplicator.Instance == null ||
                !EnemyReplicator.Instance.TryGetRemoteEnemyId(
                    __instance.gameObject, out int enemyId))
            {
                return true;
            }

            if (!HealthHandleDamageImpulsePatch.TryConsumeProjectile(__instance, projectile))
                return false;

            float damage = projectile.damage;
            __instance.headHp -= damage;
            __instance.headHp = Mathf.Clamp(__instance.headHp, 0f, 100000f);

            __instance.temperature.heat += projectile.projHeat * 0.001f;

            if (projectile.explosionRadius > 0f)
                projectile.ProjectileExplode();

            EnemyReplicator.Instance.SendEnemyDamage(enemyId, damage);
            return false;
        }
    }

    [HarmonyPatch(typeof(wormShadow), "HandleDamageImpulse", new[] { typeof(projectile) })]
    public static class WormShadowHandleDamageImpulsePatch
    {
        public static bool Prefix(wormShadow __instance, projectile projectile)
        {
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost ||
                __instance == null || projectile == null ||
                EnemyReplicator.Instance == null ||
                !EnemyReplicator.Instance.TryGetRemoteEnemyId(
                    __instance.gameObject, out int enemyId))
            {
                return true;
            }

            if (!HealthHandleDamageImpulsePatch.TryConsumeProjectile(__instance, projectile))
                return false;

            float damage = projectile.damage;
            __instance.hp -= damage;

            SpriteRenderer renderer = __instance.GetComponent<SpriteRenderer>();
            if (renderer != null)
                projectile.EmitParticles(projectile.transform, renderer.color, 2f);

            if (projectile.explosionRadius > 0f)
                projectile.ProjectileExplode();

            EnemyReplicator.Instance.SendEnemyDamage(enemyId, damage);
            return false;
        }
    }

    [HarmonyPatch(typeof(health), "HandleDamageContinuous", new[] { typeof(projectile) })]
    public static class HealthHandleDamageContinuousPatch
    {
        public static bool Prefix(health __instance, projectile projectile)
        {
            if (!OrbitUs.MultiplayerActive || OrbitUs.IsHost ||
                __instance == null || projectile == null ||
                EnemyReplicator.Instance == null)
                return true;

            if (!EnemyReplicator.Instance.TryGetRemoteEnemyId(
                    __instance.gameObject, out int enemyId))
                return true;

            // Match Orbitous' own condition for continuous damage.
            if (!(projectile is railBeam) && projectile.fieldMultiplier <= 0f)
                return true;

            float damage = projectile.damage * 0.1f;

            worm targetWorm = __instance as worm;
            if (targetWorm != null)
            {
                targetWorm.headHp -= damage;
                targetWorm.headHp = Mathf.Clamp(
                    targetWorm.headHp, 0f, 100000f);
            }
            else
            {
                __instance.hp -= damage;
            }

            // Do not execute CheckIfDead/Die on the client.
            EnemyReplicator.Instance.SendEnemyDamage(enemyId, damage);
            return false;
        }
    }
}
