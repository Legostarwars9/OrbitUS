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

        private readonly Dictionary<int, GameObject> players =
            new Dictionary<int, GameObject>();

        private readonly Dictionary<int, Text> distanceLabels =
            new Dictionary<int, Text>();

        private Text distanceList;
        private float nextDistanceUpdate;
        private Font distanceFont;

        private const float ArrowSize = 15f;

        private const float DistanceListX = 1800f;
        private const float DistanceListY = 900f;

        public PlayerTracker(
            NetworkUDPConnection connection,
            Sprite arrowSprite)
        {
            this.connection = connection;
            this.arrowSprite = arrowSprite;
        }

        public void Initialize()
        {
            connection.OnPacketReceived += HandlePacket;

            localPlayerId =
                connection.LocalPlayerId;

            CreateCanvas();

            Debug.Log(
                "[Orbit-Us] Player tracker initialized."
            );
        }

        private Color GetPlayerColor(int playerId)
        {
            return PlayerNameplates.ColorFor(playerId);
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

            CanvasScaler scaler =
                canvasObject.AddComponent<CanvasScaler>();

            scaler.uiScaleMode =
                CanvasScaler.ScaleMode.ConstantPixelSize;

            scaler.scaleFactor = 1f;

            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject distanceObject =
                new GameObject(
                    "OrbitUs_DistanceList"
                );

            distanceObject.transform.SetParent(
                canvas.transform,
                false
            );

            distanceList =
                distanceObject.AddComponent<Text>();

            distanceList.font =
                Resources.GetBuiltinResource<Font>(
                    "Arial.ttf"
                );

            distanceList.fontSize = 14;

            distanceList.alignment =
                TextAnchor.LowerLeft;

            distanceList.horizontalOverflow =
                HorizontalWrapMode.Overflow;

            distanceList.verticalOverflow =
                VerticalWrapMode.Overflow;

            RectTransform distanceTransform =
                distanceObject.GetComponent<RectTransform>();

            distanceTransform.anchorMin =
                new Vector2(0f, 0f);

            distanceTransform.anchorMax =
                new Vector2(0f, 0f);

            distanceTransform.pivot =
                new Vector2(0f, 0f);

            distanceTransform.anchoredPosition =
                new Vector2(
                    DistanceListX,
                    DistanceListY
                );

            distanceTransform.sizeDelta =
                new Vector2(
                    250f,
                    150f
                );
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
                    localPlayerId = playerId;
            }

            if (playerId == localPlayerId)
                return;

            CreatePlayer(playerId);
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

            RemovePlayer(playerId);

            RepositionPlayers();
            UpdateDistanceList();
        }

        private void HandlePlayerTransform(
            NetworkPacket packet)
        {
            if (packet.Data==null || !PlayerTransformData.IsSupportedLength(packet.Data.Length))
                return;

            PlayerTransformData transform =
                PlayerTransformData.Deserialize(
                    packet.Data
                );

            if (localPlayerId == -1)
                localPlayerId =
                    connection.LocalPlayerId;

            if (transform.PlayerId == localPlayerId)
                return;

            if(transform.HasLifeState && !transform.Alive)
            {
                if(players.ContainsKey(transform.PlayerId))
                {RemovePlayer(transform.PlayerId);RepositionPlayers();UpdateDistanceList();}
                return;
            }

            if (!players.ContainsKey(
                    transform.PlayerId))
            {
                CreatePlayer(
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

        private void CreatePlayer(
            int playerId)
        {
            if (playerId == localPlayerId)
                return;

            if (players.ContainsKey(playerId))
                return;

            if (arrowSprite == null)
            {
                Debug.LogWarning(
                    $"[Orbit-Us] Cannot create tracker for player {playerId}: arrow sprite is missing."
                );

                return;
            }

            GameObject player =
                new GameObject(
                    $"OrbitUs_TrackedPlayer_{playerId}"
                );

            players.Add(
                playerId,
                player
            );

            CreateArrow(playerId);

            RepositionPlayers();
            UpdateDistanceList();

            Debug.Log(
                $"[Orbit-Us] Created tracker for player {playerId}"
            );
        }

        private void CreateArrow(
            int playerId)
        {
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

            arrowImage.preserveAspect = true;

            arrowImage.color =
                GetPlayerColor(playerId);

            RectTransform arrowTransform =
                arrowObject.GetComponent<RectTransform>();

            arrowTransform.sizeDelta =
                new Vector2(
                    ArrowSize,
                    ArrowSize
                );

            arrowTransform.anchorMin =
                new Vector2(
                    0.5f,
                    0.5f
                );

            arrowTransform.anchorMax =
                new Vector2(
                    0.5f,
                    0.5f
                );

            arrowTransform.pivot =
                new Vector2(
                    0.5f,
                    -10f
                );

            arrowTransform.anchoredPosition =
                Vector2.zero;

            arrows.Add(
                playerId,
                arrowObject
            );
        }

        private void RepositionPlayers()
        {
            foreach (
                GameObject arrow
                in arrows.Values)
            {
                if (arrow == null)
                    continue;

                RectTransform arrowTransform =
                    arrow.GetComponent<RectTransform>();

                arrowTransform.anchorMin =
                    new Vector2(
                        0.5f,
                        0.5f
                    );

                arrowTransform.anchorMax =
                    new Vector2(
                        0.5f,
                        0.5f
                    );

                arrowTransform.pivot =
                    new Vector2(
                        0.5f,
                        -1f
                    );

                arrowTransform.anchoredPosition =
                    Vector2.zero;
            }
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
                    arrow.SetActive(false);
                    UnityEngine.Object.Destroy(
                        arrow
                    );
                }

                arrows.Remove(playerId);
            }

            if (players.TryGetValue(
                    playerId,
                    out GameObject player))
            {
                if (player != null)
                {
                    player.SetActive(false);
                    UnityEngine.Object.Destroy(
                        player
                    );
                }

                players.Remove(playerId);
            }

            if (distanceLabels.TryGetValue(
                    playerId,
                    out Text label))
            {
                if (label != null)
                {
                    label.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(
                        label.gameObject
                    );
                }

                distanceLabels.Remove(playerId);
            }
        }

        private void UpdateDistanceList()
        {
            if (distanceList == null)
                return;

            var localPlayer=global::player.Instance;

            if (localPlayer == null)
                return;

            List<int> playerIds =
                new List<int>(
                    players.Keys
                );

            playerIds.Sort();

            foreach (int playerId in playerIds)
            {
                GameObject remotePlayer =
                    players[playerId];

                if (remotePlayer == null)
                    continue;

                float distance =
                    Vector2.Distance(
                        localPlayer.transform.position,
                        remotePlayer.transform.position
                    );

                if(!distanceLabels.TryGetValue(playerId,out Text label) || label==null)
                {
                    var created=new GameObject($"OrbitUs_Distance_{playerId}");
                    created.transform.SetParent(distanceList.transform,false);
                    label=created.AddComponent<Text>();distanceLabels[playerId]=label;
                    if(distanceFont==null)distanceFont=Resources.GetBuiltinResource<Font>("Arial.ttf");
                    label.font=distanceFont;label.fontSize=14;label.alignment=TextAnchor.MiddleLeft;
                    label.horizontalOverflow=HorizontalWrapMode.Overflow;label.verticalOverflow=VerticalWrapMode.Overflow;
                }

                label.color =
                    GetPlayerColor(playerId);

                if (distance < 1000f)
                {
                    label.text =
                        connection.GetPlayerName(playerId)+": " +
                        distance.ToString("0") +
                        "m";
                }
                else
                {
                    label.text =
                        connection.GetPlayerName(playerId)+": " +
                        (distance / 1000f)
                            .ToString("0.0") +
                        "km";
                }

                RectTransform labelTransform =
                    label.rectTransform;

                labelTransform.anchorMin =
                    new Vector2(0f, 1f);

                labelTransform.anchorMax =
                    new Vector2(0f, 1f);

                labelTransform.pivot =
                    new Vector2(0f, 1f);

                labelTransform.sizeDelta =
                    new Vector2(
                        250f,
                        20f
                    );

                labelTransform.anchoredPosition =
                    new Vector2(
                        0f,
                        -playerIds.IndexOf(playerId) * 20f
                    );

            }

            distanceList.text = "";
        }

        public void Update()
        {
            if (canvas == null)
                return;

            var localPlayer=global::player.Instance;

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
                if(PlayerReplicator.Instance!=null && PlayerReplicator.Instance.TryGetRemotePosition(playerId,out Vector3 rendered))
                    remotePlayer.transform.position=rendered;

                if (!arrows.ContainsKey(playerId))
                    continue;

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

                float angle =
                    Mathf.Atan2(
                        direction.y,
                        direction.x
                    ) *
                    Mathf.Rad2Deg;

                RectTransform arrowTransform =
                    arrow.GetComponent<RectTransform>();

                arrowTransform.rotation =
                    Quaternion.Euler(
                        0f,
                        0f,
                        angle - 90f
                    );
            }

            if(Time.unscaledTime>=nextDistanceUpdate)
            {nextDistanceUpdate=Time.unscaledTime+.1f;UpdateDistanceList();}
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
                    arrow.SetActive(false);
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
                    player.SetActive(false);
                    UnityEngine.Object.Destroy(
                        player
                    );
                }
            }

            foreach (
                Text label
                in distanceLabels.Values)
            {
                if (label != null)
                {
                    label.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(
                        label.gameObject
                    );
                }
            }

            arrows.Clear();
            players.Clear();
            distanceLabels.Clear();

            if (canvas != null)
            {
                UnityEngine.Object.Destroy(
                    canvas.gameObject
                );

                canvas = null;
            }

            distanceList = null;
            localPlayerId = -1;
        }
    }
}
