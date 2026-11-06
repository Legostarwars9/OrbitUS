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
        private int sendingPlanetRepair;
        private DateTime lastPlanetRepair;
        private PendingPacketBuffer receivedPackets = new PendingPacketBuffer();
        private DateTime lastConnectSent;
        private DateTime lastHeartbeat;
        private long lastServerPacketTicks;
        private int rosterRevision;
        private HashSet<int> activeRoster;
        private readonly HashSet<int> remotePlayers = new HashSet<int>();
        private readonly HashSet<int> departedPlayers = new HashSet<int>();
        public bool IsConnected => connected;
        public bool HostConnection { get; set; }

        private string serverAddress;
        private int serverPort;

        public int LocalPlayerId { get; private set; } = -1;

        public event Action<NetworkPacket> OnPacketReceived;

        public void Connect(
            string address,
            int port)
        {
            Disconnect();

            serverAddress = address;
            serverPort = port;

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
                foreach (byte[] chunk in EnemySnapshotCodec.Encode(enemies, snapshotId))
                {
                    if (!connected || !ReferenceEquals(socket, udpClient)) return;
                    byte[] data = PacketSerializer.Serialize(new NetworkPacket
                    {
                        Type = PacketType.EnemySnapshot, Data = chunk
                    });
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
                    await socket.SendAsync(data, data.Length).ConfigureAwait(false);
                    // Bound large-world bursts to roughly 200 datagrams/sec.
                    if ((i + 1) % 16 == 0 && i + 1 < chunks.Count)
                        await Task.Delay(80).ConfigureAwait(false);
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

        public async Task SendPlayerTransform(float x, float y, float rotation)
        {
            UdpClient socket = udpClient;
            if (!connected || socket == null || LocalPlayerId == -1 ||
                Interlocked.CompareExchange(ref sendingTransform, 1, 0) != 0) return;
            try
            {
                var transform = new PlayerTransformData
                {
                    PlayerId = LocalPlayerId, Sequence = ++transformSequence,
                    X = x, Y = y, Rotation = rotation
                };
                byte[] data = PacketSerializer.Serialize(new NetworkPacket
                {
                    Type = PacketType.PlayerTransform, Data = transform.Serialize()
                });
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
                        inbox.Add(packet);
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
                Disconnect();
                return;
            }
            if (LocalPlayerId > 0 && (DateTime.UtcNow - lastHeartbeat).TotalSeconds >= 1)
            {
                lastHeartbeat = DateTime.UtcNow;
                _ = SendPacket(new NetworkPacket { Type = PacketType.KeepAlive, Data = new byte[0] });
            }
            if (LocalPlayerId == -1 && (DateTime.UtcNow - lastConnectSent).TotalSeconds >= 1)
                _ = SendPlayerConnect();

            if (!HostConnection && LocalPlayerId > 0 && (DateTime.UtcNow - lastPlanetRepair).TotalSeconds >= 0.5)
            {
                lastPlanetRepair = DateTime.UtcNow;
                byte[] request = receivedPackets.GetPlanetRepairRequest();
                if (request != null) _ = RequestPlanetRepair(request);
            }

            foreach (NetworkPacket packet in receivedPackets.Drain())
            {
                if (packet.Type == PacketType.ServerStopped)
                {
                    Disconnect();
                    return;
                }
                if (packet.Type == PacketType.PlayerAssigned && packet.Data.Length == 4 && LocalPlayerId == -1)
                    LocalPlayerId = BitConverter.ToInt32(packet.Data, 0);
                if (LocalPlayerId == -1) continue;
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
                        DeliverPacket(new NetworkPacket { Type = PacketType.PlayerDisconnected, Data = BitConverter.GetBytes(id) });
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
                    (packet.Type == PacketType.PlayerTransform && packet.Data.Length == 20))
                {
                    int id = BitConverter.ToInt32(packet.Data, 0);
                    if (departedPlayers.Contains(id) || (activeRoster != null && !activeRoster.Contains(id))) continue;
                    if (id != LocalPlayerId) remotePlayers.Add(id);
                }
                DeliverPacket(packet);
            }
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
            departedPlayers.Clear();
            rosterRevision = 0;
            activeRoster = null;
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
