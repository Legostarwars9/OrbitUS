using System.Net.Sockets;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text;

namespace Orbit_Us
{
    public class NetworkConnection
    {
        private TcpClient tcpClient;
        private NetworkStream stream;

        private bool connected;
        private Task receiveTask;

        private DateTime lastKeepAlive;

        public bool IsConnected
        {
            get
            {
                return connected;
            }
        }

        public async Task<NetworkPacket> Connect(
            string address,
            int port)
        {
            Disconnect();

            Console.WriteLine(
                $"Connecting to {address}:{port}."
            );

            tcpClient = new TcpClient();

            await tcpClient.ConnectAsync(
                address,
                port
            );

            Console.WriteLine(
                "TCP connection established."
            );

            stream = tcpClient.GetStream();

            Console.WriteLine(
                "Waiting for server packet."
            );

            NetworkPacket packet =
                await ReadPacket(stream);

            Console.WriteLine(
                "Server packet received."
            );

            connected = true;
            lastKeepAlive = DateTime.Now;

            receiveTask = ReceiveLoop();

            return packet;
        }

        private async Task ReceiveLoop()
        {
            Console.WriteLine(
                "Network receive loop started."
            );

            try
            {
                while (connected)
                {
                    NetworkPacket packet =
                        await ReadPacket(stream);

                    if (packet.Type ==
                        PacketType.KeepAlive)
                    {
                        lastKeepAlive =
                            DateTime.Now;

                        Console.WriteLine(
                            "Received KeepAlive from server."
                        );

                        NetworkPacket response =
                            new NetworkPacket
                            {
                                Type =
                                    PacketType.KeepAliveResponse,

                                Data =
                                    Encoding.UTF8.GetBytes(
                                        DateTime.Now.ToString(
                                            "yyyy-MM-dd HH:mm:ss.fff"
                                        )
                                    )
                            };

                        byte[] responseData =
                            PacketSerializer.Serialize(
                                response
                            );

                        await stream.WriteAsync(
                            responseData,
                            0,
                            responseData.Length
                        );

                        Console.WriteLine(
                            "Sent KeepAliveResponse to server."
                        );
                    }
                    else
                    {
                        Console.WriteLine(
                            $"Received packet type: {packet.Type}"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                if (connected)
                {
                    Console.WriteLine(
                        $"Connection lost: {ex.Message}"
                    );
                }

                Disconnect();
            }
        }

        public void StartKeepAlive()
        {
            if (!connected)
            {
                Console.WriteLine(
                    "Cannot start KeepAlive: not connected."
                );

                return;
            }

            Console.WriteLine(
                "KeepAlive monitoring already running."
            );
        }

        public async Task<NetworkPacket> ReceivePacket()
        {
            if (!connected || stream == null)
            {
                throw new Exception(
                    "Not connected to a server."
                );
            }

            return await ReadPacket(stream);
        }

        public void Disconnect()
        {
            bool wasConnected = connected;

            connected = false;

            try
            {
                stream?.Close();
            }
            catch
            {
            }

            try
            {
                tcpClient?.Close();
            }
            catch
            {
            }

            stream = null;
            tcpClient = null;
            receiveTask = null;

            if (wasConnected)
            {
                Console.WriteLine(
                    "Disconnected from server."
                );
            }
        }

        private async Task<byte[]> ReadExact(
            NetworkStream stream,
            int length)
        {
            byte[] buffer =
                new byte[length];

            int totalRead = 0;

            while (totalRead < length)
            {
                int bytesRead =
                    await stream.ReadAsync(
                        buffer,
                        totalRead,
                        length - totalRead
                    );

                if (bytesRead == 0)
                {
                    throw new Exception(
                        "Connection closed while reading packet."
                    );
                }

                totalRead += bytesRead;
            }

            return buffer;
        }

        public async Task<NetworkPacket> ReadPacket(
            NetworkStream stream)
        {
            byte[] header =
                await ReadExact(
                    stream,
                    8
                );

            using (MemoryStream headerStream =
                   new MemoryStream(header))

            using (BinaryReader reader =
                   new BinaryReader(headerStream))
            {
                PacketType type =
                    (PacketType)reader.ReadInt32();

                int dataLength =
                    reader.ReadInt32();

                byte[] packetData =
                    await ReadExact(
                        stream,
                        dataLength
                    );

                return new NetworkPacket
                {
                    Type = type,
                    Data = packetData
                };
            }
        }
    }
}