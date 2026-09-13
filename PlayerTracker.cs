using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Orbit_Us
{
    public class PlayerTracker
    {
        private NetworkUDPConnection connection;
        private Sprite arrowSprite;

        private int localPlayerId = -1;

        private Canvas canvas;

        private readonly Dictionary<int, GameObject> arrows =
            new Dictionary<int, GameObject>();

        private readonly Dictionary<int, Text> distanceTexts =
            new Dictionary<int, Text>();

        private readonly Dictionary<int, GameObject> players =
            new Dictionary<int, GameObject>();

        public PlayerTracker(
            NetworkUDPConnection connection,
            Sprite arrowSprite)
        {
            this.connection = connection;
            this.arrowSprite = arrowSprite;
        }

        public void Initialize()
        {
            connection.OnPacketReceived +=
                HandlePacket;

            localPlayerId =
                connection.LocalPlayerId;

            CreateCanvas();

            Debug.Log(
                "[Orbit-Us] Player tracker initialized."
            );
        }

        private void CreateCanvas()
        {
            GameObject canvasObject =
                new GameObject(
                    "OrbitUs_PlayerTracker"
                );

            canvas =
                canvasObject.AddComponent<Canvas>();

            canvas.renderMode =
                RenderMode.ScreenSpaceOverlay;

            canvasObject.AddComponent<CanvasScaler>();

            canvasObject.AddComponent<GraphicRaycaster>();
        }

        private void HandlePacket(
            NetworkPacket packet)
        {
            try
            {
                if (packet.Data == null)
                    return;

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
                    $"[Orbit-Us] Player tracker error: {ex}"
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

            if (localPlayerId == -1)
            {
                localPlayerId =
                    connection.LocalPlayerId;

                if (localPlayerId == -1)
                {
                    localPlayerId =
                        playerId;
                }
            }

            if (playerId == localPlayerId)
                return;

            CreateArrow(
                playerId
            );
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

            RemovePlayer(
                playerId
            );
        }

        private void HandlePlayerTransform(
            NetworkPacket packet)
        {
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

            if (!players.ContainsKey(
                    transform.PlayerId))
            {
                CreateArrow(
                    transform.PlayerId
                );
            }

            GameObject player =
                players[
                    transform.PlayerId
                ];

            if (player == null)
                return;

            player.transform.position =
                new Vector3(
                    transform.X,
                    transform.Y,
                    0f
                );
        }

        private void CreateArrow(
            int playerId)
        {
            if (playerId == localPlayerId)
                return;

            if (arrows.ContainsKey(
                    playerId))
            {
                return;
            }

            if (arrowSprite == null)
            {
                Debug.LogWarning(
                    $"[Orbit-Us] Cannot create tracker for player {playerId}: arrow sprite is missing."
                );

                return;
            }

            GameObject arrowObject =
                new GameObject(
                    $"OrbitUs_Arrow_{playerId}"
                );

            arrowObject.transform.SetParent(
                canvas.transform,
                false
            );

            Image arrowImage =
                arrowObject.AddComponent<Image>();

            arrowImage.sprite =
                arrowSprite;

            arrowImage.preserveAspect =
                true;

            RectTransform arrowTransform =
                arrowObject.GetComponent<RectTransform>();

            arrowTransform.sizeDelta =
                new Vector2(
                    50f,
                    50f
                );

            GameObject textObject =
                new GameObject(
                    $"OrbitUs_Distance_{playerId}"
                );

            textObject.transform.SetParent(
                arrowObject.transform,
                false
            );

            Text distanceText =
                textObject.AddComponent<Text>();

            distanceText.text =
                "0m";

            distanceText.font =
                Resources.GetBuiltinResource<Font>(
                    "Arial.ttf"
                );

            distanceText.alignment =
                TextAnchor.MiddleCenter;

            distanceText.fontSize =
                14;

            RectTransform textTransform =
                textObject.GetComponent<RectTransform>();

            textTransform.sizeDelta =
                new Vector2(
                    150f,
                    30f
                );

            textTransform.anchoredPosition =
                new Vector2(
                    0f,
                    -35f
                );

            arrows.Add(
                playerId,
                arrowObject
            );

            distanceTexts.Add(
                playerId,
                distanceText
            );

            GameObject remotePlayer =
                new GameObject(
                    $"OrbitUs_TrackedPlayer_{playerId}"
                );

            players.Add(
                playerId,
                remotePlayer
            );

            Debug.Log(
                $"[Orbit-Us] Created tracker for player {playerId}"
            );
        }

        private void RemovePlayer(
            int playerId)
        {
            if (arrows.TryGetValue(
                    playerId,
                    out GameObject arrow))
            {
                if (arrow != null)
                {
                    UnityEngine.Object.Destroy(
                        arrow
                    );
                }

                arrows.Remove(
                    playerId
                );
            }

            distanceTexts.Remove(
                playerId
            );

            if (players.TryGetValue(
                    playerId,
                    out GameObject player))
            {
                if (player != null)
                {
                    UnityEngine.Object.Destroy(
                        player
                    );
                }

                players.Remove(
                    playerId
                );
            }
        }

        public void Update()
        {
            if (canvas == null)
                return;

            Camera cam =
                Camera.main;

            if (cam == null)
                return;

            playerController localPlayer =
                UnityEngine.Object.FindObjectOfType<
                    playerController
                >();

            if (localPlayer == null)
                return;

            Vector3 localPosition =
                localPlayer.transform.position;

            foreach (
                KeyValuePair<int, GameObject> pair
                in players)
            {
                int playerId =
                    pair.Key;

                GameObject remotePlayer =
                    pair.Value;

                if (remotePlayer == null)
                    continue;

                if (!arrows.ContainsKey(
                        playerId))
                {
                    continue;
                }

                GameObject arrow =
                    arrows[playerId];

                if (arrow == null)
                    continue;

                Vector3 difference =
                    remotePlayer.transform.position -
                    localPosition;

                float distance =
                    difference.magnitude;

                if (distance < 0.01f)
                    continue;

                Vector2 direction =
                    new Vector2(
                        difference.x,
                        difference.y
                    ).normalized;

                Vector2 screenCenter =
                    new Vector2(
                        Screen.width / 2f,
                        Screen.height / 2f
                    );

                float angle =
                    Mathf.Atan2(
                        direction.y,
                        direction.x
                    ) *
                    Mathf.Rad2Deg;

                RectTransform arrowTransform =
                    arrow.GetComponent<RectTransform>();

                arrowTransform.position =
                    screenCenter;

                arrowTransform.rotation =
                    Quaternion.Euler(
                        0f,
                        0f,
                        angle - 90f
                    );

                Text distanceText =
                    distanceTexts[playerId];

                if (distanceText != null)
                {
                    if (distance < 1000f)
                    {
                        distanceText.text =
                            distance.ToString("0") +
                            "m";
                    }
                    else
                    {
                        distanceText.text =
                            (distance / 1000f)
                            .ToString("0.0") +
                            "km";
                    }
                }
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
                GameObject arrow
                in arrows.Values)
            {
                if (arrow != null)
                {
                    UnityEngine.Object.Destroy(
                        arrow
                    );
                }
            }

            foreach (
                GameObject player
                in players.Values)
            {
                if (player != null)
                {
                    UnityEngine.Object.Destroy(
                        player
                    );
                }
            }

            arrows.Clear();
            distanceTexts.Clear();
            players.Clear();

            if (canvas != null)
            {
                UnityEngine.Object.Destroy(
                    canvas.gameObject
                );

                canvas = null;
            }

            localPlayerId = -1;
        }
    }
}