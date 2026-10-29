namespace Orbit_Us
{
    public enum PacketType
    {
        Connect,
        PlayerConnect,
        PlayerDisconnected,
        PlayerTransform,
        EnemySnapshot,
        EnemyDamage,
        GameState,
        KeepAlive,
        KeepAliveResponse,
        PlayerConnected,
        EnemyDamageAck,
        PlayerAssigned
    }

    public class NetworkPacket
    {
        public PacketType Type { get; set; }
        public byte[] Data { get; set; }
    }
}
