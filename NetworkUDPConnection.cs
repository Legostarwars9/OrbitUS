using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Orbit_Us
{
    public class NetworkUDPConnection
    {
        private UdpClient udpClient;
        private bool connected;

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
            List<EnemyTransformData> enemies)
        {
            if (!connected)
                return;

            try
            {
                using (System.IO.MemoryStream stream =
                       new System.IO.MemoryStream())
                using (System.IO.BinaryWriter writer =
                       new System.IO.BinaryWriter(stream))
                {
                    writer.Write(enemies.Count);

                    foreach (EnemyTransformData enemy
                             in enemies)
                    {
                        byte[] data =
                            enemy.Serialize();

                        writer.Write(data.Length);
                        writer.Write(data);
                    }

                    NetworkPacket packet =
                        new NetworkPacket
                        {
                            Type =
                                PacketType.EnemySnapshot,

                            Data =
                                stream.ToArray()
                        };

                    byte[] packetData =
                        PacketSerializer.Serialize(packet);

                    await udpClient.SendAsync(
                        packetData,
                        packetData.Length
                    );
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"UDP enemy snapshot send failed: {ex.Message}"
                );
            }
        }

        private async Task SendPlayerConnect()
        {
            try
            {
                if (!connected)
                    return;

                NetworkPacket packet =
                    new NetworkPacket
                    {
                        Type =
                            PacketType.PlayerConnect,

                        Data =
                            new byte[0]
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
            try
            {
                while (connected)
                {
                    UdpReceiveResult result =
                        await udpClient.ReceiveAsync();

                    NetworkPacket packet =
                        PacketSerializer.Deserialize(
                            result.Buffer
                        );

                    //Console.WriteLine(
                    //    $"UDP packet received: {packet.Type}"
                    //);

                    if (packet.Type ==
                        PacketType.PlayerConnected)
                    {
                        if (packet.Data.Length >= 4)
                        {
                            int playerId =
                                BitConverter.ToInt32(
                                    packet.Data,
                                    0
                                );

                            if (LocalPlayerId == -1)
                            {
                                LocalPlayerId =
                                    playerId;

                                Console.WriteLine(
                                    $"Assigned local player ID: {LocalPlayerId}"
                                );
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"Player connected: {playerId}"
                                );
                            }
                        }
                    }

                    OnPacketReceived?.Invoke(
                        packet
                    );
                }
            }
            catch (Exception ex)
            {
                if (connected)
                {
                    Console.WriteLine(
                        $"UDP connection lost: {ex.Message}"
                    );
                }
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
        }
    }
}