using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Orbit_Us
{
    public class NetworkUDPConnection
    {
        private UdpClient udpClient;
        private bool connected;
        private readonly ConcurrentQueue<NetworkPacket> receivedPackets = new ConcurrentQueue<NetworkPacket>();
        private DateTime lastConnectSent;
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

            IPEndPoint localEndpoint =
                (IPEndPoint)
                udpClient.Client.LocalEndPoint;

            Console.WriteLine(
                $"UDP connected to {address}:{port}"
            );

            Console.WriteLine(
                $"UDP local endpoint: {localEndpoint.Address}:{localEndpoint.Port}"
            );

            _ = ReceiveLoop();

            _ = SendPlayerConnect();
        }
        public async Task SendEnemySnapshot(
            List<EnemyTransformData> enemies,
            int snapshotId)
        {
            if (!connected)
                return;

            try
            {
                foreach (byte[] chunk in EnemySnapshotCodec.Encode(enemies, snapshotId))
                {
                    if (!connected) return;
                    byte[] data = PacketSerializer.Serialize(new NetworkPacket
                    {
                        Type = PacketType.EnemySnapshot, Data = chunk
                    });
                    await udpClient.SendAsync(data, data.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP enemy snapshot send failed: {ex.Message}");
            }
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

        public async Task SendPlayerTransform(
            float x,
            float y,
            float rotation)
        {
            if (!connected)
                return;

            if (LocalPlayerId == -1)
                return;

            try
            {
                PlayerTransformData transform =
                    new PlayerTransformData
                    {
                        PlayerId =
                            LocalPlayerId,

                        X = x,
                        Y = y,
                        Rotation = rotation
                    };

                NetworkPacket packet =
                    new NetworkPacket
                    {
                        Type =
                            PacketType.PlayerTransform,

                        Data =
                            transform.Serialize()
                    };

                byte[] data =
                    PacketSerializer.Serialize(
                        packet
                    );

                await udpClient.SendAsync(
                    data,
                    data.Length
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"UDP transform send failed: {ex.Message}"
                );
            }
        }

        private async Task ReceiveLoop()
        {
            UdpClient socket = udpClient;
            while (connected && ReferenceEquals(socket, udpClient))
            {
                try
                {
                    UdpReceiveResult result = await socket.ReceiveAsync();
                    NetworkPacket packet = PacketSerializer.Deserialize(result.Buffer);
                    if (connected && ReferenceEquals(socket, udpClient))
                        receivedPackets.Enqueue(packet);
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
            if (LocalPlayerId == -1 && (DateTime.UtcNow - lastConnectSent).TotalSeconds >= 1)
                _ = SendPlayerConnect();

            int processed = 0;
            while (processed++ < 4096 && receivedPackets.TryDequeue(out NetworkPacket packet))
            {
                if (packet.Type == PacketType.PlayerAssigned && packet.Data.Length == 4 && LocalPlayerId == -1)
                    LocalPlayerId = BitConverter.ToInt32(packet.Data, 0);
                if (LocalPlayerId == -1) continue;
                try { OnPacketReceived?.Invoke(packet); }
                catch (Exception ex) { Console.WriteLine($"UDP packet handler failed: {ex}"); }
            }
        }

        public void Disconnect()
        {
            connected = false;

            LocalPlayerId = -1;

            try
            {
                udpClient?.Close();
            }
            catch
            {
            }

            udpClient = null;
            while (receivedPackets.TryDequeue(out _)) { }
        }
    }
}
