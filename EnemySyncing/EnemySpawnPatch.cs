using HarmonyLib;

namespace Orbit_Us
{
    [HarmonyPatch(typeof(waveManager), "StartWave")]
    public static class WaveManagerStartWavePatch
    {
        public static bool Prefix()
        {
            if (!OrbitUs.MultiplayerActive)
                return true;

            if (OrbitUs.IsHost)
                return true;

            return false;
        }

        public static void Postfix(waveManager __instance,bool __runOriginal)
        {
            if(!__runOriginal || !OrbitUs.MultiplayerActive || !OrbitUs.IsHost)return;
            var connection=OrbitUs.Instance?.Connection;
            if(connection==null || !connection.IsConnected || connection.LocalPlayerId<=0)return;
            // StartWave resets this counter from the vanilla wave size before
            // its coroutine's first spawn. Keep the base sizes unchanged so
            // restarts do not accumulate bonuses and the next wave recounts joins/leaves.
            int players=1+connection.GetRemotePlayerIds().Count;
            __instance.enemiesLeftToSpawn+=2*players;
        }
    }

    [HarmonyPatch(typeof(waveManager), "SpawnRandomEnemy")]
    public static class WaveManagerSpawnRandomEnemyPatch
    {
        public static bool Prefix()
        {
            if (!OrbitUs.MultiplayerActive)
                return true;

            if (OrbitUs.IsHost)
                return true;

            return false;
        }
    }
}
