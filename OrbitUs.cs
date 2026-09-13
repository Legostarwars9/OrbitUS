namespace Orbit_Us
{
    using BepInEx;
    using System.IO;
    using UnityEngine;
    using UnityEngine.UI;
    using System.Net.Sockets;
    using System.Text;
    using System;
    using System.Threading.Tasks;

    [BepInPlugin("Orbit-Us.test", "Orbit-Us", "0.0.7")]
    public class OrbitUs : BaseUnityPlugin
    {
        private NetworkServer networkServer;
        private NetworkManager networkManager;

        private NetworkUDPServer networkUDPServer;
        private NetworkUDPConnection networkUDPConnection;

        private PlayerReplicator playerReplicator;

        private Sprite playerSprite;

        private void Awake()
        {
            Application.runInBackground = true;

            Logger.LogInfo("Orbit Us Loaded");

            LoadAssets();
            LoadImage();

            networkManager = new NetworkManager();
            networkServer = new NetworkServer();

            networkUDPServer = new NetworkUDPServer();
            networkUDPConnection = new NetworkUDPConnection();

            networkServer.OnClientConnected += ClientConnected;
        }

        private void ClientConnected(TcpClient client)
        {
            Logger.LogInfo("Client connected!");
        }

        private async Task AcceptClient()
        {
            try
            {
                await networkServer.AcceptConnection();

                Logger.LogInfo(
                    "Finished handling client connection."
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
                        "jacob-bazzite.tail1da60c.ts.net",
                        7777
                    );

                Logger.LogInfo(
                    "Connected to server!"
                );

                Logger.LogInfo(
                    $"Received packet type: {packet.Type}"
                );

                string message =
                    Encoding.UTF8.GetString(packet.Data);

                Logger.LogInfo(
                    $"Packet data: {message}"
                );

                networkManager.StartKeepAlive();

                Logger.LogInfo(
                    "KeepAlive monitoring started."
                );

                networkUDPConnection.Connect(
                    "jacob-bazzite.tail1da60c.ts.net",
                    7778
                );

                Logger.LogInfo(
                    "UDP connection established."
                );

                playerReplicator =
                    new PlayerReplicator(
                        networkUDPConnection,
                        playerSprite
                    );

                playerReplicator.Initialize();

                Logger.LogInfo(
                    "Player replication started."
                );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Connection failed: {ex.Message}"
                );

                networkUDPConnection?.Disconnect();
                networkManager.Disconnect();
            }
        }
        private void StartHostReplication()
        {
            try
            {
                networkUDPConnection.Connect(
                    "127.0.0.1",
                    7778
                );

                Logger.LogInfo(
                    "Host UDP connection established."
                );

                playerReplicator =
                    new PlayerReplicator(
                        networkUDPConnection,
                        playerSprite
                    );

                playerReplicator.Initialize();

                Logger.LogInfo(
                    "Host player replication started."
                );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Host UDP connection failed: {ex.Message}"
                );
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                networkServer.Stop();
                networkUDPServer.Stop();

                Logger.LogInfo(
                    "Network server stopped."
                );

                networkServer.Start(7777);

                Logger.LogInfo(
                    "Network Server Started on port 7777"
                );

                networkUDPServer.Start(7778);

                Logger.LogInfo(
                    "UDP Server Started on port 7778"
                );

                _ = AcceptClient();
                StartHostReplication();
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                if (!networkManager.IsConnected)
                {
                    Logger.LogInfo(
                        "Connecting to server..."
                    );

                    TestConnection();
                }
                else
                {
                    Logger.LogInfo(
                        "Already connected to server."
                    );
                }
            }

            playerReplicator?.Update();
        }

        private void OnDestroy()
        {
            playerReplicator?.Destroy();

            networkManager?.Disconnect();

            networkUDPConnection?.Disconnect();

            networkServer?.Stop();

            networkUDPServer?.Stop();
        }

        private void LoadAssets()
        {
            string modPath =
                Path.GetDirectoryName(Info.Location);

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
                Path.GetDirectoryName(Info.Location);

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
                File.ReadAllBytes(imagePath);

            Texture2D tex =
                new Texture2D(2, 2);

            tex.LoadImage(data);

            playerSprite = Sprite.Create(
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
                $"Sprite loaded with dimensions: " +
                $"{tex.width}x{tex.height}"
            );
        }
    }
}