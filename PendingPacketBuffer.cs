using System;
using System.Collections.Generic;

namespace Orbit_Us
{
    // One buffer per socket session. Only complete snapshots replace state, so a
    // lost chunk never makes a newer partial snapshot erase a usable update.
    internal sealed class PendingPacketBuffer
    {
        private readonly object gate = new object();
        private readonly Queue<NetworkPacket> events = new Queue<NetworkPacket>();
        private readonly Dictionary<int, NetworkPacket> players = new Dictionary<int, NetworkPacket>();
        private readonly Dictionary<int, uint> sequences = new Dictionary<int, uint>();
        private readonly EnemySnapshotCodec.Assembler snapshots = new EnemySnapshotCodec.Assembler();
        private byte[][] latestSnapshot;
        private readonly PlanetSnapshotCodec.Assembler planetSnapshots = new PlanetSnapshotCodec.Assembler();
        private byte[][] latestPlanetSnapshot;
        private const int MaxEvents = 4096;
        private const int MaxPlayers = 256;

        public void Add(NetworkPacket packet)
        {
            lock (gate)
            {
                if (packet.Type == PacketType.PlayerTransform)
                {
                    if (packet.Data.Length != 20) return;
                    var transform = PlayerTransformData.Deserialize(packet.Data);
                    if (transform.PlayerId <= 0 || float.IsNaN(transform.X) || float.IsInfinity(transform.X) ||
                        float.IsNaN(transform.Y) || float.IsInfinity(transform.Y) ||
                        float.IsNaN(transform.Rotation) || float.IsInfinity(transform.Rotation)) return;
                    if (sequences.TryGetValue(transform.PlayerId, out uint previous))
                    {
                        if (unchecked((int)(transform.Sequence - previous)) <= 0) return;
                    }
                    else if (sequences.Count >= MaxPlayers) return;
                    sequences[transform.PlayerId] = transform.Sequence;
                    players[transform.PlayerId] = packet;
                }
                else if (packet.Type == PacketType.EnemySnapshot)
                {
                    if (snapshots.TryAdd(packet.Data, out _))
                        latestSnapshot = snapshots.CompletedChunks;
                }
                else if (packet.Type == PacketType.PlanetSnapshot)
                {
                    if (planetSnapshots.TryAdd(packet.Data, out _))
                        latestPlanetSnapshot = planetSnapshots.CompletedChunks;
                }
                else
                {
                    if (packet.Type == PacketType.PlayerDisconnected && packet.Data.Length == 4)
                    {
                        int id = BitConverter.ToInt32(packet.Data, 0);
                        players.Remove(id);
                        sequences.Remove(id);
                    }
                    // Damage and ACKs retry; drop overflow rather than retain an
                    // unlimited history. Never coalesce distinct damage hits.
                    if (events.Count < MaxEvents) events.Enqueue(packet);
                }
            }
        }

        public byte[] GetPlanetRepairRequest()
        {
            lock (gate)
            {
                int[] missing = planetSnapshots.GetMissingChunks(64, out int snapshotId);
                if (missing.Length == 0) return null;
                byte[] request = new byte[(missing.Length + 1) * 4];
                Buffer.BlockCopy(BitConverter.GetBytes(snapshotId), 0, request, 0, 4);
                for (int i = 0; i < missing.Length; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(missing[i]), 0, request, (i + 1) * 4, 4);
                return request;
            }
        }

        public List<NetworkPacket> Drain()
        {
            lock (gate)
            {
                var result = new List<NetworkPacket>(events);
                events.Clear();
                result.AddRange(players.Values);
                players.Clear();
                if (latestSnapshot != null)
                {
                    foreach (byte[] chunk in latestSnapshot)
                        result.Add(new NetworkPacket { Type = PacketType.EnemySnapshot, Data = chunk });
                    latestSnapshot = null;
                }
                if (latestPlanetSnapshot != null)
                {
                    foreach (byte[] chunk in latestPlanetSnapshot)
                        result.Add(new NetworkPacket { Type = PacketType.PlanetSnapshot, Data = chunk });
                    latestPlanetSnapshot = null;
                }
                return result;
            }
        }
    }
}
