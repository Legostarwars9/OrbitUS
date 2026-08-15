using System.Net.Sockets;
using System.Threading.Tasks;

namespace Orbit_Us
{
    public class NetworkManager
    {
        private NetworkConnection networkConnection;

        public NetworkManager()
        {
            networkConnection = new NetworkConnection();
        }

        public async Task<NetworkPacket> Connect(string address, int port)
        {
            return await networkConnection.Connect(address, port);
        }
        public async Task<NetworkPacket> ReceivePacket()
        {
            return await networkConnection.ReceivePacket();
        }
    }
}