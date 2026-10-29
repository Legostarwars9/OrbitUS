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
            public health Health;
            public Vector2 TargetPosition;
            public float TargetRotation;
            public float TargetHP;
            public float TargetHeadHP;
            public int MissingSnapshots;
        }

        private readonly NetworkUDPConnection connection;
        private readonly bool isHost;
        private readonly Dictionary<int, RemoteEnemy> remoteEnemies = new Dictionary<int, RemoteEnemy>();
        private readonly Dictionary<GameObject, int> remoteIds = new Dictionary<GameObject, int>();
        private readonly Dictionary<enemy, int> hostEnemies = new Dictionary<enemy, int>();
        private readonly Dictionary<int, enemy> hostEnemiesById = new Dictionary<int, enemy>();
        private readonly Dictionary<int, GameObject> prefabs = new Dictionary<int, GameObject>();
        private readonly Dictionary<string, int> prefabTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> unknownPrefabs = new HashSet<string>();
        private readonly Dictionary<int, DamageRequestHistory> damageHistory = new Dictionary<int, DamageRequestHistory>();
        private readonly Dictionary<int, EnemyDamageData> pendingDamage = new Dictionary<int, EnemyDamageData>();
        private readonly EnemySnapshotCodec.Assembler snapshots = new EnemySnapshotCodec.Assembler();
        private int nextEnemyId = 1;
        private int nextSnapshotId = 1;
        private int nextDamageId = 1;
        private float sendTimer;
        private float retryTimer;
        private float catalogTimer;
        private bool initialized;
        private bool sendingSnapshot;
        private GameObject stagingRoot;
        private waveManager waveManager;
        private const float SendRate = 0.05f;
        private const int MissingSnapshotsBeforeRemoval = 3;
        public static EnemyReplicator Instance { get; private set; }

        public EnemyReplicator(NetworkUDPConnection connection, bool isHost)
        {
            this.connection = connection;
            this.isHost = isHost;
        }

        public void Initialize()
        {
            if (initialized) return;
            initialized = true;
            Instance = this;
            connection.OnPacketReceived += HandlePacket;
            RefreshPrefabCatalog();
            Debug.Log($"[Orbit-Us] Enemy replication initialized. Host={isHost}, prefabs={prefabs.Count}");
        }

        public void Update()
        {
            if (!initialized) return;
            catalogTimer += Time.unscaledDeltaTime;
            if (catalogTimer >= 2f || waveManager == null)
            {
                catalogTimer = 0f;
                RefreshPrefabCatalog();
            }

            if (isHost)
            {
                sendTimer += Time.deltaTime;
                if (sendTimer >= SendRate && !sendingSnapshot)
                {
                    sendTimer = 0f;
                    SendEnemySnapshot();
                }
            }
            else
            {
                if (waveManager != null) waveManager.StopAllCoroutines();
                RemoveUnauthorizedClientEnemies();
                UpdateRemoteEnemies();
                retryTimer += Time.unscaledDeltaTime;
                if (retryTimer >= 0.2f)
                {
                    retryTimer = 0f;
                    int firstPending = FirstPendingDamageId();
                    foreach (var damage in new List<EnemyDamageData>(pendingDamage.Values))
                        if ((long)damage.RequestId - firstPending < 1024)
                            _ = connection.SendEnemyDamageRequest(damage);
                }
            }
        }

        private void RefreshPrefabCatalog()
        {
            waveManager = UnityEngine.Object.FindObjectOfType<waveManager>();
            // Include locked enemies and nested/body prefabs independently of the
            // randomly changing wave pools on each machine. Never use live clones.
            foreach (var prefab in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (prefab == null || prefab.scene.IsValid() || prefab.GetComponent<enemy>() == null)
                    continue;
                string name = prefab.name;
                int type = StableType(name);
                if (prefabs.TryGetValue(type, out GameObject existing) && existing != null && existing.name != name)
                {
                    Debug.LogError($"[Orbit-Us] Enemy prefab hash collision: {name}, {existing.name}");
                    continue;
                }
                prefabs[type] = prefab;
                prefabTypes[name] = type;
            }
        }

        private static int StableType(string name)
        {
            // String.GetHashCode is not guaranteed to agree across runtimes.
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in name)
                    hash = (hash ^ character) * 16777619;
                return (int)(hash & 0x7fffffff);
            }
        }

        private int GetEnemyType(GameObject enemyObject)
        {
            string name = enemyObject.name.Replace("(Clone)", "").Trim();
            if (prefabTypes.TryGetValue(name, out int type)) return type;
            // Some scripts rename children (e.g. "node 2"). Only use a structural
            // match when it identifies exactly one prefab.
            int match = -1;
            foreach (var pair in prefabs)
            {
                if (pair.Value == null || !MatchesPrefab(enemyObject, pair.Value)) continue;
                if (match >= 0) { match = -1; break; }
                match = pair.Key;
            }
            if (match < 0 && unknownPrefabs.Add(name))
                Debug.LogWarning($"[Orbit-Us] Cannot identify enemy prefab '{name}'; skipping replication.");
            return match;
        }

        private bool MatchesPrefab(GameObject instance, GameObject prefab)
        {
            var instanceComponents = instance.GetComponents<Component>();
            var prefabComponents = prefab.GetComponents<Component>();
            if (instanceComponents.Length != prefabComponents.Length) return false;
            var types = new List<Type>();
            foreach (var component in instanceComponents)
                if (component != null) types.Add(component.GetType());
            foreach (var component in prefabComponents)
            {
                if (component == null) continue;
                if (!types.Remove(component.GetType())) return false;
            }
            return types.Count == 0;
        }

        private void RemoveUnauthorizedClientEnemies()
        {
            if (enemyManager.instance == null) return;
            foreach (var current in new List<enemy>(enemyManager.instance.GetEnemies()))
            {
                if (current == null) continue;
                var ancestor = current.transform;
                bool belongsToReplica = false;
                while (ancestor != null)
                {
                    if (remoteIds.ContainsKey(ancestor.gameObject)) { belongsToReplica = true; break; }
                    ancestor = ancestor.parent;
                }
                if (belongsToReplica) continue;
                // Deactivate now: Destroy alone leaves time to reproduce again.
                current.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(current.gameObject);
            }
        }

        private async void SendEnemySnapshot()
        {
            if (enemyManager.instance == null) return;
            sendingSnapshot = true;
            try
            {
                var snapshot = new List<EnemyTransformData>();
                var currentEnemies = new HashSet<enemy>();
                foreach (var current in new List<enemy>(enemyManager.instance.GetEnemies()))
                {
                    if (current == null || !currentEnemies.Add(current)) continue;
                    if (!hostEnemies.TryGetValue(current, out int id))
                    {
                        id = nextEnemyId++;
                        hostEnemies.Add(current, id);
                        hostEnemiesById.Add(id, current);
                    }
                    var h = current.GetComponent<health>();
                    int type = GetEnemyType(current.gameObject);
                    if (type < 0 || (h != null && h.dead)) continue;
                    var w = h as worm;
                    snapshot.Add(new EnemyTransformData
                    {
                        EnemyId = id, Type = type,
                        X = current.transform.position.x, Y = current.transform.position.y,
                        HP = h != null ? h.hp : 0f,
                        HeadHP = w != null ? w.headHp : 0f,
                        Rotation = current.transform.eulerAngles.z
                    });
                }
                foreach (var pair in new List<KeyValuePair<enemy, int>>(hostEnemies))
                {
                    if (currentEnemies.Contains(pair.Key)) continue;
                    hostEnemies.Remove(pair.Key);
                    hostEnemiesById.Remove(pair.Value);
                }
                await connection.SendEnemySnapshot(snapshot, nextSnapshotId++);
            }
            catch (Exception ex) { Debug.LogError($"[Orbit-Us] Enemy snapshot send failed: {ex}"); }
            finally { sendingSnapshot = false; }
        }

        private void HandlePacket(NetworkPacket packet)
        {
            if (!initialized) return;
            try
            {
                if (packet.Type == PacketType.EnemyDamage && isHost)
                    HandleEnemyDamage(packet);
                else if (packet.Type == PacketType.EnemyDamageAck && !isHost)
                {
                    if (packet.Data.Length == 8 && BitConverter.ToInt32(packet.Data, 0) == connection.LocalPlayerId)
                        pendingDamage.Remove(BitConverter.ToInt32(packet.Data, 4));
                }
                else if (packet.Type == PacketType.EnemySnapshot && !isHost &&
                         snapshots.TryAdd(packet.Data, out List<EnemyTransformData> snapshot))
                {
                    var receivedIds = new HashSet<int>();
                    foreach (var data in snapshot)
                    {
                        receivedIds.Add(data.EnemyId);
                        UpdateRemoteEnemy(data);
                    }
                    foreach (var pair in new List<KeyValuePair<int, RemoteEnemy>>(remoteEnemies))
                    {
                        if (receivedIds.Contains(pair.Key)) pair.Value.MissingSnapshots = 0;
                        else if (++pair.Value.MissingSnapshots >= MissingSnapshotsBeforeRemoval)
                            RemoveRemoteEnemy(pair.Key);
                    }
                }
            }
            catch (Exception ex) { Debug.LogError($"[Orbit-Us] Enemy replication packet error: {ex}"); }
        }

        private void UpdateRemoteEnemy(EnemyTransformData data)
        {
            if (!remoteEnemies.TryGetValue(data.EnemyId, out RemoteEnemy remote) || remote.GameObject == null)
            {
                if (remote != null && !ReferenceEquals(remote.GameObject, null))
                    remoteIds.Remove(remote.GameObject);
                var spawned = SpawnEnemy(data.Type, new Vector2(data.X, data.Y));
                if (spawned == null) return;
                remote = new RemoteEnemy { GameObject = spawned, Health = spawned.GetComponent<health>() };
                // Add() throws after Instantiate when an old entry survives a
                // destroyed object, leaking another clone on every snapshot.
                remoteEnemies[data.EnemyId] = remote;
                remoteIds[spawned] = data.EnemyId;
                remote.TargetHP = data.HP;
                remote.TargetHeadHP = data.HeadHP;
                RestoreHealth(remote);
                spawned.transform.SetParent(null, true);
                spawned.SetActive(true);
            }
            remote.TargetPosition = new Vector2(data.X, data.Y);
            remote.TargetRotation = data.Rotation;
            CaptureRemoteDamage(remote.Health);
            remote.TargetHP = data.HP;
            remote.TargetHeadHP = data.HeadHP;
            RestoreHealth(remote);
        }

        public bool TryGetRemoteEnemyId(GameObject gameObject, out int enemyId)
        {
            enemyId = -1;
            return initialized && !isHost && gameObject != null && remoteIds.TryGetValue(gameObject, out enemyId);
        }

        // Capture direct HP changes as well as the game's damage methods. Restore
        // host HP before returning so repeated death checks cannot resubmit a hit.
        public bool CaptureRemoteDamage(health target)
        {
            if (target == null || !TryGetRemoteEnemyId(target.gameObject, out int id)) return false;
            RemoteEnemy remote = remoteEnemies[id];
            float damage = Mathf.Max(0f, remote.TargetHP - target.hp);
            var w = target as worm;
            float headDamage = w != null && !(w is wormShadow)
                ? Mathf.Max(0f, remote.TargetHeadHP - w.headHp) : 0f;
            RestoreHealth(remote);
            if (damage > 0f || headDamage > 0f) QueueDamage(id, damage, headDamage);
            return true;
        }

        public void SendEnemyDamage(int enemyId, float damage)
        {
            QueueDamage(enemyId, damage, 0f);
        }

        private void QueueDamage(int enemyId, float damage, float headDamage)
        {
            if (!initialized || isHost || float.IsNaN(damage) || float.IsInfinity(damage) ||
                float.IsNaN(headDamage) || float.IsInfinity(headDamage) ||
                damage < 0f || headDamage < 0f || (damage == 0f && headDamage == 0f)) return;
            var request = new EnemyDamageData
            {
                RequestId = nextDamageId++, EnemyId = enemyId,
                Damage = damage, HeadDamage = headDamage
            };
            pendingDamage.Add(request.RequestId, request);
            // Keep unacknowledged requests within the host's replay window.
            // Otherwise a long-delayed hit could be pruned before it arrives.
            if ((long)request.RequestId - FirstPendingDamageId() < 1024)
                _ = connection.SendEnemyDamageRequest(request);
        }

        private int FirstPendingDamageId()
        {
            int first = int.MaxValue;
            foreach (int id in pendingDamage.Keys) first = Math.Min(first, id);
            return first;
        }

        private void HandleEnemyDamage(NetworkPacket packet)
        {
            EnemyDamageData request = EnemyDamageData.Deserialize(packet.Data);
            if (request.PlayerId <= 0) return;
            if (!damageHistory.TryGetValue(request.PlayerId, out DamageRequestHistory history))
            {
                history = new DamageRequestHistory();
                damageHistory.Add(request.PlayerId, history);
            }
            if (history.Accept(request.RequestId) &&
                hostEnemiesById.TryGetValue(request.EnemyId, out enemy target) && target != null)
            {
                var h = target.GetComponent<health>();
                if (h != null && !h.dead)
                {
                    var w = h as worm;
                    if (w != null && !(w is wormShadow))
                    {
                        w.headHp = Mathf.Max(0f, w.headHp - request.HeadDamage - request.Damage);
                        w.hp = w.headHp;
                        foreach (var segment in w.tailHealth)
                            if (segment != null) w.hp += segment.hp;
                        w.CheckIfDead();
                    }
                    else if (request.Damage > 0f)
                        h.HandleDamageImpulse(request.Damage);
                }
            }
            // ACK duplicates and dead targets too: these requests are finished.
            _ = connection.SendEnemyDamageAcknowledgement(request.PlayerId, request.RequestId);
        }

        private GameObject SpawnEnemy(int type, Vector2 position)
        {
            if (!prefabs.TryGetValue(type, out GameObject prefab) || prefab == null)
            {
                if (unknownPrefabs.Add("type:" + type))
                    Debug.LogWarning($"[Orbit-Us] Missing enemy prefab type {type}; both players need the same game/mod build.");
                return null;
            }
            if (stagingRoot == null)
            {
                stagingRoot = new GameObject("OrbitUs_EnemyStaging");
                stagingRoot.SetActive(false);
            }
            var spawned = UnityEngine.Object.Instantiate(prefab, position, prefab.transform.rotation, stagingRoot.transform);
            try { MakeRemoteEnemyServerControlled(spawned); }
            catch
            {
                UnityEngine.Object.Destroy(spawned);
                throw;
            }
            return spawned;
        }

        private void MakeRemoteEnemyServerControlled(GameObject spawned)
        {
            // Prevent local reproduction, structure growth, gun coroutines,
            // temperature splitting and physics. Health must still receive hits.
            foreach (var behaviour in spawned.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour is health || behaviour is enemy) continue;
                behaviour.StopAllCoroutines();
                behaviour.enabled = false;
            }
            foreach (var rb in spawned.GetComponentsInChildren<Rigidbody2D>(true))
            {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.bodyType = RigidbodyType2D.Kinematic;
            }
        }

        private static void RestoreHealth(RemoteEnemy remote)
        {
            if (remote.Health == null) return;
            remote.Health.hp = remote.TargetHP;
            remote.Health.dead = false;
            var w = remote.Health as worm;
            if (w != null) w.headHp = remote.TargetHeadHP;
        }

        private void UpdateRemoteEnemies()
        {
            foreach (var remote in remoteEnemies.Values)
            {
                if (remote.GameObject == null) continue;
                CaptureRemoteDamage(remote.Health);
                var transform = remote.GameObject.transform;
                transform.position = Vector3.Lerp(transform.position,
                    new Vector3(remote.TargetPosition.x, remote.TargetPosition.y, transform.position.z), 20f * Time.deltaTime);
                transform.rotation = Quaternion.Lerp(transform.rotation,
                    Quaternion.Euler(0f, 0f, remote.TargetRotation), 20f * Time.deltaTime);
            }
        }

        private void RemoveRemoteEnemy(int enemyId)
        {
            if (!remoteEnemies.TryGetValue(enemyId, out RemoteEnemy remote)) return;
            if (!ReferenceEquals(remote.GameObject, null)) remoteIds.Remove(remote.GameObject);
            if (remote.GameObject != null)
            {
                remote.GameObject.SetActive(false);
                UnityEngine.Object.Destroy(remote.GameObject);
            }
            remoteEnemies.Remove(enemyId);
        }

        public void Destroy()
        {
            initialized = false;
            connection.OnPacketReceived -= HandlePacket;
            foreach (int id in new List<int>(remoteEnemies.Keys)) RemoveRemoteEnemy(id);
            if (stagingRoot != null) UnityEngine.Object.Destroy(stagingRoot);
            hostEnemies.Clear();
            hostEnemiesById.Clear();
            pendingDamage.Clear();
            damageHistory.Clear();
            if (Instance == this) Instance = null;
        }
    }
}
