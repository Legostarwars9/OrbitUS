using System;
using System.Collections.Generic;
using System.IO;

namespace Orbit_Us
{
    // Small datagrams avoid the UDP size limit and IP fragmentation at high counts.
    // A client applies a snapshot only after every chunk has arrived.
    public static class EnemySnapshotCodec
    {
        private const int Version = 2;
        private const int HeaderSize = 20;
        private const int EntrySize = 28;
        private const int EnemiesPerChunk = 40;
        public const int MaxEnemies = 20000;

        public static List<byte[]> Encode(List<EnemyTransformData> enemies, int snapshotId)
        {
            if (snapshotId <= 0 || enemies.Count > MaxEnemies)
                throw new InvalidDataException("Invalid enemy snapshot size or ID.");

            var result = new List<byte[]>();
            int chunks = Math.Max(1, (enemies.Count + EnemiesPerChunk - 1) / EnemiesPerChunk);
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Version);
                    writer.Write(snapshotId);
                    writer.Write(chunk);
                    writer.Write(chunks);
                    int count = Math.Min(EnemiesPerChunk, enemies.Count - chunk * EnemiesPerChunk);
                    writer.Write(count);
                    for (int i = 0; i < count; i++)
                        writer.Write(enemies[chunk * EnemiesPerChunk + i].Serialize());
                    result.Add(stream.ToArray());
                }
            }
            return result;
        }

        public sealed class Assembler
        {
            private sealed class Pending
            {
                public EnemyTransformData[][] Chunks;
                public int Received;
            }

            private readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
            public int LastSnapshotId { get; private set; }

            public bool TryAdd(byte[] data, out List<EnemyTransformData> enemies)
            {
                enemies = null;
                if (data == null || data.Length < HeaderSize)
                    return false;

                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    if (reader.ReadInt32() != Version)
                        return false;
                    int id = reader.ReadInt32();
                    int index = reader.ReadInt32();
                    int total = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    if (id <= LastSnapshotId || total < 1 ||
                        total > (MaxEnemies + EnemiesPerChunk - 1) / EnemiesPerChunk ||
                        index < 0 || index >= total || count < 0 || count > EnemiesPerChunk ||
                        (index < total - 1 && count != EnemiesPerChunk) ||
                        (total > 1 && count == 0) || data.Length != HeaderSize + count * EntrySize)
                        return false;

                    var chunk = new EnemyTransformData[count];
                    for (int i = 0; i < count; i++)
                    {
                        var item = EnemyTransformData.Deserialize(reader.ReadBytes(EntrySize));
                        if (item.EnemyId <= 0 || item.Type < 0 ||
                            !Finite(item.X) || !Finite(item.Y) || !Finite(item.HP) ||
                            !Finite(item.HeadHP) || !Finite(item.Rotation))
                            return false;
                        chunk[i] = item;
                    }

                    if (!pending.TryGetValue(id, out Pending snapshot))
                    {
                        // Bound memory while retaining several interleaved snapshots.
                        if (pending.Count >= 8)
                        {
                            int oldest = int.MaxValue;
                            foreach (int key in pending.Keys)
                                oldest = Math.Min(oldest, key);
                            if (id < oldest)
                                return false;
                            pending.Remove(oldest);
                        }
                        snapshot = new Pending { Chunks = new EnemyTransformData[total][] };
                        pending.Add(id, snapshot);
                    }
                    if (snapshot.Chunks.Length != total || snapshot.Chunks[index] != null)
                        return false;

                    snapshot.Chunks[index] = chunk;
                    if (++snapshot.Received != total)
                        return false;

                    var complete = new List<EnemyTransformData>();
                    var ids = new HashSet<int>();
                    foreach (var part in snapshot.Chunks)
                    {
                        foreach (var item in part)
                        {
                            if (!ids.Add(item.EnemyId))
                            {
                                pending.Remove(id);
                                return false;
                            }
                            complete.Add(item);
                        }
                    }
                    LastSnapshotId = id;
                    foreach (int key in new List<int>(pending.Keys))
                        if (key <= id) pending.Remove(key);
                    enemies = complete;
                    return true;
                }
            }

            private static bool Finite(float value)
            {
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }
        }
    }
}
