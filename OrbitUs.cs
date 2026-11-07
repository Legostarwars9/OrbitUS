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
    using HarmonyLib;

    [BepInPlugin("com.legostarwars9.orbit-us", "Orbit-Us", "0.4.0")]
    public class OrbitUs : BaseUnityPlugin
    {
        private NetworkServer networkServer;
        private NetworkManager networkManager;

        private NetworkUDPServer networkUDPServer;
        private NetworkUDPConnection networkUDPConnection;

        private PlayerReplicator playerReplicator;
        private PlayerTracker playerTracker;
        private EnemyReplicator enemyReplicator;
        private PlanetReplicator planetReplicator;
        private ProgressReplicator progressReplicator;
        private CombatReplicator combatReplicator;

        private Sprite playerSprite;
        private Sprite arrowSprite;
        private TeleportSound teleportSound;

        private bool udpConnected;
        private bool teamGameOverApplied;
        private bool freshReplayRequired,reloadingAfterWipe;
        public static OrbitUs Instance {get;private set;}

        private float transformSendTimer;

        private const float TransformSendRate = 0.025f;

        private ConfigEntry<string> serverAddress;
        private ConfigEntry<int> tcpPort;
        private ConfigEntry<int> udpPort;
        internal static bool MultiplayerActive { get; private set; }
        internal static bool IsHost { get; private set; }

        private Harmony harmony;

        private void Awake()
        {
            Instance=this;
            Application.runInBackground = true;
            harmony = new Harmony("Orbit-Us.enemy-spawn-control");
            harmony.PatchAll();
            Logger.LogInfo(
                "Orbit Us Loaded"
            );
            GameObject dumpObject =
                new GameObject("OrbitUs_EnemyPrefabDump");

            dumpObject.AddComponent<EnemyPrefabDump>();
            LoadConfig();
            
            LoadAssets();
            LoadImage();
            LoadArrowImage();
            teleportSound=gameObject.AddComponent<TeleportSound>();
            teleportSound.Initialize(Path.Combine(Path.GetDirectoryName(Info.Location),"Orbit_Us-Assets","teleport.wav"));

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
            MultiplayerActive = true;
            IsHost = false;
            try
            {
                Logger.LogInfo(
                    $"Connecting UDP to {serverAddress.Value}:{udpPort.Value}..."
                );

                networkUDPConnection.HostConnection = false;
                networkUDPConnection.Connect(
                    serverAddress.Value,
                    udpPort.Value
                );

                playerReplicator =
                    new PlayerReplicator(
                        networkUDPConnection,
                        playerSprite
                    );
                enemyReplicator =
                    new EnemyReplicator(
                        networkUDPConnection,
                        false
                    );

                enemyReplicator.Initialize();
                planetReplicator = new PlanetReplicator(networkUDPConnection, false);
                planetReplicator.Initialize();
                progressReplicator = new ProgressReplicator(networkUDPConnection, IsHost);
                progressReplicator.Initialize();
                combatReplicator=new CombatReplicator(networkUDPConnection,IsHost);
                combatReplicator.Initialize();

                playerReplicator.Initialize();

                playerTracker =
                    new PlayerTracker(
                        networkUDPConnection,
                        arrowSprite
                    );

                playerTracker.Initialize();

                udpConnected = true;

                Logger.LogInfo(
                    "UDP player replication started."
                );

                Logger.LogInfo(
                    "Player tracker started."
                );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"UDP connection failed: {ex.Message}"
                );

                combatReplicator?.Destroy();
                combatReplicator = null;
                progressReplicator?.Destroy();
                progressReplicator = null;
                planetReplicator?.Destroy();
                planetReplicator = null;
                enemyReplicator?.Destroy();
                playerReplicator?.Destroy();
                playerTracker?.Destroy();
                networkUDPConnection.Disconnect();
                MultiplayerActive = false;
                IsHost = false;
                udpConnected = false;
            }
        }

        private void Update()
        {
            // Let the wipe notification finish before starting another connection.
            if(teamGameOverApplied && udpConnected){UpdatePlayerReplication();return;}
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
            MultiplayerActive = true;
            IsHost = true;
            Logger.LogInfo(
                "Restarting network servers..."
            );

            playerTracker?.Destroy();

            playerTracker = null;

            playerReplicator?.Destroy();

            playerReplicator = null;
            
            combatReplicator?.Destroy();
            combatReplicator = null;
            progressReplicator?.Destroy();
            progressReplicator = null;
            planetReplicator?.Destroy();
            planetReplicator = null;
            enemyReplicator?.Destroy();

            enemyReplicator = null;

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
                networkUDPConnection.HostConnection = true;
                networkUDPConnection.Connect(
                    "127.0.0.1",
                    udpPort.Value
                );

                playerReplicator =
                    new PlayerReplicator(
                        networkUDPConnection,
                        playerSprite
                    );
                enemyReplicator =
                    new EnemyReplicator(
                        networkUDPConnection,
                        true
                    );

                enemyReplicator.Initialize();
                planetReplicator = new PlanetReplicator(networkUDPConnection, true);
                planetReplicator.Initialize();
                progressReplicator = new ProgressReplicator(networkUDPConnection, IsHost);
                progressReplicator.Initialize();
                combatReplicator=new CombatReplicator(networkUDPConnection,IsHost);
                combatReplicator.Initialize();
                
                playerReplicator.Initialize();

                playerTracker =
                    new PlayerTracker(
                        networkUDPConnection,
                        arrowSprite
                    );

                playerTracker.Initialize();

                udpConnected = true;

                Logger.LogInfo(
                    "Host UDP player replication started."
                );

                Logger.LogInfo(
                    "Host player tracker started."
                );
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Host UDP connection failed: {ex.Message}"
                );

                combatReplicator?.Destroy();
                combatReplicator = null;
                progressReplicator?.Destroy();
                progressReplicator = null;
                planetReplicator?.Destroy();
                planetReplicator = null;
                enemyReplicator?.Destroy();
                playerReplicator?.Destroy();
                playerTracker?.Destroy();
                networkUDPConnection.Disconnect();
                networkUDPServer.Stop();
                networkServer.Stop();
                MultiplayerActive = false;
                IsHost = false;
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

            networkUDPConnection.DispatchPendingPackets();
            if(playerReplicator?.RunEnded==true){UpdateTeamGameOver();return;}
            if (!networkUDPConnection.IsConnected)
            {
                playerReplicator?.Destroy();
                playerReplicator = null;
                playerTracker?.Destroy();
                playerTracker = null;
                combatReplicator?.Destroy();
                combatReplicator = null;
                progressReplicator?.Destroy();
                progressReplicator = null;
                planetReplicator?.Destroy();
                planetReplicator = null;
                enemyReplicator?.Destroy();
                enemyReplicator = null;
                udpConnected = false;
                MultiplayerActive = false;
                IsHost = false;
                return;
            }

            if (playerReplicator != null)
            {
                playerReplicator.Update();
            }
            if(playerReplicator?.RunEnded==true){UpdateTeamGameOver();return;}
            

            if (playerTracker != null)
            {
                playerTracker.Update();
            }
            planetReplicator?.Update();
            progressReplicator?.Update();
            combatReplicator?.Update();
            if (enemyReplicator != null)
            {
                enemyReplicator.Update();
            }

            transformSendTimer +=
                Time.unscaledDeltaTime;

            if (transformSendTimer <
                TransformSendRate)
            {
                return;
            }

            transformSendTimer = 0f;

            SendLocalPlayerTransform();
        }

        private void UpdateTeamGameOver()
        {
            if(!teamGameOverApplied)
            {
                teamGameOverApplied=true;
                freshReplayRequired=true;
                MultiplayerActive=false;IsHost=false;
                playerReplicator.Life.EndRun();
                playerTracker?.Destroy();playerTracker=null;
                combatReplicator?.Destroy();combatReplicator=null;
                progressReplicator?.Destroy();progressReplicator=null;
                enemyReplicator?.Destroy();enemyReplicator=null;
                planetReplicator?.Destroy();planetReplicator=null;
                foreach(var ending in UnityEngine.Object.FindObjectsOfType<singularityManager>(true))
                {
                    ending.StopAllCoroutines();ending.enabled=false;
                    if(ending.badEndingScreen!=null)ending.badEndingScreen.SetActive(false);
                    if(ending.finalQuitButton!=null)ending.finalQuitButton.SetActive(false);
                    if(ending.endingDialogue!=null)ending.endingDialogue.gameObject.SetActive(false);
                    if(ending.badEndingDialogue!=null)ending.badEndingDialogue.gameObject.SetActive(false);
                }
                foreach(var warp in UnityEngine.Object.FindObjectsOfType<devourerWarper>(true))
                {
                    warp.StopAllCoroutines();warp.warped=false;
                    if(warp.blackHoleScreen!=null)warp.blackHoleScreen.SetActive(false);
                    if(warp.blackHole!=null)warp.blackHole.playerInside=false;
                }
                var manager=gameManager.Instance;
                if(manager!=null){manager.ResetEverything();manager.StopAllCoroutines();}
                var clock=UnityEngine.Object.FindObjectOfType<timeManager>(true);
                if(clock!=null){clock.isPaused=false;clock.sloMo=false;clock.playerTimeWarping=false;}
                foreach(var menu in UnityEngine.Object.FindObjectsOfType<menuManager>(true))
                    foreach(string name in new[]{"pauseMenu","settingsMenu"})
                    {
                        var panel=BuildFieldSnapshot.Field(typeof(menuManager),name).GetValue(menu) as GameObject;
                        if(panel!=null)panel.SetActive(false);
                    }
                var local=player.Instance;
                if(local!=null)
                {
                    if(local.mapProceeding!=null)local.mapProceeding.enabled=false;
                    local.hp=0;local.dead=true;local.Die();
                }
            }
            // Retain only the connection/acknowledgements until the end notice is delivered.
            if(networkUDPConnection.IsConnected && !playerReplicator.FinishWipeDelivery())return;
            playerReplicator.Destroy();playerReplicator=null;
            networkUDPConnection.Disconnect();networkManager.Disconnect();
            networkUDPServer.Stop();networkServer.Stop();
            udpConnected=false;teamGameOverApplied=false;transformSendTimer=0;
        }

        private async void SendLocalPlayerTransform()
        {
            try
            {
                if (!udpConnected)
                    return;

                if(playerReplicator!=null)await playerReplicator.PublishLocalState();
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"Failed to send player transform: {ex.Message}"
                );
            }
        }

        public bool ReplayAfterTeamWipe()
        {
            if(!freshReplayRequired)return false;
            if(!reloadingAfterWipe){reloadingAfterWipe=true;StartCoroutine(FreshRunAfterWipe());}
            return true;
        }
        private System.Collections.IEnumerator FreshRunAfterWipe()
        {
            while(udpConnected)yield return null;
            var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            yield return UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(scene.buildIndex);
            yield return null;
            freshReplayRequired=false;reloadingAfterWipe=false;
            var start=UnityEngine.Object.FindObjectOfType<startManager>(true);
            if(start!=null)start.StartGameQuick();
        }

        private void OnGUI()
        {
            if(!udpConnected || teamGameOverApplied)return;
            playerReplicator?.DrawNameplates();
            playerReplicator?.DrawRecallCountdown();
            playerReplicator?.DrawSpectator();
        }

        private void LateUpdate()
        {
            if(teamGameOverApplied)return;
            if(udpConnected)combatReplicator?.LateUpdate();
            if(udpConnected)playerReplicator?.LateUpdate();
        }

        private void OnDestroy()
        {
            if(Instance==this)Instance=null;
            if(teleportSound!=null)UnityEngine.Object.Destroy(teleportSound);
            combatReplicator?.Destroy();
            combatReplicator = null;
            progressReplicator?.Destroy();
            progressReplicator = null;
            planetReplicator?.Destroy();
            planetReplicator = null;
            enemyReplicator?.Destroy();
            MultiplayerActive = false;
            IsHost = false;
            playerTracker?.Destroy();

            playerReplicator?.Destroy();

            networkManager?.Disconnect();

            networkServer?.Stop();

            networkUDPConnection?.Disconnect();

            networkUDPServer?.Stop();
            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }
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

        private void LoadArrowImage()
        {
            string modPath =
                Path.GetDirectoryName(
                    Info.Location
                );

            string imagePath =
                Path.Combine(
                    modPath,
                    "Orbit_Us-Assets",
                    "arrow.png"
                );

            if (!File.Exists(imagePath))
            {
                Logger.LogWarning(
                    "arrow.png Not Found"
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
                    "Failed to load arrow.png"
                );

                return;
            }

            arrowSprite =
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
                $"Arrow sprite loaded at: {imagePath}"
            );

            Logger.LogInfo(
                $"Arrow sprite loaded with dimensions: {tex.width}x{tex.height}"
            );
        }
    }
}
