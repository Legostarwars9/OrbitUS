namespace Orbit_Us
{
    using BepInEx;
    using BepInEx.Configuration;
    using System.IO;
    using UnityEngine;
    using System.Net.Sockets;
    using System.Text;
    using System;
    using System.Threading.Tasks;

    [BepInPlugin("Orbit-Us.test", "Orbit-Us", "0.0.6")]
    public class OrbitUs : BaseUnityPlugin
    {
        private NetworkServer networkServer;
        private NetworkManager networkManager;

        private NetworkUDPServer networkUDPServer;
        private NetworkUDPConnection networkUDPConnection;

        private PlayerReplicator playerReplicator;

        private Sprite playerSprite;

        private bool udpConnected;

        private float transformSendTimer;

        private const float TransformSendRate = 0.05f;

        private ConfigEntry<string> serverAddress;
        private ConfigEntry<int> tcpPort;
        private ConfigEntry<int> udpPort;

        private void Awake()
        {
            Application.runInBackground = true;

            Logger.LogInfo(
                "Orbit Us Loaded"
            );

            LoadConfig();

            LoadAssets();
            LoadImage();

            networkManager =
                new NetworkManager();

            networkServer =
                new NetworkServer();

            networkUDPServer =
                new NetworkUDPServer();

            networkUDPConnection =
                new NetworkUDPConnection();

            networkServer.OnClientConnected +=
                ClientConnected;
        }

        private void LoadConfig()
        {
            serverAddress =
                Config.Bind(
                    "Network",
                    "ServerAddress",
                    "[Put IP address Here]",
                    "Tailscale DNS name or IP address of the server."
                );

            tcpPort =
                Config.Bind(
                    "Network",
                    "TCPPort",
                    7777,
                    "TCP server port."
                );

            udpPort =
                Config.Bind(
                    "Network",
                    "UDPPort",
                    7778,
                    "UDP server port."
                );

            Logger.LogInfo(
                $"Server Address: {serverAddress.Value}"
            );

            Logger.LogInfo(
                $"TCP Port: {tcpPort.Value}"
            );

            Logger.LogInfo(
                $"UDP Port: {udpPort.Value}"
            );
        }

        private void ClientConnected(
            TcpClient client)
        {
            Logger.LogInfo(
                "TCP client connected!"
            );
        }

        private async Task AcceptClient()
        {
            try
            {
                await networkServer.AcceptConnection();

                Logger.LogInfo(
                    "Finished handling TCP client connection."
                );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Network server error: {ex}"
                );
            }
        }

        private async void TestConnection()
        {
            try
            {
                NetworkPacket packet =
                    await networkManager.Connect(
                        serverAddress.Value,
                        tcpPort.Value
                    );

                Logger.LogInfo(
                    "TCP connected to server!"
                );

                Logger.LogInfo(
                    $"Received packet type: {packet.Type}"
                );

                string message =
                    Encoding.UTF8.GetString(
                        packet.Data
                    );

                Logger.LogInfo(
                    $"Packet data: {message}"
                );

                networkManager.StartKeepAlive();

                Logger.LogInfo(
                    "KeepAlive monitoring started."
                );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"TCP connection failed: {ex.Message}"
                );

                networkManager.Disconnect();
            }
        }

        private void StartUDPClient()
        {
            if (udpConnected)
            {
                Logger.LogInfo(
                    "UDP client already connected."
                );

                return;
            }

            try
            {
                Logger.LogInfo(
                    $"Connecting UDP to {serverAddress.Value}:{udpPort.Value}..."
                );

                networkUDPConnection.Connect(
                    serverAddress.Value,
                    udpPort.Value
                );

                playerReplicator =
                    new PlayerReplicator(
                        networkUDPConnection,
                        playerSprite
                    );

                playerReplicator.Initialize();

                udpConnected = true;

                Logger.LogInfo(
                    "UDP player replication started."
                );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"UDP connection failed: {ex.Message}"
                );

                udpConnected = false;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                RestartServers();
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                ConnectToServer();
            }

            UpdatePlayerReplication();
        }

        private void RestartServers()
        {
            Logger.LogInfo(
                "Restarting network servers..."
            );

            playerReplicator?.Destroy();

            playerReplicator = null;

            networkManager.Disconnect();

            networkUDPConnection.Disconnect();

            networkServer.Stop();

            networkUDPServer.Stop();

            udpConnected = false;

            transformSendTimer = 0f;

            Logger.LogInfo(
                "Network servers stopped."
            );

            networkServer.Start(
                tcpPort.Value
            );

            Logger.LogInfo(
                $"TCP Server Started on port {tcpPort.Value}"
            );

            _ = AcceptClient();

            networkUDPServer.Start(
                udpPort.Value
            );

            Logger.LogInfo(
                $"UDP Server Started on port {udpPort.Value}"
            );

            Logger.LogInfo(
                "Connecting host to local UDP server..."
            );

            try
            {
                networkUDPConnection.Connect(
                    "127.0.0.1",
                    udpPort.Value
                );

                playerReplicator =
                    new PlayerReplicator(
                        networkUDPConnection,
                        playerSprite
                    );

                playerReplicator.Initialize();

                udpConnected = true;

                Logger.LogInfo(
                    "Host UDP player replication started."
                );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Host UDP connection failed: {ex.Message}"
                );

                udpConnected = false;
            }
        }

        private void ConnectToServer()
        {
            if (!networkManager.IsConnected)
            {
                Logger.LogInfo(
                    "Connecting TCP to server..."
                );

                TestConnection();
            }
            else
            {
                Logger.LogInfo(
                    "Already connected to TCP server."
                );
            }

            if (!udpConnected)
            {
                Logger.LogInfo(
                    "Connecting UDP to server..."
                );

                StartUDPClient();
            }
            else
            {
                Logger.LogInfo(
                    "Already connected to UDP server."
                );
            }
        }

        private void UpdatePlayerReplication()
        {
            if (!udpConnected)
                return;

            if (playerReplicator != null)
            {
                playerReplicator.Update();
            }

            transformSendTimer +=
                Time.deltaTime;

            if (transformSendTimer <
                TransformSendRate)
            {
                return;
            }

            transformSendTimer = 0f;

            SendLocalPlayerTransform();
        }

        private async void SendLocalPlayerTransform()
        {
            try
            {
                if (!udpConnected)
                    return;

                playerController localPlayer =
                    UnityEngine.Object.FindObjectOfType<
                        playerController
                    >();

                if (localPlayer == null)
                    return;

                Transform playerTransform =
                    localPlayer.transform;

                float x =
                    playerTransform.position.x;

                float y =
                    playerTransform.position.y;

                float rotation =
                    playerTransform.eulerAngles.z;

                await networkUDPConnection
                    .SendPlayerTransform(
                        x,
                        y,
                        rotation
                    );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Failed to send player transform: {ex.Message}"
                );
            }
        }

        private void OnDestroy()
        {
            playerReplicator?.Destroy();

            networkManager?.Disconnect();

            networkServer?.Stop();

            networkUDPConnection?.Disconnect();

            networkUDPServer?.Stop();
        }

        private void LoadAssets()
        {
            string modPath =
                Path.GetDirectoryName(
                    Info.Location
                );

            string assetPath =
                Path.Combine(
                    modPath,
                    "Orbit_Us-Assets"
                );

            if (!Directory.Exists(assetPath))
            {
                Logger.LogWarning(
                    "Assets Folder Not Found"
                );
            }
            else
            {
                Logger.LogInfo(
                    $"Assets loaded at: {assetPath}"
                );
            }
        }

        private void LoadImage()
        {
            string modPath =
                Path.GetDirectoryName(
                    Info.Location
                );

            string imagePath =
                Path.Combine(
                    modPath,
                    "Orbit_Us-Assets",
                    "Fing.png"
                );

            if (!File.Exists(imagePath))
            {
                Logger.LogWarning(
                    "Image Not Found"
                );

                return;
            }

            byte[] data =
                File.ReadAllBytes(
                    imagePath
                );

            Texture2D tex =
                new Texture2D(
                    2,
                    2
                );

            if (!tex.LoadImage(data))
            {
                Logger.LogError(
                    "Failed to load Fing.png"
                );

                return;
            }

            playerSprite =
                Sprite.Create(
                    tex,
                    new Rect(
                        0,
                        0,
                        tex.width,
                        tex.height
                    ),
                    new Vector2(
                        0.5f,
                        0.5f
                    )
                );

            Logger.LogInfo(
                $"Sprite loaded at: {imagePath}"
            );

            Logger.LogInfo(
                $"Sprite loaded with dimensions: {tex.width}x{tex.height}"
            );
        }
    }
}