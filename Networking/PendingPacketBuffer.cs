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
        private readonly ProgressSnapshotCodec.Assembler progressSnapshots = new ProgressSnapshotCodec.Assembler();
        private byte[][] latestProgressSnapshot;
        private object latestEnemyData,latestPlanetData,latestProgressData;
        private readonly WorldMotionInbox enemyMotion=new WorldMotionInbox(),planetMotion=new WorldMotionInbox();
        private readonly Dictionary<int, CombatSnapshotCodec.Assembler> combat = new Dictionary<int, CombatSnapshotCodec.Assembler>();
        private readonly Dictionary<int, byte[][]> latestCombat = new Dictionary<int, byte[][]>();
        private readonly Dictionary<int,CombatFrame> latestCombatData=new Dictionary<int,CombatFrame>();
        private const int MaxEvents = 4096;
        private const int MaxPlayers = 256;

        public void Add(NetworkPacket packet)
        {
            lock (gate)
            {
                if(packet.Type==PacketType.EnemyMotion){enemyMotion.Add(packet);return;}
                if(packet.Type==PacketType.PlanetMotion){planetMotion.Add(packet);return;}
                if (packet.Type == PacketType.PlayerTransform)
                {
                    if (!PlayerTransformData.TryDeserialize(packet.Data,out PlayerTransformData transform) || transform.PlayerId<=0) return;
                    if (sequences.TryGetValue(transform.PlayerId, out uint previous))
                    {
                        if (unchecked((int)(transform.Sequence - previous)) <= 0) return;
                    }
                    else if (sequences.Count >= MaxPlayers) return;
                    sequences[transform.PlayerId] = transform.Sequence;
                    packet.Decoded=transform;
                    players[transform.PlayerId] = packet;
                }
                else if (packet.Type == PacketType.EnemySnapshot)
                {
                    if (snapshots.TryAdd(packet.Data, out var state))
                    {latestSnapshot = snapshots.CompletedChunks;latestEnemyData=state;}
                }
                else if (packet.Type == PacketType.PlanetSnapshot)
                {
                    if (planetSnapshots.TryAdd(packet.Data, out var state))
                    {latestPlanetSnapshot = planetSnapshots.CompletedChunks;latestPlanetData=state;}
                }
                else if (packet.Type == PacketType.ProgressSnapshot)
                {
                    if (progressSnapshots.TryAdd(packet.Data, out var state))
                    {latestProgressSnapshot = progressSnapshots.CompletedChunks;latestProgressData=state;}
                }
                else if (packet.Type == PacketType.CombatSnapshot)
                {
                    int owner=CombatSnapshotCodec.ReadOwner(packet.Data);
                    if(owner<=0) return;
                    if(!combat.TryGetValue(owner,out CombatSnapshotCodec.Assembler buffer))
                    {
                        if(combat.Count>=MaxPlayers) return;
                        buffer=new CombatSnapshotCodec.Assembler();combat.Add(owner,buffer);
                    }
                    if(buffer.TryAdd(packet.Data,out CombatFrame state))
                    {latestCombat[owner]=buffer.CompletedChunks;latestCombatData[owner]=state;}
                }
                else
                {
                    if (packet.Type == PacketType.PlayerDisconnected && packet.Data.Length == 4)
                    {
                        int id = BitConverter.ToInt32(packet.Data, 0);
                        players.Remove(id);
                        combat.Remove(id);latestCombat.Remove(id);
                        latestCombatData.Remove(id);
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

        public byte[] GetEnemyRepairRequest()
        {
            lock (gate)
            {
                int[] missing = snapshots.GetMissingChunks(64, out int snapshotId);
                if (missing.Length == 0) return null;
                byte[] request = new byte[(missing.Length + 1) * 4];
                Buffer.BlockCopy(BitConverter.GetBytes(snapshotId), 0, request, 0, 4);
                for (int i = 0; i < missing.Length; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(missing[i]), 0, request, (i + 1) * 4, 4);
                return request;
            }
        }

        public byte[] GetProgressRepairRequest()
        {
            lock (gate)
            {
                int[] missing = progressSnapshots.GetMissingChunks(64, out int snapshotId);
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
                enemyMotion.Drain(result);planetMotion.Drain(result);
                if (latestSnapshot != null)
                {
                    for(int i=0;i<latestSnapshot.Length;i++)
                        result.Add(new NetworkPacket {Type=PacketType.EnemySnapshot,Data=latestSnapshot[i],SnapshotValidated=true,Decoded=i==latestSnapshot.Length-1?latestEnemyData:null});
                    latestSnapshot = null;
                    latestEnemyData=null;
                }
                if (latestPlanetSnapshot != null)
                {
                    for(int i=0;i<latestPlanetSnapshot.Length;i++)
                        result.Add(new NetworkPacket {Type=PacketType.PlanetSnapshot,Data=latestPlanetSnapshot[i],SnapshotValidated=true,Decoded=i==latestPlanetSnapshot.Length-1?latestPlanetData:null});
                    latestPlanetSnapshot = null;
                    latestPlanetData=null;
                }
                if (latestProgressSnapshot != null)
                {
                    for(int i=0;i<latestProgressSnapshot.Length;i++)
                        result.Add(new NetworkPacket {Type=PacketType.ProgressSnapshot,Data=latestProgressSnapshot[i],SnapshotValidated=true,Decoded=i==latestProgressSnapshot.Length-1?latestProgressData:null});
                    latestProgressSnapshot = null;
                    latestProgressData=null;
                }
                foreach(var pair in latestCombat)
                    for(int i=0;i<pair.Value.Length;i++)result.Add(new NetworkPacket {Type=PacketType.CombatSnapshot,Data=pair.Value[i],SnapshotValidated=true,Decoded=i==pair.Value.Length-1?latestCombatData[pair.Key]:null});
                latestCombat.Clear();
                latestCombatData.Clear();
                return result;
            }
        }
    }
}
