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

            nextPlayerId = 1;

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
                        await HandlePlayerConnect(sender);
                        continue;
                    }

                    if (!clients.ContainsKey(sender))
                    {
                        Console.WriteLine(
                            $"UDP packet from unknown client: {sender.Address}:{sender.Port}"
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

                        int playerId =
                            clients[sender];

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
                            data,
                            sender
                        );
                    }
                    if (packet.Type ==
                        PacketType.EnemySnapshot)
                    {
                        await Broadcast(
                            result.Buffer,
                            sender
                        );

                        continue;
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
            if (clients.TryGetValue(
                    sender,
                    out int existingId))
            {
                Console.WriteLine(
                    $"UDP player {existingId} already connected."
                );

                await SendPlayerConnected(
                    existingId,
                    sender
                );

                return;
            }

            int playerId =
                nextPlayerId++;

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
            await SendPlayerConnected(
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
                    playerId,
                    client
                );
            }
        }

        private async Task SendPlayerConnected(
            int playerId,
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
                await udpServer.SendAsync(
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