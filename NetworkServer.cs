using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;
using System;
using System.Collections.Generic;
using System.IO;

namespace Orbit_Us
{
    public class NetworkServer
    {
        private TcpListener server;

        private readonly List<TcpClient> clients =
            new List<TcpClient>();

        public Action<TcpClient> OnClientConnected;

        public bool IsRunning { get; private set; }

        public void Start(int port)
        {
            Stop();

            server =
                new TcpListener(
                    IPAddress.Any,
                    port
                );

            server.Start();

            IsRunning = true;
        }

        public async Task AcceptConnection()
        {
            while (IsRunning)
            {
                TcpClient client = null;

                try
                {
                    Console.WriteLine("Waiting for client connection...");

                    client = await server.AcceptTcpClientAsync();

                    Console.WriteLine("TCP client accepted.");

                    if (!IsRunning)
                    {
                        client.Close();
                        break;
                    }

                    clients.Add(client);

                    OnClientConnected?.Invoke(client);

                    _ = HandleClient(client);
                }
                catch (Exception ex)
                {
                    if (!IsRunning)
                        break;

                    Console.WriteLine(
                        $"Accept connection error: {ex.Message}"
                    );

                    client?.Close();
                }
            }
        }

        private async Task HandleClient(
            TcpClient client)
        {
            NetworkStream stream =
                client.GetStream();

            try
            {
                NetworkPacket connectionPacket =
                    new NetworkPacket
                    {
                        Type =
                            PacketType.Connect,

                        Data =
                            Encoding.UTF8.GetBytes(
                                DateTime.Now.ToString(
                                    "yyyy-MM-dd HH:mm:ss.fff"
                                )
                            )
                    };

                byte[] connectionData =
                    PacketSerializer.Serialize(
                        connectionPacket
                    );

                await stream.WriteAsync(
                    connectionData,
                    0,
                    connectionData.Length
                );

                Console.WriteLine(
                    "Sent connection timestamp to client."
                );

                while (IsRunning)
                {
                    await Task.Delay(30000);

                    if (!IsRunning)
                        break;

                    NetworkPacket keepAlive =
                        new NetworkPacket
                        {
                            Type =
                                PacketType.KeepAlive,

                            Data =
                                Encoding.UTF8.GetBytes(
                                    DateTime.Now.ToString(
                                        "yyyy-MM-dd HH:mm:ss.fff"
                                    )
                                )
                        };

                    byte[] keepAliveData =
                        PacketSerializer.Serialize(
                            keepAlive
                        );

                    await stream.WriteAsync(
                        keepAliveData,
                        0,
                        keepAliveData.Length
                    );

                    Console.WriteLine(
                        "Sent KeepAlive to client."
                    );

                    NetworkPacket response =
                        await ReadPacketWithTimeout(
                            stream,
                            15000
                        );

                    if (response.Type !=
                        PacketType.KeepAliveResponse)
                    {
                        Console.WriteLine(
                            "Client sent an unexpected packet. Disconnecting."
                        );

                        break;
                    }

                    Console.WriteLine(
                        "Received KeepAliveResponse from client."
                    );
                }
            }
            catch (Exception ex)
            {
                if (IsRunning)
                {
                    Console.WriteLine(
                        $"Client connection error: {ex.Message}"
                    );
                }
            }
            finally
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }

                clients.Remove(client);

                Console.WriteLine(
                    "Client disconnected."
                );
            }
        }

        private async Task<NetworkPacket>
            ReadPacketWithTimeout(
                NetworkStream stream,
                int timeout)
        {
            Task<NetworkPacket> readTask =
                ReadPacket(stream);

            Task timeoutTask =
                Task.Delay(timeout);

            Task completed =
                await Task.WhenAny(
                    readTask,
                    timeoutTask
                );

            if (completed == timeoutTask)
            {
                throw new Exception(
                    "Client did not respond to KeepAlive."
                );
            }

            return await readTask;
        }

        private async Task<NetworkPacket> ReadPacket(
            NetworkStream stream)
        {
            byte[] header =
                await ReadExact(stream, 8);

            using (MemoryStream headerStream =
                   new MemoryStream(header))

            using (BinaryReader reader =
                   new BinaryReader(headerStream))
            {
                PacketType type =
                    (PacketType)reader.ReadInt32();

                int dataLength =
                    reader.ReadInt32();

                byte[] data =
                    await ReadExact(
                        stream,
                        dataLength
                    );

                return new NetworkPacket
                {
                    Type = type,
                    Data = data
                };
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
                        "Connection closed."
                    );
                }

                totalRead += bytesRead;
            }

            return buffer;
        }

        public void Stop()
        {
            IsRunning = false;

            foreach (TcpClient client in clients)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }

            clients.Clear();

            try
            {
                server?.Stop();
            }
            catch
            {
            }

            server = null;
        }
    }
}