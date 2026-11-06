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
            var planetSnapshots = new SortedDictionary<int, CachedPlanetSnapshot>();
            var lastPlanetRepair = new Dictionary<IPEndPoint, DateTime>();
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
                            lastSeen.Remove(endpoint);
                            lastPlanetRepair.Remove(endpoint);
                            if (endpoint.Equals(hostEndpoint))
                            {
                                hostEndpoint = null;
                                planetSnapshots.Clear();
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

                    //Console.WriteLine(
                    //    $"UDP packet type: {packet.Type}"
                    //);

                    if (packet.Type ==
                        PacketType.PlayerConnect)
                    {
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
                    if (packet.Type == PacketType.PlayerDisconnected && packet.Data.Length == 0)
                    {
                        int departed = clients[sender];
                        clients.Remove(sender);
                        lastSeen.Remove(sender);
                        lastPlanetRepair.Remove(sender);
                        sequences.Remove(sender);
                        if (sender.Equals(hostEndpoint))
                        {
                            hostEndpoint = null;
                            planetSnapshots.Clear();
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
                        if (packet.Data.Length != 20) continue;
                        PlayerTransformData transform =
                            PlayerTransformData.Deserialize(
                                packet.Data
                            );

                        if (sequences.TryGetValue(sender, out uint previous) &&
                            unchecked((int)(transform.Sequence - previous)) <= 0) continue;
                        sequences[sender] = transform.Sequence;
                        int playerId = clients[sender];

                        transform.PlayerId =
                            playerId;

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
                    if (packet.Type ==
                        PacketType.EnemySnapshot)
                    {
                        if (hostEndpoint == null || !hostEndpoint.Equals(sender))
                            continue;
                        await Broadcast(
                            socket, clients,
                            result.Buffer,
                            sender
                        );

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
