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