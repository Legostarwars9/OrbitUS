namespace Orbit_Us
{
    public enum PacketType
    {
        Connect,
        Disconnect,
        PlayerInput,
        GameState,
        KeepAlive,
        KeepAliveResponse
    }

    public class NetworkPacket
    {
        public PacketType Type { get; set; }
        public byte[] Data { get; set; }
    }
}