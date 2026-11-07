using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    // Uses Unity's built-in IMGUI, so no asset bundle or scene edits are needed.
    public sealed class MultiplayerMenu : IDisposable
    {
        private readonly OrbitUs mod;
        private string address,port,error="",matchmaking,joinCode="";
        private string username;
        private bool rosterOpen,direct;
        private Vector2 rosterScroll,lobbyScroll,queueScroll;
        private GUIStyle title,muted,barText;
        private readonly Dictionary<int,float> pendingKicks=new Dictionary<int,float>();
        private readonly Dictionary<int,float> nextKick=new Dictionary<int,float>();
        private static readonly FieldInfo MainMenuField=AccessTools.Field(typeof(startManager),"mainMenuScreen");
        private startManager start;
        private float nextMenuLookup;
        public static bool PointerOverPanel {get;private set;}

        public MultiplayerMenu(OrbitUs mod,string address,int port)
        {this.mod=mod;this.address=address;this.port=port.ToString();matchmaking=mod.MatchmakingEndpoint;username=mod.Username;}

        public bool MainMenuVisible
        {
            get
            {
                if(start==null && Time.unscaledTime>=nextMenuLookup)
                {nextMenuLookup=Time.unscaledTime+1;start=UnityEngine.Object.FindObjectOfType<startManager>(true);}
                var panel=start==null?null:MainMenuField.GetValue(start) as GameObject;
                return panel!=null && panel.activeInHierarchy;
            }
        }
        private float Scale=>Mathf.Clamp(Mathf.Min(Screen.width/900f,Screen.height/640f),0.5f,1.5f);
        private Rect MenuRect=>new Rect(Screen.width/Scale-364,24,340,Mathf.Min(570,Screen.height/Scale-48));
        private Rect RosterRect=>new Rect((Screen.width/Scale-680)/2,48,680,Mathf.Min(560,Screen.height/Scale-96));

        public void Update()
        {
            if(mod.GameplayConnected && Input.GetKeyDown(KeyCode.BackQuote))rosterOpen=!rosterOpen;
            if(!mod.GameplayConnected)rosterOpen=false;
            Vector2 mouse=new Vector2(Input.mousePosition.x,Screen.height-Input.mousePosition.y)/Scale;
            PointerOverPanel=MainMenuVisible?MenuRect.Contains(mouse):mod.GameplayConnected && rosterOpen && RosterRect.Contains(mouse);
            foreach(int id in new List<int>(pendingKicks.Keys))
            {
                if(!mod.SessionActive || !mod.SessionHost || !mod.Connection.GetRemotePlayerIds().Contains(id) || Time.unscaledTime>pendingKicks[id]+5)
                {pendingKicks.Remove(id);nextKick.Remove(id);continue;}
                // Retrying an authenticated command is safe; the server removes the ID once.
                if(Time.unscaledTime<nextKick[id])continue;
                nextKick[id]=Time.unscaledTime+0.5f;
                _=mod.Connection.KickPlayer(id);
            }
        }
        private void Styles()
        {
            if(title!=null)return;
            title=new GUIStyle(GUI.skin.label) {fontSize=19,fontStyle=FontStyle.Bold};
            muted=new GUIStyle(GUI.skin.label) {wordWrap=true,fontSize=12};muted.normal.textColor=new Color(.72f,.76f,.8f);
            barText=new GUIStyle(GUI.skin.label) {alignment=TextAnchor.MiddleCenter,fontSize=11};
        }
        public void Draw()
        {
            bool menu=MainMenuVisible;
            if(!menu && (!mod.GameplayConnected || !rosterOpen))return;
            Styles();
            Matrix4x4 matrix=GUI.matrix;Color color=GUI.color;int depth=GUI.depth;
            try
            {
                GUI.depth=-1000;GUI.matrix=Matrix4x4.Scale(new Vector3(Scale,Scale,1));
                if(menu)DrawLobby();else DrawRoster();
            }
            finally{GUI.matrix=matrix;GUI.color=color;GUI.depth=depth;}
        }
        private void DrawLobby()
        {
            Rect panel=MenuRect;GUI.Box(panel,GUIContent.none);
            GUILayout.BeginArea(new Rect(panel.x+16,panel.y+12,panel.width-32,panel.height-24));
            GUILayout.Label("Orbit Us",title);
            if(!mod.SessionActive && !mod.RoomRequested)
            {
                GUILayout.Label("Multiplayer",muted);GUILayout.Space(8);
                GUILayout.Label("Username");username=GUILayout.TextField(username,PlayerNames.MaxLength);
                GUILayout.Label("Matchmaking server");matchmaking=GUILayout.TextField(matchmaking,253);
                GUILayout.Label("Room code");joinCode=GUILayout.TextField(joinCode,11).ToUpperInvariant();
                GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("Create room",GUILayout.Height(30))){error="";mod.Username=username;mod.BeginCodeRoom(matchmaking,"",true);}
                if(GUILayout.Button("Join code",GUILayout.Height(30))){error="";mod.Username=username;mod.BeginCodeRoom(matchmaking,joinCode,false);}
                GUILayout.EndHorizontal();GUILayout.Space(10);
                direct=GUILayout.Toggle(direct,"Direct IP / VPN");
                if(direct)
                {
                    GUILayout.Label("Host address");address=GUILayout.TextField(address,253);
                    GUILayout.BeginHorizontal();GUILayout.Label("UDP port",GUILayout.Width(70));port=GUILayout.TextField(port,5);GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if(GUILayout.Button("Host directly"))
                    {
                        if(int.TryParse(port,out int parsed) && parsed>0 && parsed<=65535){error="";mod.Username=username;mod.HostDirectLobby(parsed);}
                        else error="Enter a valid UDP port.";
                    }
                    if(GUILayout.Button("Join IP"))
                    {
                        if(string.IsNullOrWhiteSpace(address) || address.Contains("[") || !int.TryParse(port,out int parsed) || parsed<1 || parsed>65535)
                            error="Enter the host address and a valid UDP port.";
                        else{error="";mod.Username=username;mod.JoinLobby(address,parsed);}
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(10);
                GUILayout.Label(error.Length>0?error:mod.NetworkStatus,muted);
            }
            else
            {
                var connection=mod.Connection;
                if(mod.RoomRequested)
                {
                    string code=mod.Room?.Code??"";
                    if(code.Length==10)
                    {
                        GUILayout.BeginHorizontal();GUILayout.Label(code.Insert(5,"-"),title);
                        if(GUILayout.Button("Copy",GUILayout.Width(50)))GUIUtility.systemCopyBuffer=code.Insert(5,"-");GUILayout.EndHorizontal();
                    }
                    GUILayout.Label(mod.Room?.Status??mod.NetworkStatus,muted);
                    DrawQueue();
                }
                else GUILayout.Label(connection.LocalPlayerId>0?(mod.SessionHost?"Hosting lobby":"Joined lobby"):"Connecting...",muted);
                GUILayout.Space(8);GUILayout.Label("Joined players",title);
                lobbyScroll=GUILayout.BeginScrollView(lobbyScroll,GUILayout.MaxHeight(120));
                foreach(int id in PlayerIds())
                {
                    GUILayout.BeginHorizontal();GUILayout.Label(Label(id));
                    GUILayout.Label(Ping(id),GUILayout.Width(64));
                    if(mod.SessionHost && id!=connection.LocalPlayerId && GUILayout.Button("Kick",GUILayout.Width(45)))Kick(id);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();GUILayout.Space(10);
                if(mod.SessionHost)
                {
                    bool enabled=GUI.enabled;GUI.enabled=connection.LocalPlayerId>0;
                    if(GUILayout.Button("Start game",GUILayout.Height(32)))mod.StartLobbyGame();
                    GUI.enabled=enabled;
                }
                else GUILayout.Label("Waiting for the host to start. You will join automatically.",muted);
                if(GUILayout.Button(mod.SessionHost?"Close lobby":"Leave / cancel"))mod.LeaveSession();
            }
            GUILayout.EndArea();
        }
        private void DrawQueue()
        {
            if(mod.Room==null || !mod.Room.IsHost)return;
            GUILayout.Space(6);GUILayout.Label("Join queue",muted);
            queueScroll=GUILayout.BeginScrollView(queueScroll,GUILayout.MaxHeight(100));
            var peers=mod.Room.GetPeers();bool found=false;
            foreach(var peer in peers)
            {
                if(peer.PlayerId>0 || peer.State=="Declined")continue;found=true;
                GUILayout.BeginHorizontal();GUILayout.Label(peer.Name+" • "+peer.State,muted);
                if(peer.Queued)
                {
                    if(GUILayout.Button("Accept",GUILayout.Width(52)))mod.Room.Approve(peer.Id);
                    if(GUILayout.Button("Decline",GUILayout.Width(55)))mod.Room.Decline(peer.Id);
                }
                GUILayout.EndHorizontal();
            }
            if(!found)GUILayout.Label("No pending requests",muted);
            GUILayout.EndScrollView();
        }
        private List<int> PlayerIds()
        {
            var ids=mod.Connection.GetRemotePlayerIds();
            if(mod.Connection.LocalPlayerId>0)ids.Add(mod.Connection.LocalPlayerId);
            ids.Sort();return ids;
        }
        private string Label(int id)=>mod.Connection.GetPlayerName(id)+(id==mod.Connection.LocalPlayerId?" (You)":"")+
            (id==mod.Connection.HostPlayerId || (mod.SessionHost && id==mod.Connection.LocalPlayerId)?" • Host":"");
        private string Ping(int id)=>mod.Connection.GetPlayerPing(id)?.ToString()+ (mod.Connection.GetPlayerPing(id).HasValue?" ms":"—");
        private void Kick(int id)
        {if(!pendingKicks.ContainsKey(id)){pendingKicks[id]=Time.unscaledTime;nextKick[id]=Time.unscaledTime+0.5f;_=mod.Connection.KickPlayer(id);}}

        private void DrawRoster()
        {
            Rect panel=RosterRect;GUI.Box(panel,GUIContent.none);
            GUI.Label(new Rect(panel.x+18,panel.y+12,420,30),"Connected players",title);
            if(GUI.Button(new Rect(panel.xMax-76,panel.y+12,58,26),"Close"))rosterOpen=false;
            float x=panel.x+18,y=panel.y+50;
            float queueHeight=mod.Room?.IsHost==true?130:0;
            if(mod.Room?.Code.Length==10)
            {
                if(GUI.Button(new Rect(panel.x+360,panel.y+13,220,25),mod.Room.Code.Insert(5,"-")+"  • Copy"))GUIUtility.systemCopyBuffer=mod.Room.Code.Insert(5,"-");
            }
            GUI.Label(new Rect(x,y,210,24),"Player");GUI.Label(new Rect(x+210,y,65,24),"Ping");
            GUI.Label(new Rect(x+282,y,136,24),"Health");GUI.Label(new Rect(x+428,y,136,24),"Fuel");
            var ids=PlayerIds();
            rosterScroll=GUI.BeginScrollView(new Rect(x,y+28,panel.width-36,panel.height-118-queueHeight),rosterScroll,
                new Rect(0,0,panel.width-56,ids.Count*55));
            for(int i=0;i<ids.Count;i++)
            {
                int id=ids[i];float row=i*55;var state=mod.Connection.GetPlayerState(id);
                GUI.Label(new Rect(0,row,206,24),Label(id));
                GUI.Label(new Rect(0,row+23,200,20),state==null?"Waiting for status":state.HasLifeState && !state.Alive?"Spectating":"Playing",muted);
                GUI.Label(new Rect(210,row+10,65,24),Ping(id));
                Meter(new Rect(282,row+9,136,24),state?.Health??0,state?.MaxHealth??0,state?.HasVitals==true,new Color(.75f,.24f,.28f));
                Meter(new Rect(428,row+9,136,24),state?.Fuel??0,state?.MaxFuel??0,state?.HasVitals==true,new Color(.2f,.65f,.85f));
                if(mod.SessionHost && id!=mod.Connection.LocalPlayerId)
                {
                    bool enabled=GUI.enabled;GUI.enabled=!pendingKicks.ContainsKey(id);
                    if(GUI.Button(new Rect(574,row+9,44,24),pendingKicks.ContainsKey(id)?"...":"Kick"))Kick(id);
                    GUI.enabled=enabled;
                }
            }
            GUI.EndScrollView();
            if(queueHeight>0)
            {
                GUILayout.BeginArea(new Rect(x,panel.yMax-35-queueHeight,panel.width-36,queueHeight));DrawQueue();GUILayout.EndArea();
            }
            GUI.Label(new Rect(x,panel.yMax-35,panel.width-36,24),"` / ~: close   •   Ping to host relay",muted);
        }
        private void Meter(Rect rect,float value,float max,bool known,Color tint)
        {
            Color previous=GUI.color;GUI.color=new Color(.12f,.14f,.17f);GUI.DrawTexture(rect,Texture2D.whiteTexture);
            if(known && max>0)
            {
                GUI.color=tint;GUI.DrawTexture(new Rect(rect.x,rect.y,rect.width*Mathf.Clamp01(value/max),rect.height),Texture2D.whiteTexture);
            }
            GUI.color=previous;GUI.Label(rect,known?Mathf.CeilToInt(value)+" / "+Mathf.CeilToInt(max):"—",barText);
        }
        public void Dispose(){PointerOverPanel=false;pendingKicks.Clear();nextKick.Clear();}
    }

    [HarmonyPatch(typeof(startManager),nameof(startManager.StartGameQuick))]
    internal static class MultiplayerLobbyStartPatch
    {
        private static bool Prefix()=>OrbitUs.Instance==null || OrbitUs.Instance.CanStartGame();
        private static void Postfix(){if(OrbitUs.Instance?.CanStartGame()==true)OrbitUs.Instance.OnGameStarted();}
    }
    [HarmonyPatch(typeof(startManager),nameof(startManager.StartGameWithCutscene))]
    internal static class MultiplayerLobbyCutscenePatch
    {private static bool Prefix()=>OrbitUs.Instance==null || (!OrbitUs.Instance.SessionActive && !OrbitUs.Instance.RoomRequested);}

    [HarmonyPatch(typeof(menuButton),nameof(menuButton.PressQuitToMenu))]
    internal static class MultiplayerQuitToMenuPatch
    {private static void Prefix()=>OrbitUs.Instance?.LeaveSession();}

    [HarmonyPatch(typeof(shooterWithLoadout),nameof(shooterWithLoadout.Shoot))]
    internal static class MultiplayerMenuShootPatch
    {private static bool Prefix()=>!MultiplayerMenu.PointerOverPanel;}
}
