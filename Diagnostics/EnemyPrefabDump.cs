using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit_Us
{
    public class EnemyPrefabDump : MonoBehaviour
    {
        private bool dumped;
        private int frames;

        private void Awake()
        {
            Debug.Log(
                "[Orbit-Us] EnemyPrefabDump component started."
            );
        }

        private void Update()
        {
            if (dumped)
                return;

            frames++;

            if (frames % 60 != 0)
                return;

            waveManager manager =
                UnityEngine.Object.FindObjectOfType<waveManager>();

            if (manager == null)
            {
                Debug.Log(
                    "[Orbit-Us] EnemyPrefabDump: Still waiting for waveManager..."
                );

                return;
            }

            Debug.Log(
                "[Orbit-Us] EnemyPrefabDump: Found waveManager!"
            );

            dumped = true;

            Debug.Log(
                "========== ORBIT US ENEMY PREFAB DUMP =========="
            );

            DumpPool(
                "REGULAR ENEMY POOL",
                manager.enemyPool
            );

            if (manager.minibossPool != null)
            {
                DumpPool(
                    "MINIBOSS POOL",
                    new List<GameObject>(
                        manager.minibossPool
                    )
                );
            }
            else
            {
                Debug.Log(
                    "[Orbit-Us] Miniboss pool is NULL."
                );
            }

            if (enemyManager.instance != null)
            {
                List<enemy> enemies =
                    enemyManager.instance.GetEnemies();

                Debug.Log(
                    $"[Orbit-Us] Currently registered enemies: {enemies.Count}"
                );

                foreach (enemy currentEnemy in enemies)
                {
                    if (currentEnemy == null)
                        continue;

                    Debug.Log(
                        $"[Orbit-Us] Active enemy: {currentEnemy.gameObject.name} " +
                        $"Type={currentEnemy.GetType().FullName}"
                    );
                }
            }
            else
            {
                Debug.Log(
                    "[Orbit-Us] enemyManager.instance is NULL."
                );
            }

            Debug.Log(
                "========== END ENEMY PREFAB DUMP =========="
            );
        }

        private void DumpPool(
            string poolName,
            List<GameObject> pool)
        {
            Debug.Log(
                $"===== {poolName} ====="
            );

            if (pool == null)
            {
                Debug.Log(
                    "POOL IS NULL"
                );

                return;
            }

            Debug.Log(
                $"Prefab count: {pool.Count}"
            );

            for (int i = 0; i < pool.Count; i++)
            {
                GameObject prefab = pool[i];

                if (prefab == null)
                {
                    Debug.Log(
                        $"[{i}] NULL PREFAB"
                    );

                    continue;
                }

                Debug.Log(
                    $"--- [{i}] {prefab.name} ---"
                );

                Component[] components =
                    prefab.GetComponents<Component>();

                foreach (Component component in components)
                {
                    if (component == null)
                        continue;

                    Type type =
                        component.GetType();

                    Debug.Log(
                        $"    Component: {type.FullName}"
                    );

                    if (component is enemyController)
                    {
                        Debug.Log(
                            $"    >>> ENEMY CONTROLLER: {type.FullName}"
                        );
                    }

                    if (component is enemy)
                    {
                        Debug.Log(
                            "    >>> ENEMY COMPONENT"
                        );
                    }

                    if (component is health)
                    {
                        health h =
                            component as health;

                        Debug.Log(
                            $"    >>> HEALTH: hp={h.hp}, hpMax={h.hpMax}"
                        );
                    }
                }
            }
        }
    }
}