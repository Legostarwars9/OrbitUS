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
        private readonly HostRecallState hostRecall=new HostRecallState();
        private GUIStyle recallStyle;
        private GUISkin recallSkin;
        private readonly PlayerNameplates nameplates=new PlayerNameplates();
        private readonly TaskTeleportQueue taskTeleports=new TaskTeleportQueue();
        private readonly TaskTeleportReceiver taskTeleportReceiver=new TaskTeleportReceiver();
        private float taskRetryTimer,taskRecallGrace;
        public static PlayerReplicator Instance {get;private set;}
        public PlayerLifeController Life {get;private set;}
        public SpectatorRoster Spectators {get;}=new SpectatorRoster();
        private readonly HashSet<int> motionLife=new HashSet<int>();
        private readonly Dictionary<int,Vector2> remoteVelocities=new Dictionary<int,Vector2>();
        private readonly Dictionary<int,MotionInterpolation> playerMotion=new Dictionary<int,MotionInterpolation>();
        private readonly Dictionary<int,RemoteMotionClock> playerClocks=new Dictionary<int,RemoteMotionClock>();
        private readonly TeamWipeState teamWipe=new TeamWipeState();
        private System.Threading.Tasks.Task wipeAck;
        private float wipeRetry;
        private float nextVitalsSend;
        private bool? lastPublishedAlive;
        public bool RunEnded=>teamWipe.Ended;

        private readonly Dictionary<int, GameObject> remotePlayers =
            new Dictionary<int, GameObject>();

        private readonly Dictionary<int, Vector3> targetPositions =
            new Dictionary<int, Vector3>();

        private readonly Dictionary<int, float> targetRotations =
            new Dictionary<int, float>();

        private const float InterpolationSpeed = 15f;

        public PlayerReplicator(
            NetworkUDPConnection connection,
            Sprite playerSprite)
        {
            this.connection = connection;
            this.playerSprite = playerSprite;
            Life=new PlayerLifeController(this);
        }

        public void Initialize()
        {
            Instance=this;
            connection.OnPacketReceived += HandlePacket;

            localPlayerId =
                connection.LocalPlayerId;

            Debug.Log(
                $"[Orbit-Us] Player replication initialized. Local ID: {localPlayerId}"
            );
        }

        private void HandlePacket(
            NetworkPacket packet)
        {
            try
            {
                switch (packet.Type)
                {
                    case PacketType.TeamWipe:
                        if(!connection.HostConnection && packet.Data.Length==4 && BitConverter.ToInt32(packet.Data,0)==1)
                        {
                            if(!teamWipe.Ended){teamWipe.Receive();taskTeleports.Clear();hostRecall.Clear();}
                            wipeAck=connection.SendTeamWipeAck();
                        }
                        break;
                    case PacketType.TeamWipeAck:
                        if(connection.HostConnection && packet.Data.Length==4)teamWipe.Acknowledge(BitConverter.ToInt32(packet.Data,0));
                        break;
                    case PacketType.TaskTeleport:
                        if(connection.HostConnection || RunEnded)break;
                        var command=TaskTeleportData.Deserialize(packet.Data);
                        if(taskTeleportReceiver.TryApply(command,connection.LocalPlayerId,ApplyTaskTeleport))
                            _=connection.SendTaskTeleportAck(command.Sequence);
                        break;
                    case PacketType.TaskTeleportAck:
                        if(connection.HostConnection && packet.Data.Length==8)
                            taskTeleports.Acknowledge(BitConverter.ToInt32(packet.Data,0),BitConverter.ToInt32(packet.Data,4));
                        break;
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
            if (packet.Data == null ||
                packet.Data.Length < 4)
            {
                return;
            }

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

                Debug.Log(
                    $"[Orbit-Us] Local player ID: {localPlayerId}"
                );
            }

            Debug.Log(
                $"[Orbit-Us] Player connected: {playerId}"
            );

            if (playerId == localPlayerId)
            {
                return;
            }

            if(RunEnded){if(connection.HostConnection){teamWipe.Add(playerId);_=connection.SendTeamWipe();}return;}

            CreateRemotePlayer(
                playerId
            );
        }

        private void HandlePlayerDisconnected(
            NetworkPacket packet)
        {
            if (packet.Data == null ||
                packet.Data.Length < 4)
            {
                return;
            }

            int playerId =
                BitConverter.ToInt32(
                    packet.Data,
                    0
                );

            RemoveRemotePlayer(
                playerId
            );
        }

        private void HandlePlayerTransform(
            NetworkPacket packet)
        {
            if (packet.Data == null)
                return;

            if (!PlayerTransformData.IsSupportedLength(packet.Data.Length))
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

            if(!connection.HostConnection)hostRecall.Observe(transform,Time.unscaledTime);
            if(RunEnded)return;
            if(!playerMotion.TryGetValue(transform.PlayerId,out MotionInterpolation motion))
            {motion=new MotionInterpolation();playerMotion.Add(transform.PlayerId,motion);playerClocks.Add(transform.PlayerId,new RemoteMotionClock());}
            double arrived=packet.ReceivedAt>0?packet.ReceivedAt:NetworkTime.Now,stamp=transform.HasSampleTime?transform.SampleTime:arrived;
            playerClocks[transform.PlayerId].Observe(stamp,arrived);
            motion.Add(transform.Sequence,stamp,arrived,new MotionPose {X=transform.X,Y=transform.Y,Rotation=transform.Rotation,
                VX=transform.VX*transform.TimeScale,VY=transform.VY*transform.TimeScale});

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
            remoteVelocities[transform.PlayerId]=new Vector2(transform.VX,transform.VY);
            if(transform.HasLifeState)
            {motionLife.Add(transform.PlayerId);ObserveRemoteLife(transform.PlayerId,transform.Alive);}
            else if(!motionLife.Contains(transform.PlayerId))ObserveRemoteLife(transform.PlayerId,true);
            nameplates.Observe(transform.PlayerId,transform.IsHost);
        }

        private void CreateRemotePlayer(
            int playerId)
        {
            if (playerId == localPlayerId)
                return;

            if (remotePlayers.ContainsKey(
                    playerId))
            {
                return;
            }

            GameObject remotePlayer =
                new GameObject(
                    $"OrbitUs_RemotePlayer_{playerId}"
                );

            SpriteRenderer renderer =
                remotePlayer.AddComponent<SpriteRenderer>();

            renderer.sprite =
                playerSprite;
            nameplates.Register(playerId,renderer);
            remotePlayer.SetActive(false);

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
            hostRecall.Remove(playerId);
            playerMotion.Remove(playerId);playerClocks.Remove(playerId);
            nameplates.Remove(playerId);
            taskTeleports.Remove(playerId);
            Spectators.Remove(playerId);motionLife.Remove(playerId);remoteVelocities.Remove(playerId);
            teamWipe.Acknowledge(playerId);
            GameObject remotePlayer;

            if (remotePlayers.TryGetValue(
                    playerId,
                    out remotePlayer))
            {
                if (remotePlayer != null)
                {
                    remotePlayer.SetActive(false);
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
            Life.Update();
            if(connection.HostConnection && teamWipe.Begin(Life.IsSpectating,connection.GetRemotePlayerIds(),id=>Spectators.ConfirmedDead(id,Time.unscaledTime)))
            {taskTeleports.Clear();hostRecall.Clear();_=connection.SendTeamWipe();return;}
            if(connection.HostConnection)
            {
                taskRetryTimer+=Time.unscaledDeltaTime;
                if(taskRetryTimer>=0.35f)
                {taskRetryTimer=0;foreach(var command in taskTeleports.Pending)_=connection.SendTaskTeleport(command);}
            }
            UpdateHostRecall();
            foreach (
                KeyValuePair<int, GameObject> pair
                in remotePlayers)
            {
                int playerId =
                    pair.Key;

                GameObject player =
                    pair.Value;

                if (player == null)
                    continue;

                if (!targetPositions.ContainsKey(
                        playerId))
                {
                    continue;
                }

                if(playerMotion.TryGetValue(playerId,out MotionInterpolation motion) &&
                    motion.Evaluate(playerClocks[playerId].SourceTime(NetworkTime.Now)-(connection.HostConnection?0:.05),out MotionPose pose))
                {
                    player.transform.position=new Vector3(pose.X,pose.Y,player.transform.position.z);
                    player.transform.rotation=Quaternion.Euler(0,0,pose.Rotation);
                    continue;
                }
                Vector3 targetPosition =
                    targetPositions[
                        playerId
                    ];

                float targetRotation =
                    targetRotations[
                        playerId
                    ];

                player.transform.position =
                    Vector3.Lerp(
                        player.transform.position,
                        targetPosition,
                        InterpolationSpeed *
                        Time.deltaTime
                    );

                Quaternion targetQuaternion =
                    Quaternion.Euler(
                        0f,
                        0f,
                        targetRotation
                    );

                player.transform.rotation =
                    Quaternion.Lerp(
                        player.transform.rotation,
                        targetQuaternion,
                        InterpolationSpeed *
                        Time.deltaTime
                    );
            }
        }

        public void GatherTeam(int anchorId,Vector2 position,Vector2 velocity,TaskTeleportReason reason)
        {
            if(RunEnded || !connection.HostConnection || !connection.IsConnected || connection.LocalPlayerId<=0 || anchorId<=0)return;
            foreach(var command in taskTeleports.Create(connection.LocalPlayerId,anchorId,reason,position.x,position.y,velocity.x,velocity.y,connection.GetRemotePlayerIds()))
                if(command.RecipientId==connection.LocalPlayerId)ApplyTaskTeleport(command);
                else _=connection.SendTaskTeleport(command);
        }

        private bool ApplyTaskTeleport(TaskTeleportData command)
        {
            if(RunEnded)return false;
            var local=global::player.Instance;
            if(local==null || local.rb==null)return false;
            if(local.dead && !Life.IsSpectating)EnterSpectator(local);
            bool revived=Life.Revive(new Vector2(command.X,command.Y),new Vector2(command.VX,command.VY));
            hostRecall.CancelCountdown();
            // Allow the host's newly teleported movement to replace its old pose.
            taskRecallGrace=Time.unscaledTime+1f;
            if(command.AnchorId==connection.LocalPlayerId && !revived){TeleportSound.PlayOnce();return true;}
            local.rb.position=new Vector2(command.X,command.Y);
            local.transform.position=new Vector3(command.X,command.Y,local.transform.position.z);
            local.rb.velocity=new Vector2(command.VX,command.VY);local.rb.WakeUp();
            TeleportSound.PlayOnce();
            _=PublishLocalState();
            return true;
        }

        private void UpdateHostRecall()
        {
            var local=global::player.Instance;
            if(connection.HostConnection || !connection.IsConnected || Time.unscaledTime<taskRecallGrace || local==null || local.dead ||
                !local.gameObject.activeInHierarchy || local.rb==null || hostRecall.TargetPlayerId==connection.LocalPlayerId)
            {hostRecall.CancelCountdown();return;}
            if(!hostRecall.TryRecall(local.rb.position.x,local.rb.position.y,Time.unscaledTime,out PlayerTransformData destination))return;
            local.rb.position=new Vector2(destination.X,destination.Y);
            local.transform.position=new Vector3(destination.X,destination.Y,local.transform.position.z);
            local.rb.velocity=new Vector2(destination.VX,destination.VY);
            local.rb.WakeUp();
            // Distance recall gets one local cue when the teleport actually happens.
            TeleportSound.PlayOnce();
        }

        public void DrawNameplates()
        {
            var local=global::player.Instance;
            if(connection.IsConnected && local!=null && local.gameObject.activeInHierarchy)nameplates.Draw();
        }

        private void ObserveRemoteLife(int id,bool alive)
        {
            Spectators.Observe(id,alive,Time.unscaledTime);
            if(remotePlayers.TryGetValue(id,out GameObject model) && model!=null)model.SetActive(alive);
        }
        public void ObserveCombatLife(int id,bool alive)
        {if(id!=connection.LocalPlayerId && !motionLife.Contains(id))ObserveRemoteLife(id,alive);}
        public bool EnterSpectator(player local)
        {
            if(RunEnded)return false;
            if(!Life.Begin(local))return false;
            _=PublishLocalState();return true;
        }
        public System.Threading.Tasks.Task PublishLocalState()
        {
            var local=global::player.Instance;
            if(!connection.IsConnected || local==null || local.rb==null || !local.gameObject.activeInHierarchy)return System.Threading.Tasks.Task.CompletedTask;
            bool alive=!local.dead && !Life.IsSpectating;
            bool vitals=Time.unscaledTime>=nextVitalsSend || lastPublishedAlive!=alive;
            if(vitals)nextVitalsSend=Time.unscaledTime+.25f;
            lastPublishedAlive=alive;
            Vector2 position=Life.IsSpectating?Life.DeathPosition:local.rb.position;
            Vector2 velocity=alive?local.rb.velocity:Vector2.zero;
            int spectating=connection.HostConnection && !alive && Life.IsSpectating && SpectatorTarget()!=null?Spectators.TargetId:0;
            return connection.SendPlayerState(new PlayerTransformData {X=position.x,Y=position.y,Rotation=local.rb.rotation,
                HasSampleTime=true,SampleTime=NetworkTime.PhysicsSample(Math.Min(Time.fixedDeltaTime,Time.timeAsDouble-Time.fixedTimeAsDouble),Time.timeScale),
                VX=velocity.x,VY=velocity.y,TimeScale=Time.timeScale,HasVelocity=true,HasLifeState=true,Alive=alive,SpectatingPlayerId=spectating,
                HasVitals=vitals,Health=alive?Mathf.Clamp(local.hp,0,local.hpMax):0,MaxHealth=local.hpMax,
                Fuel=Mathf.Clamp(local.fuel,0,local.fuelCap),MaxFuel=local.fuelCap});
        }
        public Transform SpectatorTarget()
        {
            int id=Spectators.EnsureTarget(Time.unscaledTime);
            return remotePlayers.TryGetValue(id,out GameObject model) && model!=null && model.activeInHierarchy?model.transform:null;
        }
        public Vector2 RemoteVelocity(int id)=>remoteVelocities.TryGetValue(id,out Vector2 velocity)?velocity:Vector2.zero;
        public bool TryGetRemotePosition(int id,out Vector3 position)
        {
            position=Vector3.zero;
            if(!remotePlayers.TryGetValue(id,out GameObject model) || model==null || !model.activeInHierarchy)return false;
            position=model.transform.position;return true;
        }
        public string PlayerLabel(int id)=>connection.GetPlayerName(id);
        public void LateUpdate()=>Life.LateUpdate();
        public void DrawSpectator()=>Life.Draw();
        public bool FinishWipeDelivery()
        {
            if(!RunEnded)return false;
            if(!connection.HostConnection)return wipeAck!=null && wipeAck.IsCompleted;
            wipeRetry+=Time.unscaledDeltaTime;
            if(teamWipe.Pending.Count>0 && wipeRetry>=0.35f){wipeRetry=0;_=connection.SendTeamWipe();}
            return teamWipe.Pending.Count==0;
        }

        public void DrawRecallCountdown()
        {
            if(connection.HostConnection || !connection.IsConnected || hostRecall.SecondsRemaining<=0)return;
            GUISkin previousSkin=GUI.skin;Matrix4x4 matrix=GUI.matrix;Color color=GUI.color;
            try
            {
                GUI.skin=GameMenuTheme.Skin;GUI.color=Color.white;
                if(recallSkin!=GUI.skin || recallStyle==null)
                {
                    recallSkin=GUI.skin;
                    recallStyle=new GUIStyle(GUI.skin.label) {fontSize=20,fontStyle=GUI.skin.button.fontStyle,alignment=TextAnchor.MiddleCenter};
                }
                float scale=Mathf.Clamp(Mathf.Min(Screen.width/900f,Screen.height/640f),.5f,1.5f);
                GUI.matrix=Matrix4x4.Scale(new Vector3(scale,scale,1));
                string target=hostRecall.FollowingSpectator?connection.GetPlayerName(hostRecall.TargetPlayerId):"Host";
                string caption="Teleporting to "+target+" in "+hostRecall.SecondsRemaining+"s";
                float width=Mathf.Min(Mathf.Max(360,recallStyle.CalcSize(new GUIContent(caption)).x+24),Screen.width/scale-32);
                Rect panel=new Rect((Screen.width/scale-width)/2f,60,width,48);
                GameMenuTheme.Panel(panel);
                GUI.Label(new Rect(panel.x+12,panel.y,panel.width-24,panel.height),caption,recallStyle);
            }
            finally{GUI.skin=previousSkin;GUI.matrix=matrix;GUI.color=color;}
        }

        public void Destroy()
        {
            Life.Dispose();
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

            localPlayerId = -1;
            hostRecall.Clear();
            nameplates.Clear();
            Spectators.Clear();motionLife.Clear();remoteVelocities.Clear();
            playerMotion.Clear();playerClocks.Clear();
            taskTeleports.Clear();taskTeleportReceiver.Clear();taskRetryTimer=taskRecallGrace=0;
            if(Instance==this)Instance=null;
        }
    }
}
