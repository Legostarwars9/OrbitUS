using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;

namespace Orbit_Us
{
    public class NetworkUDPConnection
    {
        private UdpClient udpClient;
        private volatile bool connected;
        private uint transformSequence;
        private int sendingTransform;
        private int sendingPlanetSnapshot;
        private int sendingCombat;
        private int sendingEnemyMotion,sendingPlanetMotion;
        private readonly GameplaySendBudget bulkBudget=new GameplaySendBudget();
        private int sendingProgressSnapshot, sendingProgressRepair;
        private DateTime lastProgressRepair;
        private int sendingPlanetRepair;
        private int sendingEnemyRepair;
        private DateTime lastEnemyRepair;
        private DateTime lastPlanetRepair;
        private PendingPacketBuffer receivedPackets = new PendingPacketBuffer();
        private DateTime lastConnectSent;
        private DateTime lastHeartbeat;
        private long lastServerPacketTicks;
        private int rosterRevision;
        private HashSet<int> activeRoster;
        private readonly HashSet<int> remotePlayers = new HashSet<int>();
        private readonly HashSet<int> departedPlayers = new HashSet<int>();
        private readonly Dictionary<int,string> playerNames=new Dictionary<int,string>();
        public string LocalUsername {get;set;}="";
        public string GetPlayerName(int id)
        {
            string name=id==LocalPlayerId?PlayerNames.Normalize(LocalUsername):playerNames.TryGetValue(id,out string known)?known:"";
            return name.Length>0?name:"Player "+id;
        }
        private readonly Dictionary<int,PlayerTransformData> playerStates=new Dictionary<int,PlayerTransformData>();
        private readonly Dictionary<int,DateTime> stateTimes=new Dictionary<int,DateTime>();
        private readonly Dictionary<int,DateTime> vitalsTimes=new Dictionary<int,DateTime>();
        private readonly Dictionary<int,int> playerPings=new Dictionary<int,int>();
        private readonly Dictionary<int,DateTime> pingTimes=new Dictionary<int,DateTime>();
        public int HostPlayerId {get;private set;}=-1;
        public bool LobbyStarted {get;private set;}
        public string DisconnectReason {get;private set;}="";
        public PlayerTransformData GetPlayerState(int id)=>stateTimes.TryGetValue(id,out DateTime at) &&
            (DateTime.UtcNow-at).TotalSeconds<3 && playerStates.TryGetValue(id,out PlayerTransformData state)?state:null;
        public int? GetPlayerPing(int id)=>pingTimes.TryGetValue(id,out DateTime at) &&
            (DateTime.UtcNow-at).TotalSeconds<5 && playerPings.TryGetValue(id,out int ping)?(int?)ping:null;
        public Task KickPlayer(int id)=>HostConnection && id>0 && id!=LocalPlayerId?
            SendPacket(new NetworkPacket {Type=PacketType.HostKick,Data=BitConverter.GetBytes(id)}):Task.CompletedTask;
        public Task SetLobbyStarted()=>HostConnection?
            SendPacket(new NetworkPacket {Type=PacketType.LobbyState,Data=new byte[]{1}}):Task.CompletedTask;
        public bool IsConnected => connected;
        public bool HostConnection { get; set; }

        public int LocalPlayerId { get; private set; } = -1;

        public event Action<NetworkPacket> OnPacketReceived;
        public List<int> GetRemotePlayerIds()=>new List<int>(remotePlayers);
        public Task SendTaskTeleport(TaskTeleportData command)
            =>HostConnection?SendPacket(new NetworkPacket {Type=PacketType.TaskTeleport,Data=command.Serialize()}):Task.CompletedTask;
        public Task SendTaskTeleportAck(int sequence)
            =>SendPacket(new NetworkPacket {Type=PacketType.TaskTeleportAck,Data=BitConverter.GetBytes(sequence)});
        public Task SendTeamWipe()
            =>HostConnection?SendPacket(new NetworkPacket {Type=PacketType.TeamWipe,Data=BitConverter.GetBytes(1)}):Task.CompletedTask;
        public Task SendTeamWipeAck()=>SendPacket(new NetworkPacket {Type=PacketType.TeamWipeAck,Data=BitConverter.GetBytes(1)});

