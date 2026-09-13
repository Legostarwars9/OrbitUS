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

    [BepInPlugin("Orbit-Us.test", "Orbit-Us", "0.0.6")]
    public class OrbitUs : BaseUnityPlugin
    {
        private NetworkServer networkServer;
        private NetworkManager networkManager;

        private void Awake()
        {
            Application.runInBackground = true;
            Logger.LogInfo("Orbit Us Loaded");

            LoadAssets();
            LoadImage();

            networkManager = new NetworkManager();
            networkServer = new NetworkServer();

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
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Connection failed: {ex.Message}"
                );

                networkManager.Disconnect();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                networkServer.Stop();

                Logger.LogInfo(
                    "Network server stopped."
                );

                networkServer.Start(7777);

                Logger.LogInfo(
                    "Network Server Started on port 7777"
                );

                _ = AcceptClient();
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
        }

        private void OnDestroy()
        {
            networkManager?.Disconnect();
            networkServer?.Stop();
        }

        void LoadAssets()
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

        void LoadImage()
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

            Sprite sprite =
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

            GameObject canvasObject =
                new GameObject("OrbitUsCanvas");

            Canvas canvas =
                canvasObject.AddComponent<Canvas>();

            canvas.renderMode =
                RenderMode.ScreenSpaceOverlay;

            GameObject imageObject =
                new GameObject("OrbitUsTestImage");

            imageObject.transform.SetParent(
                canvasObject.transform,
                false
            );

            Image image =
                imageObject.AddComponent<Image>();

            image.sprite = sprite;

            RectTransform rectTransform =
                imageObject.GetComponent<RectTransform>();

            rectTransform.sizeDelta =
                new Vector2(
                    tex.width/2,
                    tex.height/2
                );

            rectTransform.anchoredPosition =
                new Vector2(
                    10,
                    10
                );
        }
    }
}