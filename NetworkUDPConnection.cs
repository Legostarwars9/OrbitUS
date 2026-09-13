using System;
using System.Net.Sockets;
using System.Threading.Tasks;

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

            udpClient = new UdpClient();

            udpClient.Connect(
                address,
                port
            );

            connected = true;

            Console.WriteLine(
                $"UDP connected to {address}:{port}"
            );

            _ = ReceiveLoop();

            _ = SendPlayerConnect();
        }

        private async Task SendPlayerConnect()
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
                PacketSerializer.Serialize(packet);

            await udpClient.SendAsync(
                data,
                data.Length
            );

            Console.WriteLine(
                "Sent UDP PlayerConnect."
            );
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

            PlayerTransformData transform =
                new PlayerTransformData
                {
                    PlayerId = LocalPlayerId,
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
                PacketSerializer.Serialize(packet);

            await udpClient.SendAsync(
                data,
                data.Length
            );
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
                                LocalPlayerId = playerId;

                                Console.WriteLine(
                                    $"Assigned local player ID: {LocalPlayerId}"
                                );
                            }
                        }
                    }

                    OnPacketReceived?.Invoke(packet);
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