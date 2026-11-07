namespace Orbit_Us
{
    using BepInEx;
    using BepInEx.Configuration;
    using System.IO;
    using UnityEngine;
    using System;
    using System.Threading.Tasks;
    using HarmonyLib;

    [BepInPlugin("com.legostarwars9.orbit-us", "Orbit-Us", "0.4.0")]
    public class OrbitUs : BaseUnityPlugin
    {
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
        private bool menuLobby,replicationInitialized;
        private MultiplayerMenu multiplayerMenu;
        private float lobbyStartRetry;
        private RoomTransport room;
        private Task<RoomTransport> roomTask;
        private ConfigEntry<string> matchmakingAddress,matchmakingAccess;
        private ConfigEntry<int> matchmakingPort;
        private ConfigEntry<string> username;
        public string Username
        {
            get=>username.Value;
            set{username.Value=PlayerNames.Normalize(value);if(networkUDPConnection!=null)networkUDPConnection.LocalUsername=username.Value;}
        }
        public RoomTransport Room=>room;
        public bool RoomRequested=>room!=null || roomTask!=null;
        public string MatchmakingEndpoint=>matchmakingAddress.Value+(matchmakingPort.Value==3478?"":":"+matchmakingPort.Value);
        public NetworkUDPConnection Connection=>networkUDPConnection;
        public bool SessionActive=>udpConnected;
        public bool GameplayConnected=>udpConnected && replicationInitialized && !teamGameOverApplied;
        public bool SessionHost=>IsHost;
        public string NetworkStatus {get;private set;}="";
        private bool teamGameOverApplied;
        private bool freshReplayRequired,reloadingAfterWipe;
        public static OrbitUs Instance {get;private set;}

        private float transformSendTimer;

        private const float TransformSendRate = 0.025f;

        private ConfigEntry<string> serverAddress;
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

            networkUDPServer =
                new NetworkUDPServer();

            networkUDPConnection =
                new NetworkUDPConnection();
            networkUDPConnection.LocalUsername=Username;

            multiplayerMenu=new MultiplayerMenu(this,serverAddress.Value,udpPort.Value);
        }

        private void LoadConfig()
        {
            username=Config.Bind("Player","Username","","Your multiplayer display name (up to 24 characters). Blank uses Player ID.");
            matchmakingAddress=Config.Bind("Matchmaking","ServerAddress","orbitstun.legostarwars9.com","IP address or DNS name of your running OrbitUS-P2P-Server, without a URL scheme or port. All players use the same server.");
            matchmakingPort=Config.Bind("Matchmaking","Port",12415,
                new ConfigDescription("UDP port of OrbitUS-P2P-Server. This is separate from the game's direct-connection ports.",new AcceptableValueRange<int>(1,65535)));
            matchmakingAccess=Config.Bind("Matchmaking","AccessKey","","Optional ORBITUS_ACCESS_KEY configured by the server operator.");
            serverAddress =
                Config.Bind(
                    "Network",
                    "ServerAddress",
                    "[Put IP address Here]",
                    "Host IP address or DNS name for Direct IP / VPN and F7 joining. Room codes use Matchmaking.ServerAddress instead."
                );

            udpPort =
                Config.Bind(
                    "Network",
                    "UDPPort",
                    7778,
                    new ConfigDescription("UDP game port used by the host and Direct IP / VPN connections.",new AcceptableValueRange<int>(1,65535))
                );

            Config.Save();
            Logger.LogInfo($"Server settings: {Config.ConfigFilePath}");
            Logger.LogInfo(
                $"Server Address: {serverAddress.Value}"
            );

            Logger.LogInfo(
                $"UDP Port: {udpPort.Value}"
            );
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
                    room!=null?"127.0.0.1":serverAddress.Value,
                    room!=null?room.LocalPort:udpPort.Value
                );

                if(!menuLobby)InitializeReplication();

                udpConnected = true;
                NetworkStatus="Connecting...";
            }
            catch (Exception ex)
            {
                NetworkStatus=ex.Message;
                Logger.LogError($"UDP connection failed: {ex.Message}");
                LeaveSession();
            }
        }

        private void InitializeReplication()
        {
            if(replicationInitialized)return;
            playerReplicator=new PlayerReplicator(networkUDPConnection,playerSprite);
            enemyReplicator=new EnemyReplicator(networkUDPConnection,IsHost);
            enemyReplicator.Initialize();
            planetReplicator=new PlanetReplicator(networkUDPConnection,IsHost);
            planetReplicator.Initialize();
            progressReplicator=new ProgressReplicator(networkUDPConnection,IsHost);
            progressReplicator.Initialize();
            combatReplicator=new CombatReplicator(networkUDPConnection,IsHost);
            combatReplicator.Initialize();
            playerReplicator.Initialize();
            playerTracker=new PlayerTracker(networkUDPConnection,arrowSprite);
            playerTracker.Initialize();
            replicationInitialized=true;
        }

        private void Update()
        {
            UpdateRoom();
            multiplayerMenu?.Update();
            // Let the wipe notification finish before starting another connection.
            if(teamGameOverApplied && udpConnected){UpdatePlayerReplication();return;}
            if (Input.GetKeyDown(KeyCode.F6))
            {
                CloseRoom();menuLobby=false;
                networkUDPServer.LoopbackClientsOnly=false;
                RestartServers();
            }

            if (Input.GetKeyDown(KeyCode.F7) && !RoomRequested)
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
                "Restarting UDP server..."
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

            networkUDPConnection.Disconnect();

            networkUDPServer.Stop();

            udpConnected = false;
            replicationInitialized=false;

            transformSendTimer = 0f;

            Logger.LogInfo(
                "UDP server stopped."
            );

            try
            {
                networkUDPServer.Start(udpPort.Value);
                networkUDPConnection.HostConnection = true;
                networkUDPConnection.Connect(
                    "127.0.0.1",
                    udpPort.Value
                );

                if(!menuLobby)InitializeReplication();

                udpConnected = true;
                NetworkStatus="Hosting";

                Logger.LogInfo($"Hosting on UDP {udpPort.Value}; UDP keepalives enabled.");
            }
            catch (Exception ex)
            {
                NetworkStatus=ex.Message;
                Logger.LogError(
                    $"Host UDP connection failed: {ex.Message}"
                );

                LeaveSession();
            }
        }
        

        private void ConnectToServer()
        {
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

        public void HostLobby()
        {
            if(udpConnected)return;
            menuLobby=true;RestartServers();
        }

        public void HostDirectLobby(int port)
        {networkUDPServer.LoopbackClientsOnly=false;udpPort.Value=port;HostLobby();}

        public void BeginCodeRoom(string endpoint,string code,bool host)
        {
            if(udpConnected || RoomRequested)return;
            try
            {
                endpoint=endpoint.Trim();int port=3478;
                int colon=endpoint.LastIndexOf(':');
                if(colon>=0){port=int.Parse(endpoint.Substring(colon+1));endpoint=endpoint.Substring(0,colon);}
                if(endpoint.Length==0 || port<1 || port>65535)throw new ArgumentException("Enter the matchmaking server address and port.");
                if(!host)code=RoomProtocol.NormalizeCode(code);
                matchmakingAddress.Value=endpoint;matchmakingPort.Value=port;
                if(host){networkUDPServer.LoopbackClientsOnly=true;HostLobby();if(!udpConnected)return;}
                roomTask=RoomTransport.CreateAsync(endpoint,port,host,code,matchmakingAccess.Value,udpPort.Value);
                NetworkStatus="Contacting matchmaking server...";
            }
            catch(Exception ex){NetworkStatus=ex.Message;}
        }
        private void UpdateRoom()
        {
            if(roomTask!=null && roomTask.IsCompleted)
            {
                var completed=roomTask;roomTask=null;
                if(completed.IsFaulted){NetworkStatus=completed.Exception.GetBaseException().Message;LeaveSession();return;}
                room=completed.Result;
                room.LocalUsername=Username;
            }
            if(room==null)return;
            try{room.Tick();}
            catch(Exception ex){NetworkStatus=ex.Message;LeaveSession();return;}
            if(room.EndedReason.Length>0)
            {
                bool gameplay=GameplayConnected;NetworkStatus=room.EndedReason;LeaveSession();
                if(gameplay)ReturnToMenu();return;
            }
            if(!room.IsHost && room.Accepted && !udpConnected){menuLobby=true;StartUDPClient();}
        }
        private void CloseRoom()
        {
            room?.Dispose();room=null;
            if(roomTask!=null)
            {
                _=roomTask.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();else{var ignored=t.Exception;}},TaskScheduler.Default);
                roomTask=null;
            }
        }
        private void ReturnToMenu()
        {
            var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(scene.buildIndex);
        }

        public void JoinLobby(string address,int port)
        {
            if(udpConnected)return;
            serverAddress.Value=address.Trim();udpPort.Value=port;
            menuLobby=true;StartUDPClient();
        }

        public void StartLobbyGame()
        {
            if(!udpConnected || networkUDPConnection.LocalPlayerId<=0 || replicationInitialized)return;
            if(!IsHost && !networkUDPConnection.LobbyStarted)return;
            var start=UnityEngine.Object.FindObjectOfType<startManager>(true);
            if(start==null){NetworkStatus="Game menu is not ready.";return;}
            if(IsHost)_=networkUDPConnection.SetLobbyStarted();
            start.StartGameQuick();
        }

        public bool CanStartGame()=>RoomRequested && room?.IsHost!=true && !networkUDPConnection.LobbyStarted?false:
            !udpConnected || IsHost || networkUDPConnection.LobbyStarted;
        public void OnGameStarted()
        {
            if(!udpConnected || replicationInitialized)return;
            try
            {
                if(IsHost)_=networkUDPConnection.SetLobbyStarted();
                InitializeReplication();menuLobby=false;
            }
            catch(Exception ex){NetworkStatus=ex.Message;Logger.LogError(ex);LeaveSession();}
        }

        public void LeaveSession()
        {
            CloseRoom();
            combatReplicator?.Destroy();combatReplicator=null;
            progressReplicator?.Destroy();progressReplicator=null;
            planetReplicator?.Destroy();planetReplicator=null;
            enemyReplicator?.Destroy();enemyReplicator=null;
            playerTracker?.Destroy();playerTracker=null;
            playerReplicator?.Destroy();playerReplicator=null;
            networkUDPConnection?.Disconnect();
            networkUDPServer?.Stop();
            udpConnected=false;replicationInitialized=false;menuLobby=false;
            MultiplayerActive=false;IsHost=false;transformSendTimer=0;lobbyStartRetry=0;
        }

        private void UpdatePlayerReplication()
        {
            if (!udpConnected)
                return;

            networkUDPConnection.DispatchPendingPackets();
            if(playerReplicator?.RunEnded==true){UpdateTeamGameOver();return;}
            if (!networkUDPConnection.IsConnected)
            {
                bool gameplay=replicationInitialized;
                NetworkStatus=networkUDPConnection.DisconnectReason;
                LeaveSession();
                if(gameplay && NetworkStatus.Length>0)ReturnToMenu();
                return;
            }
            if(menuLobby && !replicationInitialized)
            {
                if(networkUDPConnection.LobbyStarted)StartLobbyGame();
                return;
            }
            if(IsHost && !networkUDPConnection.LobbyStarted)
            {
                lobbyStartRetry+=Time.unscaledDeltaTime;
                if(lobbyStartRetry>=0.5f){lobbyStartRetry=0;_=networkUDPConnection.SetLobbyStarted();}
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
            networkUDPConnection.Disconnect();
            networkUDPServer.Stop();
            CloseRoom();
            udpConnected=false;replicationInitialized=false;menuLobby=false;teamGameOverApplied=false;transformSendTimer=0;
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
            multiplayerMenu?.Draw();
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
            CloseRoom();
            multiplayerMenu?.Dispose();
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
