using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Orbit_Us
{
    public class NetworkUDPServer
    {
        private UdpClient udpServer;
        private bool running;

        private int nextPlayerId = 1;

        private readonly Dictionary<IPEndPoint, int> clients =
            new Dictionary<IPEndPoint, int>();

        public void Start(int port)
        {
            Stop();

            udpServer = new UdpClient(port);
            running = true;

            Console.WriteLine(
                $"UDP Server Started on port {port}"
            );

            _ = Listen();
        }

        private async Task Listen()
        {
            while (running)
            {
                try
                {
                    UdpReceiveResult result =
                        await udpServer.ReceiveAsync();

                    IPEndPoint sender =
                        result.RemoteEndPoint;

                    NetworkPacket packet =
                        PacketSerializer.Deserialize(
                            result.Buffer
                        );

                    if (packet.Type ==
                        PacketType.PlayerConnect)
                    {
                        await HandlePlayerConnect(sender);
                        continue;
                    }

                    if (!clients.ContainsKey(sender))
                    {
                        Console.WriteLine(
                            "Received UDP packet from unknown client."
                        );

                        continue;
                    }

                    if (packet.Type ==
                        PacketType.PlayerTransform)
                    {
                        PlayerTransformData transform =
                            PlayerTransformData.Deserialize(
                                packet.Data
                            );

                        transform.PlayerId =
                            clients[sender];

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

                        await Broadcast(
                            data,
                            sender
                        );
                    }
                }
                catch (Exception ex)
                {
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
            IPEndPoint sender)
        {
            if (clients.ContainsKey(sender))
            {
                int existingId =
                    clients[sender];

                NetworkPacket existingPacket =
                    new NetworkPacket
                    {
                        Type =
                            PacketType.PlayerConnected,

                        Data =
                            BitConverter.GetBytes(
                                existingId
                            )
                    };

                byte[] existingData =
                    PacketSerializer.Serialize(
                        existingPacket
                    );

                await udpServer.SendAsync(
                    existingData,
                    existingData.Length,
                    sender
                );

                return;
            }

            int playerId = nextPlayerId++;

            clients.Add(
                sender,
                playerId
            );

            Console.WriteLine(
                $"UDP player connected: {playerId}"
            );

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
                PacketSerializer.Serialize(packet);

            await Broadcast(
                data,
                null
            );
        }

        private async Task Broadcast(
            byte[] data,
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
                    await udpServer.SendAsync(
                        data,
                        data.Length,
                        client
                    );
                }
                catch
                {
                    clients.Remove(client);
                }
            }
        }

        public void Stop()
        {
            running = false;

            clients.Clear();

            try
            {
                udpServer?.Close();
            }
            catch
            {
            }

            udpServer = null;
        }
    }
}