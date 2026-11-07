using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;

namespace Orbit_Us
{
    public class NetworkUDPServer
    {
        private sealed class CachedPlanetSnapshot
        {
            public byte[][] Chunks;
        }

        private UdpClient udpServer;
        private volatile bool running;
        private int nextPlayerId;
        private readonly object lifecycle = new object();
        private IPEndPoint[] destinations = new IPEndPoint[0];
        public bool LoopbackClientsOnly {get;set;}
        public void Start(int port)
        {
            Stop();

            udpServer = new UdpClient(port);
            running = true;

            Console.WriteLine(
                $"UDP Server Started on port {port}"
            );

            UdpClient socket = udpServer;
            _ = Task.Run(() => Listen(socket));
        }

        private async Task Listen(UdpClient socket)
        {
            var clients = new Dictionary<IPEndPoint, int>();
            var sequences = new Dictionary<IPEndPoint, uint>();
            IPEndPoint hostEndpoint = null;
            var lastSeen = new Dictionary<IPEndPoint, DateTime>();
            // The codec caps each snapshot at 8 MiB; retain only three IDs.
            var enemySnapshots=new SortedDictionary<int,CachedPlanetSnapshot>();
            var lastEnemyRepair=new Dictionary<IPEndPoint,DateTime>();
            var planetSnapshots = new SortedDictionary<int, CachedPlanetSnapshot>();
            var lastPlanetRepair = new Dictionary<IPEndPoint, DateTime>();
            var combatFrames=new Dictionary<IPEndPoint,CombatSnapshotCodec.Assembler>();
            var progressSnapshots = new SortedDictionary<int, CachedPlanetSnapshot>();
            var lastProgressRepair = new Dictionary<IPEndPoint, DateTime>();
            var probes=new Dictionary<IPEndPoint,long>();
            var pings=new Dictionary<IPEndPoint,int>();
            var pingTimes=new Dictionary<IPEndPoint,DateTime>();
            var kicked=new Dictionary<IPEndPoint,DateTime>();
            var kickOrder=new Queue<IPEndPoint>();
            bool lobbyStarted=false;
            Task<UdpReceiveResult> receive = socket.ReceiveAsync();
            Task tick = Task.Delay(1000);
            int rosterRevision = 0;
            while (running && ReferenceEquals(socket, udpServer))
            {
                try
                {
                    await Task.WhenAny(receive, tick).ConfigureAwait(false);
                    if (tick.IsCompleted)
                    {
                        tick = Task.Delay(1000);
                        foreach (var endpoint in new List<IPEndPoint>(clients.Keys))
                        {
                            if ((DateTime.UtcNow - lastSeen[endpoint]).TotalSeconds < 10) continue;
                            int departed = clients[endpoint];
                            clients.Remove(endpoint);
                            sequences.Remove(endpoint);
                            combatFrames.Remove(endpoint);
                            lastSeen.Remove(endpoint);
                            lastPlanetRepair.Remove(endpoint);lastEnemyRepair.Remove(endpoint);
                            lastProgressRepair.Remove(endpoint);
                            probes.Remove(endpoint);pings.Remove(endpoint);pingTimes.Remove(endpoint);
                            if (endpoint.Equals(hostEndpoint))
                            {
                                hostEndpoint = null;
                                planetSnapshots.Clear();enemySnapshots.Clear();
                                progressSnapshots.Clear();
                            }
                            await Broadcast(socket, clients, PacketSerializer.Serialize(new NetworkPacket
                            {
                                Type = PacketType.PlayerDisconnected, Data = BitConverter.GetBytes(departed)
                            }), null);
                        }
                        lock (lifecycle)
                            if (ReferenceEquals(socket, udpServer)) destinations = new List<IPEndPoint>(clients.Keys).ToArray();
                        byte[] roster = new byte[(clients.Count + 1) * 4];
                        Buffer.BlockCopy(BitConverter.GetBytes(++rosterRevision), 0, roster, 0, 4);
                        int offset = 4;
                        foreach (int id in clients.Values)
                        {
                            Buffer.BlockCopy(BitConverter.GetBytes(id), 0, roster, offset, 4);
                            offset += 4;
                        }
                        await Broadcast(socket, clients, PacketSerializer.Serialize(new NetworkPacket
                        {
                            Type = PacketType.PlayerRoster, Data = roster
                        }), null);
                        // One server-clock round trip per second. No client clocks or reported ping values are trusted.
                        foreach(var endpoint in clients.Keys)
                        {
                            long now=DateTime.UtcNow.Ticks;
                            if(probes.TryGetValue(endpoint,out long pending) && now-pending<TimeSpan.FromSeconds(5).Ticks)continue;
                            probes[endpoint]=now;
                            byte[] probe=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.LatencyProbe,Data=BitConverter.GetBytes(now)});
                            await socket.SendAsync(probe,probe.Length,endpoint);
                        }
                        var latency=new List<byte>();
                        foreach(var client in clients)
                        {
                            if(!pings.TryGetValue(client.Key,out int ms) || !pingTimes.TryGetValue(client.Key,out DateTime at) ||
                                (DateTime.UtcNow-at).TotalSeconds>=5)continue;
                            latency.AddRange(BitConverter.GetBytes(client.Value));latency.AddRange(BitConverter.GetBytes(ms));
                            if(latency.Count==512)
                            {
                                await Broadcast(socket,clients,PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.PlayerLatency,Data=latency.ToArray()}),null);
                                latency.Clear();
                            }
                        }
                        if(latency.Count>0)await Broadcast(socket,clients,PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.PlayerLatency,Data=latency.ToArray()}),null);
                        if(hostEndpoint!=null && clients.TryGetValue(hostEndpoint,out int hostId))
                        {
                            byte[] state=new byte[5];Buffer.BlockCopy(BitConverter.GetBytes(hostId),0,state,0,4);state[4]=lobbyStarted?(byte)1:(byte)0;
                            await Broadcast(socket,clients,PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.LobbyState,Data=state}),null);
                        }
                    }
                    if (!receive.IsCompleted) continue;
                    UdpReceiveResult result;
                    try { result = await receive.ConfigureAwait(false); }
                    finally
                    {
                        if (running && ReferenceEquals(socket, udpServer)) receive = socket.ReceiveAsync();
                    }

                    if (!running || !ReferenceEquals(socket, udpServer))
                        break;

                    IPEndPoint sender =
                        result.RemoteEndPoint;

                    //Console.WriteLine(
                    //    $"UDP packet received from {sender.Address}:{sender.Port}"
                    //);

                    NetworkPacket packet =
                        PacketSerializer.Deserialize(
                            result.Buffer
                        );
                    if(kicked.TryGetValue(sender,out DateTime lastKick))
                    {
                        if((DateTime.UtcNow-lastKick).TotalSeconds>=0.5)
                        {
                            kicked[sender]=DateTime.UtcNow;
                            byte[] notice=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.PlayerKicked,Data=new byte[0]});
                            await socket.SendAsync(notice,notice.Length,sender);
                        }
                        continue;
                    }

                    //Console.WriteLine(
                    //    $"UDP packet type: {packet.Type}"
                    //);

                    if (packet.Type ==
                        PacketType.PlayerConnect)
                    {
                        if(LoopbackClientsOnly && !IPAddress.IsLoopback(sender.Address))continue;
                        if (packet.Data.Length != 1 || packet.Data[0] > 1)
                            continue;
                        if (packet.Data[0] == 1)
                        {
                            // Only the host's explicit loopback connection may
                            // publish snapshots or acknowledge applied damage.
                            if (!IPAddress.IsLoopback(sender.Address) ||
                                (hostEndpoint != null && !hostEndpoint.Equals(sender)))
                                continue;
                            hostEndpoint = sender;
                        }
                        if (!clients.ContainsKey(sender) && clients.Count >= 256) continue;
                        lastSeen[sender] = DateTime.UtcNow;
                        await HandlePlayerConnect(socket, clients, sender);
                        lock (lifecycle)
                            if (ReferenceEquals(socket, udpServer)) destinations = new List<IPEndPoint>(clients.Keys).ToArray();
                        continue;
                    }

                    // Late datagrams after disconnect/restart are expected.
                    if (!clients.ContainsKey(sender)) continue;
                    lastSeen[sender] = DateTime.UtcNow;
                    if(packet.Type==PacketType.LatencyReply && packet.Data.Length==8)
                    {
                        long stamp=BitConverter.ToInt64(packet.Data,0);
                        if(probes.TryGetValue(sender,out long expected) && stamp==expected)
                        {
                            double elapsed=(DateTime.UtcNow.Ticks-stamp)/(double)TimeSpan.TicksPerMillisecond;
                            if(elapsed>=0 && elapsed<=10000)
                            {
                                int ms=(int)Math.Round(elapsed);
                                pings[sender]=pings.TryGetValue(sender,out int old)?(old*3+ms)/4:ms;
                                pingTimes[sender]=DateTime.UtcNow;probes.Remove(sender);
                            }
                        }
                        continue;
                    }
                    if(packet.Type==PacketType.KeepAlive)
                    {
                        byte[] reply=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.KeepAliveResponse,Data=new byte[0]});
                        await socket.SendAsync(reply,reply.Length,sender);continue;
                    }
                    if(packet.Type==PacketType.LobbyState)
                    {
                        if(sender.Equals(hostEndpoint) && packet.Data.Length==1 && packet.Data[0]==1)lobbyStarted=true;
                        continue;
                    }
                    if(packet.Type==PacketType.HostKick)
                    {
                        if(!sender.Equals(hostEndpoint) || packet.Data.Length!=4)continue;
                        int target=BitConverter.ToInt32(packet.Data,0);IPEndPoint victim=null;
                        foreach(var client in clients)if(client.Value==target && !client.Key.Equals(hostEndpoint)){victim=client.Key;break;}
                        if(victim==null)continue;
                        if(kicked.Count>=256)kicked.Remove(kickOrder.Dequeue());
                        kicked[victim]=DateTime.UtcNow;kickOrder.Enqueue(victim);
                        clients.Remove(victim);lastSeen.Remove(victim);sequences.Remove(victim);combatFrames.Remove(victim);
                        lastPlanetRepair.Remove(victim);lastEnemyRepair.Remove(victim);lastProgressRepair.Remove(victim);
                        probes.Remove(victim);pings.Remove(victim);pingTimes.Remove(victim);
                        lock(lifecycle)if(ReferenceEquals(socket,udpServer))destinations=new List<IPEndPoint>(clients.Keys).ToArray();
                        byte[] notice=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.PlayerKicked,Data=new byte[0]});
                        await socket.SendAsync(notice,notice.Length,victim);
                        await Broadcast(socket,clients,PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.PlayerDisconnected,Data=BitConverter.GetBytes(target)}),null);
                        continue;
                    }
                    if (packet.Type == PacketType.PlayerDisconnected && packet.Data.Length == 0)
                    {
                        int departed = clients[sender];
                        clients.Remove(sender);
                        lastSeen.Remove(sender);
                        lastPlanetRepair.Remove(sender);lastEnemyRepair.Remove(sender);
                        lastProgressRepair.Remove(sender);
                        sequences.Remove(sender);
                        combatFrames.Remove(sender);
                        probes.Remove(sender);pings.Remove(sender);pingTimes.Remove(sender);
                        if (sender.Equals(hostEndpoint))
                        {
                            hostEndpoint = null;
                            planetSnapshots.Clear();enemySnapshots.Clear();
                                progressSnapshots.Clear();
                        }
                        lock (lifecycle)
                            if (ReferenceEquals(socket, udpServer)) destinations = new List<IPEndPoint>(clients.Keys).ToArray();
                        await Broadcast(socket, clients, PacketSerializer.Serialize(new NetworkPacket
                        {
                            Type = PacketType.PlayerDisconnected, Data = BitConverter.GetBytes(departed)
                        }), null);
                        continue;
                    }

                    if (packet.Type ==
                        PacketType.PlayerTransform)
                    {
                        if (!PlayerTransformData.TryDeserialize(packet.Data,out PlayerTransformData transform)) continue;

                        if (sequences.TryGetValue(sender, out uint previous) &&
                            unchecked((int)(transform.Sequence - previous)) <= 0) continue;
                        sequences[sender] = transform.Sequence;
                        int playerId = clients[sender];

                        transform.PlayerId =
                            playerId;
                        // Host identity comes from relay registration, never a client claim.
                        transform.IsHost=hostEndpoint!=null && hostEndpoint.Equals(sender);

                        NetworkPacket outgoingPacket =
                            new NetworkPacket
                            {
                                Type =
                                    PacketType.PlayerTransform,

                                Data =
                                    transform.Serialize()
                            };

                        byte[] data =
                            PacketSerializer.Serialize(
                                outgoingPacket
                            );

                        //Console.WriteLine(
                        //    $"Broadcasting transform from player {playerId}"
                        //);

                        await Broadcast(
                            socket, clients,
                            data,
                            sender
                        );
                    }
                    if (packet.Type == PacketType.EnemySnapshot)
                    {
                        if (hostEndpoint == null || !hostEndpoint.Equals(sender) ||
                            !EnemySnapshotCodec.TryReadChunkHeader(packet.Data, out int snapshotId,
                                out int chunkIndex, out int chunkCount)) continue;
                        if (!enemySnapshots.TryGetValue(snapshotId, out CachedPlanetSnapshot cached))
                        {
                            if (enemySnapshots.Count >= 3)
                            {
                                int oldest = int.MaxValue;
                                foreach (int id in enemySnapshots.Keys) { oldest = id; break; }
                                if (snapshotId < oldest) continue;
                                enemySnapshots.Remove(oldest);
                            }
                            cached = new CachedPlanetSnapshot { Chunks = new byte[chunkCount][] };
                            enemySnapshots.Add(snapshotId, cached);
                        }
                        if (cached.Chunks.Length != chunkCount) continue;
                        cached.Chunks[chunkIndex] = result.Buffer;
                        await Broadcast(socket, clients, result.Buffer, sender);
                        continue;
                    }
                    if (packet.Type == PacketType.EnemySnapshotRequest)
                    {
                        if (hostEndpoint == null || hostEndpoint.Equals(sender) ||
                            packet.Data.Length < 8 || packet.Data.Length > 260 || packet.Data.Length % 4 != 0)
                            continue;
                        DateTime now = DateTime.UtcNow;
                        if (lastEnemyRepair.TryGetValue(sender, out DateTime previousRepair) &&
                            (now - previousRepair).TotalSeconds < 0.25) continue;
                        lastEnemyRepair[sender] = now;
                        int snapshotId = BitConverter.ToInt32(packet.Data, 0);
                        if (!enemySnapshots.TryGetValue(snapshotId, out CachedPlanetSnapshot cached)) continue;
                        var sent = new HashSet<int>();
                        for (int offset = 4; offset < packet.Data.Length; offset += 4)
                        {
                            int index = BitConverter.ToInt32(packet.Data, offset);
                            if (index < 0 || index >= cached.Chunks.Length || !sent.Add(index)) continue;
                            byte[] chunk = cached.Chunks[index];
                            if (chunk != null) await socket.SendAsync(chunk, chunk.Length, sender);
                        }
                        continue;
                    }

                    if (packet.Type == PacketType.PlanetSnapshot)
                    {
                        if (hostEndpoint == null || !hostEndpoint.Equals(sender) ||
                            !PlanetSnapshotCodec.TryReadChunkHeader(packet.Data, out int snapshotId,
                                out int chunkIndex, out int chunkCount)) continue;
                        if (!planetSnapshots.TryGetValue(snapshotId, out CachedPlanetSnapshot cached))
                        {
                            if (planetSnapshots.Count >= 3)
                            {
                                int oldest = int.MaxValue;
                                foreach (int id in planetSnapshots.Keys) { oldest = id; break; }
                                if (snapshotId < oldest) continue;
                                planetSnapshots.Remove(oldest);
                            }
                            cached = new CachedPlanetSnapshot { Chunks = new byte[chunkCount][] };
                            planetSnapshots.Add(snapshotId, cached);
                        }
                        if (cached.Chunks.Length != chunkCount) continue;
                        cached.Chunks[chunkIndex] = result.Buffer;
                        await Broadcast(socket, clients, result.Buffer, sender);
                        continue;
                    }
                    if (packet.Type == PacketType.PlanetSnapshotRequest)
                    {
                        if (hostEndpoint == null || hostEndpoint.Equals(sender) ||
                            packet.Data.Length < 8 || packet.Data.Length > 260 || packet.Data.Length % 4 != 0)
                            continue;
                        DateTime now = DateTime.UtcNow;
                        if (lastPlanetRepair.TryGetValue(sender, out DateTime previousRepair) &&
                            (now - previousRepair).TotalSeconds < 0.25) continue;
                        lastPlanetRepair[sender] = now;
                        int snapshotId = BitConverter.ToInt32(packet.Data, 0);
                        if (!planetSnapshots.TryGetValue(snapshotId, out CachedPlanetSnapshot cached)) continue;
                        var sent = new HashSet<int>();
                        for (int offset = 4; offset < packet.Data.Length; offset += 4)
                        {
                            int index = BitConverter.ToInt32(packet.Data, offset);
                            if (index < 0 || index >= cached.Chunks.Length || !sent.Add(index)) continue;
                            byte[] chunk = cached.Chunks[index];
                            if (chunk != null) await socket.SendAsync(chunk, chunk.Length, sender);
                        }
                        continue;
                    }

                    if (packet.Type == PacketType.ProgressSnapshot)
                    {
                        if (hostEndpoint == null || !hostEndpoint.Equals(sender) ||
                            !ProgressSnapshotCodec.TryReadChunkHeader(packet.Data, out int snapshotId,
                                out int chunkIndex, out int chunkCount)) continue;
                        if (!progressSnapshots.TryGetValue(snapshotId, out CachedPlanetSnapshot cached))
                        {
                            if (progressSnapshots.Count >= 3)
                            {
                                int oldest = int.MaxValue;
                                foreach (int id in progressSnapshots.Keys) { oldest = id; break; }
                                if (snapshotId < oldest) continue;
                                progressSnapshots.Remove(oldest);
                            }
                            cached = new CachedPlanetSnapshot { Chunks = new byte[chunkCount][] };
                            progressSnapshots.Add(snapshotId, cached);
                        }
                        if (cached.Chunks.Length != chunkCount) continue;
                        cached.Chunks[chunkIndex] = result.Buffer;
                        await Broadcast(socket, clients, result.Buffer, sender);
                        continue;
                    }
                    if (packet.Type == PacketType.ProgressSnapshotRequest)
                    {
                        if (hostEndpoint == null || hostEndpoint.Equals(sender) ||
                            packet.Data.Length < 8 || packet.Data.Length > 260 || packet.Data.Length % 4 != 0)
                            continue;
                        DateTime now = DateTime.UtcNow;
                        if (lastProgressRepair.TryGetValue(sender, out DateTime previousRepair) &&
                            (now - previousRepair).TotalSeconds < 0.25) continue;
                        lastProgressRepair[sender] = now;
                        int snapshotId = BitConverter.ToInt32(packet.Data, 0);
                        if (!progressSnapshots.TryGetValue(snapshotId, out CachedPlanetSnapshot cached)) continue;
                        var sent = new HashSet<int>();
                        for (int offset = 4; offset < packet.Data.Length; offset += 4)
                        {
                            int index = BitConverter.ToInt32(packet.Data, offset);
                            if (index < 0 || index >= cached.Chunks.Length || !sent.Add(index)) continue;
                            byte[] chunk = cached.Chunks[index];
                            if (chunk != null) await socket.SendAsync(chunk, chunk.Length, sender);
                        }
                        continue;
                    }

                    if(packet.Type==PacketType.CombatSnapshot)
                    {
                        if(CombatSnapshotCodec.ReadOwner(packet.Data)!=clients[sender])continue;
                        if(!combatFrames.TryGetValue(sender,out CombatSnapshotCodec.Assembler assembler))
                        {assembler=new CombatSnapshotCodec.Assembler();combatFrames.Add(sender,assembler);}
                        if(!assembler.TryAdd(packet.Data,out CombatFrame frame))continue;
                        bool authority=hostEndpoint!=null && hostEndpoint.Equals(sender);
                        if(frame.Authority!=authority)continue;
                        bool valid=true;
                        if(!authority)foreach(var entity in frame.Entities) if(entity.Kind!=0 || !entity.PlayerSafe){valid=false;break;}
                        if(!valid)continue;
                        foreach(var chunk in assembler.CompletedChunks)
                            await Broadcast(socket,clients,PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.CombatSnapshot,Data=chunk}),sender);
                        continue;
                    }
                    if(packet.Type==PacketType.CombatHit && hostEndpoint!=null && hostEndpoint.Equals(sender))
                    {
                        var hit=CombatHit.Deserialize(packet.Data);
                        foreach(var client in clients)if(client.Value==hit.PlayerId)
                        {await socket.SendAsync(result.Buffer,result.Buffer.Length,client.Key);break;}
                        continue;
                    }
                    if(packet.Type==PacketType.CombatHitAck && hostEndpoint!=null && !hostEndpoint.Equals(sender) && packet.Data.Length==4)
                    {
                        byte[] ack=new byte[8];Buffer.BlockCopy(BitConverter.GetBytes(clients[sender]),0,ack,0,4);
                        Buffer.BlockCopy(packet.Data,0,ack,4,4);
                        byte[] bytes=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.CombatHitAck,Data=ack});
                        await socket.SendAsync(bytes,bytes.Length,hostEndpoint);continue;
                    }

                    if(packet.Type==PacketType.TeamWipe && hostEndpoint!=null && hostEndpoint.Equals(sender) && packet.Data.Length==4 && BitConverter.ToInt32(packet.Data,0)==1)
                    {await Broadcast(socket,clients,result.Buffer,sender);continue;}
                    if(packet.Type==PacketType.TeamWipeAck && hostEndpoint!=null && !hostEndpoint.Equals(sender) && packet.Data.Length==4 && BitConverter.ToInt32(packet.Data,0)==1)
                    {
                        byte[] bytes=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.TeamWipeAck,Data=BitConverter.GetBytes(clients[sender])});
                        await socket.SendAsync(bytes,bytes.Length,hostEndpoint);continue;
                    }

                    if(packet.Type==PacketType.TaskTeleport && hostEndpoint!=null && hostEndpoint.Equals(sender))
                    {
                        var command=TaskTeleportData.Deserialize(packet.Data);
                        foreach(var client in clients)if(client.Value==command.RecipientId)
                        {await socket.SendAsync(result.Buffer,result.Buffer.Length,client.Key);break;}
                        continue;
                    }
                    if(packet.Type==PacketType.TaskTeleportAck && hostEndpoint!=null && !hostEndpoint.Equals(sender) && packet.Data.Length==4)
                    {
                        byte[] ack=new byte[8];Buffer.BlockCopy(BitConverter.GetBytes(clients[sender]),0,ack,0,4);
                        Buffer.BlockCopy(packet.Data,0,ack,4,4);
                        byte[] bytes=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.TaskTeleportAck,Data=ack});
                        await socket.SendAsync(bytes,bytes.Length,hostEndpoint);continue;
                    }

                    if (packet.Type == PacketType.ProgressAction)
                    {
                        if (hostEndpoint == null || hostEndpoint.Equals(sender)) continue;
                        var action = ProgressAction.Deserialize(packet.Data);
                        action.PlayerId = clients[sender];
                        byte[] bytes = PacketSerializer.Serialize(new NetworkPacket { Type = PacketType.ProgressAction, Data = action.Serialize() });
                        await socket.SendAsync(bytes, bytes.Length, hostEndpoint);
                        continue;
                    }
                    if (packet.Type == PacketType.ProgressActionAck && hostEndpoint != null && hostEndpoint.Equals(sender) && packet.Data.Length == 8)
                    {
                        int target = BitConverter.ToInt32(packet.Data, 0);
                        foreach (var client in clients)
                            if (client.Value == target) { await socket.SendAsync(result.Buffer, result.Buffer.Length, client.Key); break; }
                        continue;
                    }

                    if (packet.Type ==
                        PacketType.EnemyDamage)
                    {
                        if (hostEndpoint == null || hostEndpoint.Equals(sender))
                            continue;
                        var damage = EnemyDamageData.Deserialize(packet.Data);
                        damage.PlayerId = clients[sender];
                        byte[] data = PacketSerializer.Serialize(new NetworkPacket
                        {
                            Type = PacketType.EnemyDamage, Data = damage.Serialize()
                        });
                        await socket.SendAsync(data, data.Length, hostEndpoint);

                        continue;
                    }

                    if (packet.Type == PacketType.EnemyDamageAck &&
                        hostEndpoint != null && hostEndpoint.Equals(sender) && packet.Data.Length == 8)
                    {
                        int playerId = BitConverter.ToInt32(packet.Data, 0);
                        foreach (var client in new List<KeyValuePair<IPEndPoint, int>>(clients))
                            if (client.Value == playerId)
                            {
                                await socket.SendAsync(result.Buffer, result.Buffer.Length, client.Key);
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    if (!running || !ReferenceEquals(socket, udpServer)) break;
                    if (running)
                    {
                        Console.WriteLine(
                            $"UDP server error: {ex.Message}"
                        );
                    }
                }
            }
        }

        private async Task HandlePlayerConnect(
            UdpClient socket, Dictionary<IPEndPoint, int> clients, IPEndPoint sender)
        {
            if (clients.TryGetValue(
                    sender,
                    out int existingId))
            {
                Console.WriteLine(
                    $"UDP player {existingId} already connected."
                );

                byte[] existingAssignment = PacketSerializer.Serialize(new NetworkPacket
                {
                    Type = PacketType.PlayerAssigned, Data = BitConverter.GetBytes(existingId)
                });
                await socket.SendAsync(existingAssignment, existingAssignment.Length, sender);
                await SendPlayerConnected(
                    socket,
                    existingId,
                    sender
                );

                return;
            }

            int playerId =
                Interlocked.Increment(ref nextPlayerId);

            clients.Add(
                sender,
                playerId
            );

            Console.WriteLine(
                $"UDP player connected: {playerId} from {sender.Address}:{sender.Port}"
            );

            Console.WriteLine(
                $"Current UDP players: {clients.Count}"
            );

            // Tell the new player their own ID.
            byte[] assignment = PacketSerializer.Serialize(new NetworkPacket
            {
                Type = PacketType.PlayerAssigned, Data = BitConverter.GetBytes(playerId)
            });
            await socket.SendAsync(assignment, assignment.Length, sender);
            await SendPlayerConnected(
                    socket,
                playerId,
                sender
            );

            // Tell the new player about every player
            // that was already connected.
            foreach (
                KeyValuePair<IPEndPoint, int> client
                in clients)
            {
                if (client.Key.Equals(sender))
                    continue;

                Console.WriteLine(
                    $"Telling player {playerId} about player {client.Value}"
                );

                await SendPlayerConnected(
                    socket,
                    client.Value,
                    sender
                );
            }

            // Tell every existing player about the new player.
            foreach (
                IPEndPoint client
                in new List<IPEndPoint>(clients.Keys))
            {
                if (client.Equals(sender))
                    continue;

                Console.WriteLine(
                    $"Telling existing player at {client.Address}:{client.Port} about player {playerId}"
                );

                await SendPlayerConnected(
                    socket,
                    playerId,
                    client
                );
            }
        }

        private async Task SendPlayerConnected(
            UdpClient socket, int playerId,
            IPEndPoint destination)
        {
            NetworkPacket packet =
                new NetworkPacket
                {
                    Type =
                        PacketType.PlayerConnected,

                    Data =
                        BitConverter.GetBytes(
                            playerId
                        )
                };

            byte[] data =
                PacketSerializer.Serialize(
                    packet
                );

            try
            {
                await socket.SendAsync(
                    data,
                    data.Length,
                    destination
                );

                Console.WriteLine(
                    $"Sent PlayerConnected({playerId}) to {destination.Address}:{destination.Port}"
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Failed sending PlayerConnected to {destination}: {ex.Message}"
                );
            }
        }

        private async Task Broadcast(
            UdpClient socket, Dictionary<IPEndPoint, int> clients, byte[] data,
            IPEndPoint sender)
        {
            foreach (
                IPEndPoint client
                in new List<IPEndPoint>(clients.Keys))
            {
                if (sender != null &&
                    client.Equals(sender))
                {
                    continue;
                }

                try
                {
                    await socket.SendAsync(
                        data,
                        data.Length,
                        client
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed sending UDP packet to {client}: {ex.Message}"
                    );
                }
            }
        }

        public void Stop()
        {
            lock (lifecycle)
            {
                running = false;
                if (udpServer != null)
                {
                    byte[] stopped = PacketSerializer.Serialize(new NetworkPacket
                    {
                        Type = PacketType.ServerStopped, Data = new byte[0]
                    });
                    foreach (var endpoint in destinations)
                        try { udpServer.Send(stopped, stopped.Length, endpoint); } catch { }
                    udpServer.Close();
                }
                destinations = new IPEndPoint[0];
                udpServer = null;
            }
        }
    }
}
