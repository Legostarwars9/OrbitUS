using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;
using System;
namespace Orbit_Us
{
    public class NetworkServer
    {
        private TcpClient client;
        private TcpListener server;
        public Action<TcpClient> OnClientConnected;
        public void Start(int port)
        {
            server = new TcpListener(IPAddress.Any, port);
            server.Start();
        }

        public async Task<TcpClient> AcceptConnection()
        {
            client = await server.AcceptTcpClientAsync();

            OnClientConnected?.Invoke(client);

            NetworkStream stream = client.GetStream();
            NetworkPacket packet = new NetworkPacket
            {
                Type = PacketType.Connect,
                Data = Encoding.UTF8.GetBytes("Hello from Orbit Us!")
            };

            byte[] packetData = PacketSerializer.Serialize(packet);

            await stream.WriteAsync(packetData, 0, packetData.Length);
            NetworkPacket secondPacket = new NetworkPacket
            {
                Type = PacketType.GameState,
                Data = Encoding.UTF8.GetBytes("Second packet!")
            };

            byte[] secondPacketData = PacketSerializer.Serialize(secondPacket);

            await stream.WriteAsync(secondPacketData, 0, secondPacketData.Length);
            return client;
        }
    }
}