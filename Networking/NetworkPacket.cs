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
        PlayerAssigned,
        PlayerRoster,
        ServerStopped,
        PlanetSnapshot,
        PlanetSnapshotRequest,
        ProgressSnapshot,
        ProgressSnapshotRequest,
        ProgressAction,
        ProgressActionAck,
        CombatSnapshot,
        CombatHit,
        CombatHitAck,
        EnemySnapshotRequest
    }

    public class NetworkPacket
    {
        public PacketType Type { get; set; }
        public byte[] Data { get; set; }
    }
}
