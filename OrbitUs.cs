namespace Orbit_Us
{
    using BepInEx;
    using System.IO;
    using UnityEngine;
    using UnityEngine.UI;
    using System.Net.Sockets;
    using System.Text;
    [BepInPlugin("Orbit-Us.test", "Orbit-Us", "0.0.2")]
    public class OrbitUs : BaseUnityPlugin
    {
        private NetworkServer networkServer;
        private NetworkManager networkManager;
        private void Awake()
        {
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
        
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                networkServer.Start(7777);
                Logger.LogInfo("Network Server Started on port 7777");
                _ = networkServer.AcceptConnection();
            }
            if (Input.GetKeyDown(KeyCode.F7))
            {
                TestConnection();
                Logger.LogInfo("Connecting to local server...");
            }
        }
        private async void TestConnection()
        {
            NetworkPacket packet = await networkManager.Connect("127.0.0.1", 7777);

            Logger.LogInfo($"Received packet type: {packet.Type}");

            string message = Encoding.UTF8.GetString(packet.Data);

            Logger.LogInfo($"Packet data: {message}");
        }
        void LoadAssets()
        {
            string modPath = Path.GetDirectoryName(Info.Location);
            string assetPath = Path.Combine(modPath, "Orbit_Us-Assets");
           
            if (!Directory.Exists(assetPath))
            {
                Logger.LogWarning("Assets Folder Not Found");
            }
            else
            {
                Logger.LogInfo($"Assets loaded at: {assetPath}");
            }
        }

        void LoadImage()
        {
            string modPath = Path.GetDirectoryName(Info.Location);
            string imagePath = Path.Combine(modPath, "Orbit_Us-Assets", "Fing.png");

            if (!File.Exists(imagePath))
            {
                Logger.LogWarning("Image Not Found");
                return;
            }
            
            byte[] data = File.ReadAllBytes(imagePath);
            
            Texture2D tex = new Texture2D(2, 2);
            tex.LoadImage(data);
            
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            Logger.LogInfo($"Sprite loaded at: {imagePath}");
            Logger.LogInfo($"Sprite loaded with dimentions: {tex.width}x{tex.height}");
            
            //GameObject canvasObject = new GameObject("OrbitUsCanvas");
            //Canvas canvas = canvasObject.AddComponent<Canvas>();
            //canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            //GameObject imageObject = new GameObject("OrbitUsTestImage");
            //imageObject.transform.SetParent(canvasObject.transform, false);
            //Image image = imageObject.AddComponent<Image>();
            //image.sprite = sprite;
            //RectTransform rectTransform = imageObject.GetComponent<RectTransform>();
            //rectTransform.sizeDelta = new Vector2(tex.width, tex.height);
            //rectTransform.anchoredPosition = new Vector2(120, 120);
        }
    }
}