        public void Connect(
            string address,
            int port)
        {
            Disconnect();
            DisconnectReason="";

            udpClient =
                new UdpClient(0);

            udpClient.Connect(
                address,
                port
            );

            connected = true;
            Interlocked.Exchange(ref lastServerPacketTicks, DateTime.UtcNow.Ticks);

            IPEndPoint localEndpoint =
                (IPEndPoint)
                udpClient.Client.LocalEndPoint;

            Console.WriteLine(
                $"UDP connected to {address}:{port}"
            );

            Console.WriteLine(
                $"UDP local endpoint: {localEndpoint.Address}:{localEndpoint.Port}"
            );

            var socket = udpClient;
            var inbox = receivedPackets;
            _ = Task.Run(() => ReceiveLoop(socket, inbox));

            _ = SendPlayerConnect();
        }
        public async Task SendEnemySnapshot(
            List<EnemyTransformData> enemies,
            int snapshotId)
        {
            UdpClient socket = udpClient;
            if (!connected || socket == null)
                return;

            try
            {
                var chunks=await Task.Run(()=>EnemySnapshotCodec.Encode(enemies,snapshotId)).ConfigureAwait(false);
                foreach (byte[] chunk in chunks)
                {
                    if (!connected || !ReferenceEquals(socket, udpClient)) return;
                    byte[] data = PacketSerializer.Serialize(new NetworkPacket
                    {
                        Type = PacketType.EnemySnapshot, Data = chunk
                    });
                    await bulkBudget.Wait(data.Length).ConfigureAwait(false);
                    if(!connected || !ReferenceEquals(socket,udpClient))return;
                    await socket.SendAsync(data, data.Length).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP enemy snapshot send failed: {ex.Message}");
            }
        }

        public async Task SendPlanetSnapshot(List<PlanetState> planets, int snapshotId)
        {
            UdpClient socket = udpClient;
            if (!connected || socket == null || !HostConnection ||
                Interlocked.CompareExchange(ref sendingPlanetSnapshot, 1, 0) != 0) return;
            try
            {
                // The caller captures Unity state into independent DTOs before
                // this call. Serialization and socket work never use Unity APIs.
                var chunks = await Task.Run(() => PlanetSnapshotCodec.Encode(planets, snapshotId)).ConfigureAwait(false);
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (!connected || !ReferenceEquals(socket, udpClient)) return;
                    byte[] data = PacketSerializer.Serialize(new NetworkPacket
                    {
                        Type = PacketType.PlanetSnapshot, Data = chunks[i]
                    });
                    await bulkBudget.Wait(data.Length).ConfigureAwait(false);
                    if(!connected || !ReferenceEquals(socket,udpClient))return;
                    await socket.SendAsync(data, data.Length).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Console.WriteLine($"UDP planet snapshot send failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref sendingPlanetSnapshot, 0); }
        }

        private async Task RequestPlanetRepair(byte[] request)
        {
            UdpClient socket = udpClient;
            if (!connected || socket == null ||
                Interlocked.CompareExchange(ref sendingPlanetRepair, 1, 0) != 0) return;
            try
            {
                byte[] data = PacketSerializer.Serialize(new NetworkPacket
                {
                    Type = PacketType.PlanetSnapshotRequest, Data = request
                });
                await socket.SendAsync(data, data.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (connected && ReferenceEquals(socket, udpClient))
                    Console.WriteLine($"UDP planet repair request failed: {ex.Message}");
            }
            finally { Interlocked.Exchange(ref sendingPlanetRepair, 0); }
        }

        private async Task RequestEnemyRepair(byte[] request)
        {
            UdpClient socket = udpClient;
            if (!connected || socket == null ||
                Interlocked.CompareExchange(ref sendingEnemyRepair, 1, 0) != 0) return;
            try
            {
                byte[] data = PacketSerializer.Serialize(new NetworkPacket
                {
                    Type = PacketType.EnemySnapshotRequest, Data = request
                });
                await socket.SendAsync(data, data.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (connected && ReferenceEquals(socket, udpClient))
                    Console.WriteLine($"UDP enemy repair request failed: {ex.Message}");
            }
            finally { Interlocked.Exchange(ref sendingEnemyRepair, 0); }
        }

        public async Task SendCombatFrame(CombatFrame frame)
        {
            var socket=udpClient;
            if(!connected || socket==null || LocalPlayerId<=0 || Interlocked.CompareExchange(ref sendingCombat,1,0)!=0)return;
            frame.Owner=LocalPlayerId;frame.Authority=HostConnection;
            try
            {
                var chunks=await Task.Run(()=>CombatSnapshotCodec.Encode(frame,frame.Sequence)).ConfigureAwait(false);
                for(int i=0;i<chunks.Count;i++)
                {
                    if(!connected || !ReferenceEquals(socket,udpClient))return;
                    byte[] bytes=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.CombatSnapshot,Data=chunks[i]});
                    await bulkBudget.Wait(bytes.Length).ConfigureAwait(false);
                    if(!connected || !ReferenceEquals(socket,udpClient))return;
                    await socket.SendAsync(bytes,bytes.Length).ConfigureAwait(false);
                }
            }
            catch(Exception ex){Console.WriteLine("Combat send failed: "+ex.Message);}
            finally{Interlocked.Exchange(ref sendingCombat,0);}
        }
        public Task SendCombatHit(CombatHit hit)=>SendPacket(new NetworkPacket {Type=PacketType.CombatHit,Data=hit.Serialize()});
        public Task SendCombatHitAck(int id)=>SendPacket(new NetworkPacket {Type=PacketType.CombatHitAck,Data=BitConverter.GetBytes(id)});

        public async Task SendProgressSnapshot(ProgressState state, int snapshotId)
        {
            UdpClient socket = udpClient;
            if (!connected || socket == null || !HostConnection ||
                Interlocked.CompareExchange(ref sendingProgressSnapshot, 1, 0) != 0) return;
            try
            {
                // The caller captures Unity state into independent DTOs before
                // this call. Serialization and socket work never use Unity APIs.
                var chunks = await Task.Run(() => ProgressSnapshotCodec.Encode(state, snapshotId)).ConfigureAwait(false);
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (!connected || !ReferenceEquals(socket, udpClient)) return;
                    byte[] data = PacketSerializer.Serialize(new NetworkPacket
                    {
                        Type = PacketType.ProgressSnapshot, Data = chunks[i]
                    });
                    await bulkBudget.Wait(data.Length).ConfigureAwait(false);
                    if(!connected || !ReferenceEquals(socket,udpClient))return;
                    await socket.SendAsync(data, data.Length).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Console.WriteLine($"UDP progress snapshot send failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref sendingProgressSnapshot, 0); }
        }

        private async Task RequestProgressRepair(byte[] request)
        {
            UdpClient socket = udpClient;
            if (!connected || socket == null ||
                Interlocked.CompareExchange(ref sendingProgressRepair, 1, 0) != 0) return;
            try
            {
                byte[] data = PacketSerializer.Serialize(new NetworkPacket
                {
                    Type = PacketType.ProgressSnapshotRequest, Data = request
                });
                await socket.SendAsync(data, data.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (connected && ReferenceEquals(socket, udpClient))
                    Console.WriteLine($"UDP progress repair request failed: {ex.Message}");
            }
            finally { Interlocked.Exchange(ref sendingProgressRepair, 0); }
        }

        public Task SendProgressAction(ProgressAction action) => SendPacket(new NetworkPacket { Type = PacketType.ProgressAction, Data = action.Serialize() });
        public Task SendProgressActionAck(int playerId, int requestId)
        {
            byte[] bytes = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(playerId), 0, bytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(requestId), 0, bytes, 4, 4);
            return SendPacket(new NetworkPacket { Type = PacketType.ProgressActionAck, Data = bytes });
        }

        private async Task SendPlayerConnect()
        {
            try
            {
                if (!connected)
                    return;

                lastConnectSent = DateTime.UtcNow;
                NetworkPacket packet = new NetworkPacket
                {
                    Type = PacketType.PlayerConnect,
                    Data = new byte[] { HostConnection ? (byte)1 : (byte)0 }
                };

                byte[] data =
                    PacketSerializer.Serialize(
                        packet
                    );

                await udpClient.SendAsync(
                    data,
                    data.Length
                );

                Console.WriteLine(
                    "Sent UDP PlayerConnect."
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"UDP PlayerConnect failed: {ex.Message}"
                );
            }
        }

        public Task SendEnemyDamage(int enemyId, float damage)
        {
            // Keep the public entry point on the reliable request path.
            EnemyReplicator.Instance?.SendEnemyDamage(enemyId, damage);
            return Task.CompletedTask;
        }

        public Task SendEnemyDamageRequest(EnemyDamageData request)
        {
            return SendPacket(new NetworkPacket
            {
                Type = PacketType.EnemyDamage, Data = request.Serialize()
            });
        }

        public Task SendEnemyDamageAcknowledgement(int playerId, int requestId)
        {
            byte[] data = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(playerId), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(requestId), 0, data, 4, 4);
            return SendPacket(new NetworkPacket { Type = PacketType.EnemyDamageAck, Data = data });
        }

        private async Task SendPacket(NetworkPacket packet)
        {
            if (!connected) return;
            try
            {
                byte[] data = PacketSerializer.Serialize(packet);
                await udpClient.SendAsync(data, data.Length);
            }
            catch (Exception ex) { Console.WriteLine($"UDP send failed: {ex.Message}"); }
        }

        public Task SendPlayerTransform(float x, float y, float rotation)
            =>SendMovement(new PlayerTransformData {X=x,Y=y,Rotation=rotation});

        public Task SendPlayerMotion(float x,float y,float rotation,float vx,float vy)
            =>SendMovement(new PlayerTransformData {X=x,Y=y,Rotation=rotation,VX=vx,VY=vy,HasVelocity=true});
        public Task SendPlayerState(float x,float y,float rotation,float vx,float vy,bool alive)
            =>SendMovement(new PlayerTransformData {X=x,Y=y,Rotation=rotation,VX=vx,VY=vy,HasVelocity=true,HasLifeState=true,Alive=alive});
        public Task SendPlayerState(PlayerTransformData state)=>SendMovement(state);
        public async Task SendWorldMotion(WorldMotionBatch batch)
        {
            var socket=udpClient;
            if(!connected || socket==null || !HostConnection || batch.Entities.Count==0)return;
            int busy=batch.Planets?Interlocked.CompareExchange(ref sendingPlanetMotion,1,0):Interlocked.CompareExchange(ref sendingEnemyMotion,1,0);
            if(busy!=0)return;
            try
            {
                var packets=await Task.Run(()=>WorldMotionCodec.Encode(batch)).ConfigureAwait(false);
                foreach(var payload in packets)
                {
                    if(!connected || !ReferenceEquals(socket,udpClient))return;
                    byte[] bytes=PacketSerializer.Serialize(new NetworkPacket {Type=batch.Planets?PacketType.PlanetMotion:PacketType.EnemyMotion,Data=payload});
                    await socket.SendAsync(bytes,bytes.Length).ConfigureAwait(false);
                }
            }
            catch(Exception ex){if(connected && ReferenceEquals(socket,udpClient))Console.WriteLine("UDP motion send failed: "+ex.Message);}
            finally{if(batch.Planets)Interlocked.Exchange(ref sendingPlanetMotion,0);else Interlocked.Exchange(ref sendingEnemyMotion,0);}
        }
        public Task SendPlayerVitals(float x,float y,float rotation,float vx,float vy,bool alive,float health,float maxHealth,float fuel,float maxFuel)
            =>SendMovement(new PlayerTransformData {X=x,Y=y,Rotation=rotation,VX=vx,VY=vy,HasVelocity=true,HasLifeState=true,Alive=alive,
                HasVitals=true,Health=health,MaxHealth=maxHealth,Fuel=fuel,MaxFuel=maxFuel});

        private async Task SendMovement(PlayerTransformData transform)
        {
            UdpClient socket = udpClient;
            if (!connected || socket == null || LocalPlayerId == -1 ||
                Interlocked.CompareExchange(ref sendingTransform, 1, 0) != 0) return;
            try
            {
                transform.PlayerId=LocalPlayerId;transform.Sequence=++transformSequence;
                transform.IsHost=HostConnection;
                if(!transform.HasSampleTime){transform.HasSampleTime=true;transform.SampleTime=NetworkTime.Now;}
                if(!HostConnection || !transform.HasLifeState || transform.Alive)transform.SpectatingPlayerId=0;
                byte[] data = PacketSerializer.Serialize(new NetworkPacket
                {
                    Type = PacketType.PlayerTransform, Data = transform.Serialize()
                });
                CachePlayerState(transform);
                await socket.SendAsync(data, data.Length).ConfigureAwait(false);
            }
            catch (Exception ex) { Console.WriteLine($"UDP transform send failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref sendingTransform, 0); }
        }

        private async Task ReceiveLoop(UdpClient socket, PendingPacketBuffer inbox)
        {
            while (connected && ReferenceEquals(socket, udpClient))
            {
                try
                {
                    UdpReceiveResult result = await socket.ReceiveAsync().ConfigureAwait(false);
                    NetworkPacket packet = PacketSerializer.Deserialize(result.Buffer);
                    if (connected && ReferenceEquals(socket, udpClient))
                    {
                        Interlocked.Exchange(ref lastServerPacketTicks, DateTime.UtcNow.Ticks);
                        packet.ReceivedAt=NetworkTime.Now;inbox.Add(packet);
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!connected || !ReferenceEquals(socket, udpClient)) break;
                    Console.WriteLine($"UDP receive failed: {ex.Message}");
                }
            }
        }

        // Called by OrbitUs.Update: Unity objects and replica dictionaries are
        // touched only on the Unity thread, regardless of async continuations.
        public void DispatchPendingPackets()
        {
            if (!connected) return;
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastServerPacketTicks) > TimeSpan.FromSeconds(10).Ticks)
            {
                DisconnectReason="Connection timed out.";
                Disconnect();
                return;
            }
            if (LocalPlayerId > 0 && (DateTime.UtcNow - lastHeartbeat).TotalSeconds >= 1)
            {
                lastHeartbeat = DateTime.UtcNow;
                _ = SendPacket(new NetworkPacket { Type = PacketType.KeepAlive, Data = System.Text.Encoding.UTF8.GetBytes(PlayerNames.Normalize(LocalUsername)) });
            }
            if (LocalPlayerId == -1 && (DateTime.UtcNow - lastConnectSent).TotalSeconds >= 1)
                _ = SendPlayerConnect();

            if (!HostConnection && LocalPlayerId > 0 && (DateTime.UtcNow - lastPlanetRepair).TotalSeconds >= 0.5)
            {
                lastPlanetRepair = DateTime.UtcNow;
                byte[] request = receivedPackets.GetPlanetRepairRequest();
                if (request != null) _ = RequestPlanetRepair(request);
            }

            if (!HostConnection && LocalPlayerId > 0 && (DateTime.UtcNow - lastEnemyRepair).TotalSeconds >= 0.5)
            {
                lastEnemyRepair = DateTime.UtcNow;
                byte[] request = receivedPackets.GetEnemyRepairRequest();
                if (request != null) _ = RequestEnemyRepair(request);
            }

            if (!HostConnection && LocalPlayerId > 0 && (DateTime.UtcNow - lastProgressRepair).TotalSeconds >= 0.5)
            {
                lastProgressRepair = DateTime.UtcNow;
                byte[] request = receivedPackets.GetProgressRepairRequest();
                if (request != null) _ = RequestProgressRepair(request);
            }

            foreach (NetworkPacket packet in receivedPackets.Drain())
            {
                if (packet.Type == PacketType.ServerStopped)
                {
                    DisconnectReason="The host closed the session.";
                    Disconnect();
                    return;
                }
                if(packet.Type==PacketType.PlayerKicked)
                {
                    DisconnectReason="You were kicked by the host.";Disconnect();return;
                }
                if(packet.Type==PacketType.LatencyProbe && packet.Data.Length==8)
                {
                    _=SendPacket(new NetworkPacket {Type=PacketType.LatencyReply,Data=packet.Data});continue;
                }
                if (packet.Type == PacketType.PlayerAssigned && packet.Data.Length == 4 && LocalPlayerId == -1)
                    LocalPlayerId = BitConverter.ToInt32(packet.Data, 0);
                if (LocalPlayerId == -1) continue;
                if(packet.Type==PacketType.PlayerName)
                {
                    if(PlayerNames.TryDecode(packet.Data,out int id,out string name) && !departedPlayers.Contains(id) &&
                        (id==LocalPlayerId || remotePlayers.Contains(id)))playerNames[id]=name;
                    continue;
                }
                if(packet.Type==PacketType.PlayerLatency)
                {
                    if(packet.Data.Length%8!=0 || packet.Data.Length>512)continue;
                    for(int i=0;i<packet.Data.Length;i+=8)
                    {
                        int id=BitConverter.ToInt32(packet.Data,i),ms=BitConverter.ToInt32(packet.Data,i+4);
                        if(id<=0 || ms<0 || ms>10000 || (id!=LocalPlayerId && !remotePlayers.Contains(id)))continue;
                        playerPings[id]=ms;pingTimes[id]=DateTime.UtcNow;
                    }
                    continue;
                }
                if(packet.Type==PacketType.LobbyState && packet.Data.Length==5)
                {
                    int id=BitConverter.ToInt32(packet.Data,0);
                    if(id>0 && packet.Data[4]<=1){HostPlayerId=id;LobbyStarted=packet.Data[4]==1;}
                    continue;
                }
                if (packet.Type == PacketType.PlayerRoster)
                {
                    if (packet.Data.Length < 4 || packet.Data.Length % 4 != 0) continue;
                    int revision = BitConverter.ToInt32(packet.Data, 0);
                    if (revision <= rosterRevision) continue;
                    rosterRevision = revision;
                    var active = new HashSet<int>();
                    for (int i = 4; i < packet.Data.Length; i += 4)
                        active.Add(BitConverter.ToInt32(packet.Data, i));
                    activeRoster = active;
                    foreach (int id in new List<int>(remotePlayers))
                    {
                        if (active.Contains(id)) continue;
                        remotePlayers.Remove(id);
                        NotifyPlayerLeft(id);
                    }
                    foreach (int id in active)
                    {
                        if (id == LocalPlayerId || departedPlayers.Contains(id) || !remotePlayers.Add(id)) continue;
                        DeliverPacket(new NetworkPacket { Type = PacketType.PlayerConnected, Data = BitConverter.GetBytes(id) });
                    }
                    continue;
                }
                if (packet.Type == PacketType.PlayerDisconnected && packet.Data.Length == 4)
                {
                    NotifyPlayerLeft(BitConverter.ToInt32(packet.Data, 0));
                    continue;
                }
                if ((packet.Type == PacketType.PlayerConnected && packet.Data.Length == 4) ||
                    (packet.Type == PacketType.PlayerTransform && PlayerTransformData.IsSupportedLength(packet.Data.Length)))
                {
                    int id = BitConverter.ToInt32(packet.Data, 0);
                    if (departedPlayers.Contains(id) || (activeRoster != null && !activeRoster.Contains(id))) continue;
                    if (id != LocalPlayerId) remotePlayers.Add(id);
                }
                if(packet.Type==PacketType.PlayerTransform && PlayerTransformData.TryDeserialize(packet.Data,out PlayerTransformData state))
                {CachePlayerState(state);}
                DeliverPacket(packet);
            }
        }

        private void CachePlayerState(PlayerTransformData state)
        {
            DateTime now=DateTime.UtcNow;
            if(state.HasVitals)vitalsTimes[state.PlayerId]=now;
            else if(vitalsTimes.TryGetValue(state.PlayerId,out DateTime at) && (now-at).TotalSeconds<3 &&
                playerStates.TryGetValue(state.PlayerId,out PlayerTransformData previous) && previous.HasVitals)
            {
                state.HasVitals=true;state.Health=state.HasLifeState && !state.Alive?0:previous.Health;
                state.MaxHealth=previous.MaxHealth;state.Fuel=previous.Fuel;state.MaxFuel=previous.MaxFuel;
            }
            playerStates[state.PlayerId]=state;stateTimes[state.PlayerId]=now;
        }
        private void DeliverPacket(NetworkPacket packet)
        {
            try { OnPacketReceived?.Invoke(packet); }
            catch (Exception ex) { Console.WriteLine($"UDP packet handler failed: {ex}"); }
        }

        private void NotifyPlayerLeft(int id)
        {
            if (id == LocalPlayerId || !departedPlayers.Add(id)) return;
            remotePlayers.Remove(id);
            playerNames.Remove(id);
            playerStates.Remove(id);stateTimes.Remove(id);vitalsTimes.Remove(id);playerPings.Remove(id);pingTimes.Remove(id);
            DeliverPacket(new NetworkPacket { Type = PacketType.PlayerDisconnected, Data = BitConverter.GetBytes(id) });
        }

        public void Disconnect()
        {
            if (connected && udpClient != null)
            {
                byte[] leave = PacketSerializer.Serialize(new NetworkPacket
                {
                    Type = PacketType.PlayerDisconnected, Data = new byte[0]
                });
                try { udpClient.Send(leave, leave.Length); } catch { }
            }
            connected = false;
            foreach (int id in new List<int>(remotePlayers)) NotifyPlayerLeft(id);
            remotePlayers.Clear();
            playerNames.Clear();
            departedPlayers.Clear();
            rosterRevision = 0;
            activeRoster = null;
            playerStates.Clear();stateTimes.Clear();vitalsTimes.Clear();playerPings.Clear();pingTimes.Clear();
            HostPlayerId=-1;LobbyStarted=false;
            lastHeartbeat = default(DateTime);
            lastPlanetRepair = default(DateTime);

            LocalPlayerId = -1;

            try
            {
                udpClient?.Close();
            }
            catch
            {
            }

            udpClient = null;
            receivedPackets = new PendingPacketBuffer();
            transformSequence = 0;
        }
    }
}
