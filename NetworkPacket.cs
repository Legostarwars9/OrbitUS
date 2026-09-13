namespace Orbit_Us
{
    public enum PacketType
    {
        Connect,
        PlayerConnect,
        PlayerDisconnected,
        PlayerTransform,
        GameState,
        KeepAlive,
        KeepAliveResponse,
        PlayerConnected
    }

    public class NetworkPacket
    {
        public PacketType Type { get; set; }
        public byte[] Data { get; set; }
    }
}