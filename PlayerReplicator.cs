using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit_Us
{
    public class PlayerReplicator
    {
        private NetworkUDPConnection connection;
        private Sprite playerSprite;

        private int localPlayerId = -1;

        private readonly Dictionary<int, GameObject> remotePlayers =
            new Dictionary<int, GameObject>();

        private readonly Dictionary<int, Vector3> targetPositions =
            new Dictionary<int, Vector3>();

        private readonly Dictionary<int, float> targetRotations =
            new Dictionary<int, float>();

        private const float InterpolationSpeed = 15f;

        public PlayerReplicator(
            NetworkUDPConnection connection,
            Sprite playerSprite)
        {
            this.connection = connection;
            this.playerSprite = playerSprite;
        }

        public void Initialize()
        {
            connection.OnPacketReceived += HandlePacket;

            localPlayerId =
                connection.LocalPlayerId;

            Debug.Log(
                $"[Orbit-Us] Player replication initialized. Local ID: {localPlayerId}"
            );
        }

        private void HandlePacket(
            NetworkPacket packet)
        {
            try
            {
                switch (packet.Type)
                {
                    case PacketType.PlayerConnected:
                        HandlePlayerConnected(packet);
                        break;

                    case PacketType.PlayerDisconnected:
                        HandlePlayerDisconnected(packet);
                        break;

                    case PacketType.PlayerTransform:
                        HandlePlayerTransform(packet);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[Orbit-Us] Player replication error: {ex}"
                );
            }
        }

        private void HandlePlayerConnected(
            NetworkPacket packet)
        {
            if (packet.Data == null ||
                packet.Data.Length < 4)
            {
                return;
            }

            int playerId =
                BitConverter.ToInt32(
                    packet.Data,
                    0
                );

            if (localPlayerId == -1)
            {
                localPlayerId =
                    connection.LocalPlayerId;

                if (localPlayerId == -1)
                {
                    localPlayerId =
                        playerId;
                }

                Debug.Log(
                    $"[Orbit-Us] Local player ID: {localPlayerId}"
                );
            }

            Debug.Log(
                $"[Orbit-Us] Player connected: {playerId}"
            );

            if (playerId == localPlayerId)
            {
                return;
            }

            CreateRemotePlayer(
                playerId
            );
        }

        private void HandlePlayerDisconnected(
            NetworkPacket packet)
        {
            if (packet.Data == null ||
                packet.Data.Length < 4)
            {
                return;
            }

            int playerId =
                BitConverter.ToInt32(
                    packet.Data,
                    0
                );

            RemoveRemotePlayer(
                playerId
            );
        }

        private void HandlePlayerTransform(
            NetworkPacket packet)
        {
            if (packet.Data == null)
                return;

            if (packet.Data.Length < 16)
                return;

            PlayerTransformData transform =
                PlayerTransformData.Deserialize(
                    packet.Data
                );

            if (localPlayerId == -1)
            {
                localPlayerId =
                    connection.LocalPlayerId;
            }

            if (transform.PlayerId ==
                localPlayerId)
            {
                return;
            }

            if (!remotePlayers.ContainsKey(
                    transform.PlayerId))
            {
                CreateRemotePlayer(
                    transform.PlayerId
                );
            }

            targetPositions[
                transform.PlayerId
            ] =
                new Vector3(
                    transform.X,
                    transform.Y,
                    0f
                );

            targetRotations[
                transform.PlayerId
            ] =
                transform.Rotation;
        }

        private void CreateRemotePlayer(
            int playerId)
        {
            if (playerId == localPlayerId)
                return;

            if (remotePlayers.ContainsKey(
                    playerId))
            {
                return;
            }

            GameObject remotePlayer =
                new GameObject(
                    $"OrbitUs_RemotePlayer_{playerId}"
                );

            SpriteRenderer renderer =
                remotePlayer.AddComponent<SpriteRenderer>();

            renderer.sprite =
                playerSprite;

            remotePlayer.transform.localScale =
                Vector3.one;

            remotePlayers.Add(
                playerId,
                remotePlayer
            );

            targetPositions.Add(
                playerId,
                remotePlayer.transform.position
            );

            targetRotations.Add(
                playerId,
                0f
            );

            Debug.Log(
                $"[Orbit-Us] Created remote player {playerId}"
            );
        }

        private void RemoveRemotePlayer(
            int playerId)
        {
            GameObject remotePlayer;

            if (remotePlayers.TryGetValue(
                    playerId,
                    out remotePlayer))
            {
                if (remotePlayer != null)
                {
                    UnityEngine.Object.Destroy(
                        remotePlayer
                    );
                }

                remotePlayers.Remove(
                    playerId
                );
            }

            targetPositions.Remove(
                playerId
            );

            targetRotations.Remove(
                playerId
            );

            Debug.Log(
                $"[Orbit-Us] Removed remote player {playerId}"
            );
        }

        public void Update()
        {
            foreach (
                KeyValuePair<int, GameObject> pair
                in remotePlayers)
            {
                int playerId =
                    pair.Key;

                GameObject player =
                    pair.Value;

                if (player == null)
                    continue;

                if (!targetPositions.ContainsKey(
                        playerId))
                {
                    continue;
                }

                Vector3 targetPosition =
                    targetPositions[
                        playerId
                    ];

                float targetRotation =
                    targetRotations[
                        playerId
                    ];

                player.transform.position =
                    Vector3.Lerp(
                        player.transform.position,
                        targetPosition,
                        InterpolationSpeed *
                        Time.deltaTime
                    );

                Quaternion targetQuaternion =
                    Quaternion.Euler(
                        0f,
                        0f,
                        targetRotation
                    );

                player.transform.rotation =
                    Quaternion.Lerp(
                        player.transform.rotation,
                        targetQuaternion,
                        InterpolationSpeed *
                        Time.deltaTime
                    );
            }
        }

        public void Destroy()
        {
            if (connection != null)
            {
                connection.OnPacketReceived -=
                    HandlePacket;
            }

            foreach (
                GameObject player
                in remotePlayers.Values)
            {
                if (player != null)
                {
                    UnityEngine.Object.Destroy(
                        player
                    );
                }
            }

            remotePlayers.Clear();
            targetPositions.Clear();
            targetRotations.Clear();

            localPlayerId = -1;
        }
    }
}