using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System;
using System.IO;
namespace Orbit_Us
{
    public class NetworkConnection
    {
        private TcpClient tcpClient;
        private NetworkStream stream;
        public async Task<NetworkPacket> Connect(string address, int port)
        {
            tcpClient = new TcpClient();
            
            await tcpClient.ConnectAsync(address, port);

            stream = tcpClient.GetStream();

            NetworkPacket packet = await ReadPacket(stream);

            return packet;
        }
        public async Task<NetworkPacket> ReceivePacket()
        {
            return await ReadPacket(stream);
        }
        private async Task<byte[]> ReadExact(NetworkStream stream, int length)
        {
            byte[] buffer = new byte[length];
            int totalRead = 0;

            while (totalRead < length)
            {
                int bytesRead = await stream.ReadAsync(
                    buffer,
                    totalRead,
                    length - totalRead
                );

                if (bytesRead == 0)
                {
                    throw new Exception("Connection closed while reading packet.");
                }

                totalRead += bytesRead;
            }

            return buffer;
        }
        public async Task<NetworkPacket> ReadPacket(NetworkStream stream)
        {
            byte[] header = await ReadExact(stream, 8);

            using (MemoryStream headerStream = new MemoryStream(header))
            using (BinaryReader reader = new BinaryReader(headerStream))
            {
                PacketType type = (PacketType)reader.ReadInt32();
                int dataLength = reader.ReadInt32();

                byte[] packetData = await ReadExact(stream, dataLength);

                return new NetworkPacket
                {
                    Type = type,
                    Data = packetData
                };
            }
        }
    }
}