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

        private Dictionary<int, GameObject> remotePlayers =
            new Dictionary<int, GameObject>();

        private Dictionary<int, Vector3> targetPositions =
            new Dictionary<int, Vector3>();

        private Dictionary<int, float> targetRotations =
            new Dictionary<int, float>();

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

            Debug.Log(
                "[Orbit-Us] Player replication initialized."
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
            if (packet.Data.Length < 4)
                return;

            int playerId =
                BitConverter.ToInt32(
                    packet.Data,
                    0
                );

            Debug.Log(
                $"[Orbit-Us] Player connected: {playerId}"
            );

            if (localPlayerId == -1)
            {
                localPlayerId = playerId;

                Debug.Log(
                    $"[Orbit-Us] Local player ID: {localPlayerId}"
                );

                return;
            }

            if (playerId == localPlayerId)
                return;

            CreateRemotePlayer(playerId);
        }

        private void HandlePlayerDisconnected(
            NetworkPacket packet)
        {
            if (packet.Data.Length < 4)
                return;

            int playerId =
                BitConverter.ToInt32(
                    packet.Data,
                    0
                );

            RemoveRemotePlayer(playerId);
        }

        private void HandlePlayerTransform(
            NetworkPacket packet)
        {
            PlayerTransformData transform =
                PlayerTransformData.Deserialize(
                    packet.Data
                );

            if (localPlayerId == -1)
                return;

            if (transform.PlayerId == localPlayerId)
                return;

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
            if (remotePlayers.ContainsKey(playerId))
                return;

            GameObject remotePlayer =
                new GameObject(
                    $"OrbitUs_RemotePlayer_{playerId}"
                );

            SpriteRenderer renderer =
                remotePlayer.AddComponent<SpriteRenderer>();

            renderer.sprite = playerSprite;

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
                int playerId = pair.Key;
                GameObject player = pair.Value;

                if (player == null)
                    continue;

                if (!targetPositions.ContainsKey(
                        playerId))
                    continue;

                player.transform.position =
                    targetPositions[playerId];

                player.transform.rotation =
                    Quaternion.Euler(
                        0f,
                        0f,
                        targetRotations[playerId]
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
        }
    }
}