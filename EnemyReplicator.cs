using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit_Us
{
    public class EnemyReplicator
    {
        private class RemoteEnemy
        {
            public GameObject GameObject;
            public int Type;

            public Vector2 TargetPosition;

            public float TargetHP;
        }

        private NetworkUDPConnection connection;

        private Dictionary<int, RemoteEnemy> remoteEnemies =
            new Dictionary<int, RemoteEnemy>();

        private Dictionary<enemy, int> hostEnemies =
            new Dictionary<enemy, int>();

        private int nextEnemyId = 1;

        private float sendTimer;

        private const float SendRate = 0.05f;

        private const float PositionCorrectionSpeed = 20f;

        private bool isHost;

        private bool initialized;

        public EnemyReplicator(
            NetworkUDPConnection connection,
            bool isHost)
        {
            this.connection = connection;
            this.isHost = isHost;
        }

        public void Initialize()
        {
            if (initialized)
                return;

            initialized = true;

            connection.OnPacketReceived +=
                HandlePacket;

            Debug.Log(
                $"[Orbit-Us] EnemyReplicator initialized. Host={isHost}"
            );
        }

        public void Update()
        {
            if (!initialized)
                return;

            if (isHost)
            {
                sendTimer +=
                    Time.deltaTime;

                if (sendTimer >= SendRate)
                {
                    sendTimer = 0f;

                    SendEnemySnapshot();
                }
            }
            else
            {
                UpdateRemoteEnemies();
            }
        }

        private async void SendEnemySnapshot()
        {
            enemyManager manager =
                enemyManager.instance;

            if (manager == null)
                return;

            List<enemy> enemies =
                manager.GetEnemies();

            List<EnemyTransformData> snapshot =
                new List<EnemyTransformData>();

            HashSet<enemy> currentEnemies =
                new HashSet<enemy>();

            foreach (enemy currentEnemy in enemies)
            {
                if (currentEnemy == null)
                    continue;

                currentEnemies.Add(
                    currentEnemy
                );

                if (!hostEnemies.TryGetValue(
                        currentEnemy,
                        out int enemyId))
                {
                    enemyId =
                        nextEnemyId++;

                    hostEnemies.Add(
                        currentEnemy,
                        enemyId
                    );

                    Debug.Log(
                        $"[Orbit-Us] Host enemy registered: " +
                        $"{enemyId} ({currentEnemy.gameObject.name})"
                    );
                }

                health enemyHealth =
                    currentEnemy.GetComponent<health>();

                float hp =
                    enemyHealth != null
                        ? enemyHealth.hp
                        : 0f;

                int type =
                    GetEnemyType(
                        currentEnemy.gameObject
                    );

                snapshot.Add(
                    new EnemyTransformData
                    {
                        EnemyId = enemyId,
                        Type = type,
                        X =
                            currentEnemy.transform
                                .position.x,
                        Y =
                            currentEnemy.transform
                                .position.y,
                        HP = hp
                    }
                );
            }

            List<enemy> removedEnemies =
                new List<enemy>();

            foreach (
                KeyValuePair<enemy, int> pair
                in hostEnemies)
            {
                if (!currentEnemies.Contains(
                        pair.Key))
                {
                    removedEnemies.Add(
                        pair.Key
                    );
                }
            }

            foreach (enemy removed in removedEnemies)
            {
                hostEnemies.Remove(
                    removed
                );
            }

            await connection.SendEnemySnapshot(
                snapshot
            );
        }

        private int GetEnemyType(
            GameObject enemyObject)
        {
            waveManager manager =
                UnityEngine.Object.FindObjectOfType<
                    waveManager
                >();

            if (manager == null)
                return -1;

            if (manager.enemyPool != null)
            {
                for (int i = 0;
                     i < manager.enemyPool.Count;
                     i++)
                {
                    GameObject prefab =
                        manager.enemyPool[i];

                    if (prefab == null)
                        continue;

                    if (MatchesPrefab(
                            enemyObject,
                            prefab))
                    {
                        return i;
                    }
                }
            }

            if (manager.minibossPool != null)
            {
                for (int i = 0;
                     i < manager.minibossPool.Length;
                     i++)
                {
                    GameObject prefab =
                        manager.minibossPool[i];

                    if (prefab == null)
                        continue;

                    if (MatchesPrefab(
                            enemyObject,
                            prefab))
                    {
                        return 1000 + i;
                    }
                }
            }

            return -1;
        }

        private bool MatchesPrefab(
            GameObject instance,
            GameObject prefab)
        {
            if (instance == null ||
                prefab == null)
            {
                return false;
            }

            health instanceHealth =
                instance.GetComponent<health>();

            health prefabHealth =
                prefab.GetComponent<health>();

            if (instanceHealth != null &&
                prefabHealth != null)
            {
                if (Mathf.Abs(
                        instanceHealth.hpMax -
                        prefabHealth.hpMax
                    ) > 0.01f)
                {
                    return false;
                }
            }

            Component[] instanceComponents =
                instance.GetComponents<Component>();

            Component[] prefabComponents =
                prefab.GetComponents<Component>();

            if (instanceComponents.Length !=
                prefabComponents.Length)
            {
                return false;
            }

            foreach (Component prefabComponent
                     in prefabComponents)
            {
                if (prefabComponent == null)
                    continue;

                Type prefabType =
                    prefabComponent.GetType();

                bool found = false;

                foreach (
                    Component instanceComponent
                    in instanceComponents)
                {
                    if (instanceComponent == null)
                        continue;

                    if (instanceComponent.GetType() ==
                        prefabType)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return false;
            }

            return true;
        }

        private void HandlePacket(
            NetworkPacket packet)
        {
            if (packet.Type !=
                PacketType.EnemySnapshot)
            {
                return;
            }

            if (isHost)
                return;

            try
            {
                using (
                    System.IO.MemoryStream stream =
                        new System.IO.MemoryStream(
                            packet.Data))
                using (
                    System.IO.BinaryReader reader =
                        new System.IO.BinaryReader(
                            stream))
                {
                    int count =
                        reader.ReadInt32();

                    HashSet<int> receivedIds =
                        new HashSet<int>();

                    for (int i = 0;
                         i < count;
                         i++)
                    {
                        int dataLength =
                            reader.ReadInt32();

                        byte[] data =
                            reader.ReadBytes(
                                dataLength
                            );

                        EnemyTransformData enemyData =
                            EnemyTransformData.Deserialize(
                                data
                            );

                        receivedIds.Add(
                            enemyData.EnemyId
                        );

                        UpdateRemoteEnemy(
                            enemyData
                        );
                    }

                    List<int> removedIds =
                        new List<int>();

                    foreach (
                        KeyValuePair<int, RemoteEnemy> pair
                        in remoteEnemies)
                    {
                        if (!receivedIds.Contains(
                                pair.Key))
                        {
                            removedIds.Add(
                                pair.Key
                            );
                        }
                    }

                    foreach (int enemyId
                             in removedIds)
                    {
                        RemoveRemoteEnemy(
                            enemyId
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[Orbit-Us] Enemy snapshot error: {ex}"
                );
            }
        }

        private void UpdateRemoteEnemy(
            EnemyTransformData data)
        {
            if (data.Type < 0)
            {
                Debug.LogWarning(
                    $"[Orbit-Us] Enemy {data.EnemyId} " +
                    $"has unknown type {data.Type}"
                );

                return;
            }

            if (!remoteEnemies.TryGetValue(
                    data.EnemyId,
                    out RemoteEnemy remoteEnemy))
            {
                GameObject spawned =
                    SpawnEnemy(
                        data.Type,
                        new Vector2(
                            data.X,
                            data.Y
                        )
                    );

                if (spawned == null)
                    return;

                remoteEnemy =
                    new RemoteEnemy
                    {
                        GameObject = spawned,
                        Type = data.Type,
                        TargetPosition =
                            new Vector2(
                                data.X,
                                data.Y
                            ),
                        TargetHP = data.HP
                    };

                remoteEnemies.Add(
                    data.EnemyId,
                    remoteEnemy
                );

                Debug.Log(
                    $"[Orbit-Us] Created remote enemy " +
                    $"{data.EnemyId} type {data.Type}"
                );
            }

            remoteEnemy.TargetPosition =
                new Vector2(
                    data.X,
                    data.Y
                );

            remoteEnemy.TargetHP =
                data.HP;

            health enemyHealth =
                remoteEnemy.GameObject
                    .GetComponent<health>();

            if (enemyHealth != null)
            {
                enemyHealth.hp =
                    data.HP;
            }
        }

        private GameObject SpawnEnemy(
            int type,
            Vector2 position)
        {
            waveManager manager =
                UnityEngine.Object.FindObjectOfType<
                    waveManager
                >();

            if (manager == null)
                return null;

            GameObject prefab = null;

            if (type >= 1000)
            {
                int minibossIndex =
                    type - 1000;

                if (manager.minibossPool == null ||
                    minibossIndex < 0 ||
                    minibossIndex >=
                    manager.minibossPool.Length)
                {
                    return null;
                }

                prefab =
                    manager.minibossPool[
                        minibossIndex
                    ];
            }
            else
            {
                if (manager.enemyPool == null ||
                    type < 0 ||
                    type >= manager.enemyPool.Count)
                {
                    return null;
                }

                prefab =
                    manager.enemyPool[type];
            }

            if (prefab == null)
                return null;

            GameObject spawned =
                UnityEngine.Object.Instantiate(
                    prefab,
                    position,
                    prefab.transform.rotation
                );

            Rigidbody2D rb =
                spawned.GetComponent<Rigidbody2D>();

            if (rb != null)
                rb.velocity = Vector2.zero;

            return spawned;
        }

        private void UpdateRemoteEnemies()
        {
            foreach (
                KeyValuePair<int, RemoteEnemy> pair
                in remoteEnemies)
            {
                RemoteEnemy remoteEnemy =
                    pair.Value;

                if (remoteEnemy == null ||
                    remoteEnemy.GameObject == null)
                {
                    continue;
                }

                Transform transform =
                    remoteEnemy.GameObject.transform;

                Vector3 target =
                    new Vector3(
                        remoteEnemy.TargetPosition.x,
                        remoteEnemy.TargetPosition.y,
                        transform.position.z
                    );

                transform.position =
                    Vector3.Lerp(
                        transform.position,
                        target,
                        PositionCorrectionSpeed *
                        Time.deltaTime
                    );

                health enemyHealth =
                    remoteEnemy.GameObject
                        .GetComponent<health>();

                if (enemyHealth != null)
                {
                    enemyHealth.hp =
                        remoteEnemy.TargetHP;
                }
            }
        }

        private void RemoveRemoteEnemy(
            int enemyId)
        {
            if (!remoteEnemies.TryGetValue(
                    enemyId,
                    out RemoteEnemy remoteEnemy))
            {
                return;
            }

            if (remoteEnemy != null &&
                remoteEnemy.GameObject != null)
            {
                UnityEngine.Object.Destroy(
                    remoteEnemy.GameObject
                );
            }

            remoteEnemies.Remove(
                enemyId
            );
        }

        public void Destroy()
        {
            if (connection != null)
            {
                connection.OnPacketReceived -=
                    HandlePacket;
            }

            foreach (
                RemoteEnemy remoteEnemy
                in remoteEnemies.Values)
            {
                if (remoteEnemy != null &&
                    remoteEnemy.GameObject != null)
                {
                    UnityEngine.Object.Destroy(
                        remoteEnemy.GameObject
                    );
                }
            }

            remoteEnemies.Clear();
            hostEnemies.Clear();

            nextEnemyId = 1;
            sendTimer = 0f;
            initialized = false;
        }
    }
}