using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace Orbit_Us
{
    public static class CombatSnapshotCodec
    {
        private const int Version=1, HeaderBytes=24, ChunkBytes=1076;
        public const int MaxSnapshotBytes=1024*1024;
        public static List<byte[]> Encode(CombatFrame state, int snapshotId)
        {
            if (snapshotId <= 0 || state == null || state.Owner<=0 || state.Sequence!=snapshotId) throw new InvalidDataException("Invalid progress snapshot.");
            byte[] payload;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteFrame(writer, state);
                if (stream.Length > MaxSnapshotBytes) throw new InvalidDataException("Progress snapshot too large.");
                payload = stream.ToArray();
            }
            int total = (payload.Length + ChunkBytes - 1) / ChunkBytes;
            var result = new List<byte[]>(total);
            for (int index = 0; index < total; index++)
            {
                int length = Math.Min(ChunkBytes, payload.Length - index * ChunkBytes);
                using (var stream = new MemoryStream(HeaderBytes + length))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(state.Owner);
                    writer.Write(Version);
                    writer.Write(snapshotId);
                    writer.Write(index);
                    writer.Write(total);
                    writer.Write(payload.Length);
                    writer.Write(payload, index * ChunkBytes, length);
                    result.Add(stream.ToArray());
                }
            }
            return result;
        }

        public static bool TryReadChunkHeader(byte[] data, out int snapshotId, out int chunkIndex, out int chunkCount)
        {
            snapshotId = chunkIndex = chunkCount = 0;
            if (data == null || data.Length <= HeaderBytes || data.Length > HeaderBytes + ChunkBytes) return false;
            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                if (reader.ReadInt32()<=0 || reader.ReadInt32() != Version) return false;
                int id = reader.ReadInt32(), index = reader.ReadInt32(), total = reader.ReadInt32(), length = reader.ReadInt32();
                if (id <= 0 || length < 4 || length > MaxSnapshotBytes ||
                    total != (length + ChunkBytes - 1) / ChunkBytes || index < 0 || index >= total ||
                    data.Length != HeaderBytes + Math.Min(ChunkBytes, length - index * ChunkBytes)) return false;
                snapshotId = id; chunkIndex = index; chunkCount = total;
                return true;
            }
        }


        public static int ReadOwner(byte[] data)
        {return data!=null && data.Length>=4?BitConverter.ToInt32(data,0):0;}
        private static void String(BinaryWriter w,string text)
        {byte[] b=System.Text.Encoding.UTF8.GetBytes(text);if(b.Length>256)throw new InvalidDataException();w.Write(b.Length);w.Write(b);}
        private static string String(BinaryReader r)
        {int n=r.ReadInt32();if(n<0||n>256)throw new InvalidDataException();byte[] b=r.ReadBytes(n);if(b.Length!=n)throw new EndOfStreamException();return new System.Text.UTF8Encoding(false,true).GetString(b);}
        private static void Validate(CombatFrame f)
        {
            if(f.Owner<=0 || f.Sequence<=0 || f.Entities.Count>4096)throw new InvalidDataException("Invalid combat frame.");
            var ids=new HashSet<int>();
            foreach(var e in f.Entities)
            {
                if(e.Id<=0 || !ids.Add(e.Id) || e.Kind>2 || e.Prefab==null || e.Prefab.Length>256 || e.LinePoints.Count>192 || e.LinePoints.Count%3!=0)throw new InvalidDataException("Invalid combat entity.");
                foreach(float v in new[]{e.X,e.Y,e.VX,e.VY,e.Rotation,e.ScaleX,e.ScaleY,e.R,e.G,e.B,e.A,e.Amount,e.CollisionRadius})
                    if(!CombatRules.Finite(v))throw new InvalidDataException("Nonfinite combat state.");
                foreach(float v in e.LinePoints)if(!CombatRules.Finite(v))throw new InvalidDataException();
                if(e.Amount<0)throw new InvalidDataException("Invalid pellet amount.");
            }
        }
        private static void WriteFrame(BinaryWriter w,CombatFrame f)
        {
            Validate(f);w.Write(f.Owner);w.Write(f.Sequence);w.Write(f.Alive);w.Write(f.Authority);w.Write(f.Entities.Count);
            foreach(var e in f.Entities)
            {
                w.Write(e.Id);String(w,e.Prefab);w.Write(e.Kind);w.Write(e.PlayerSafe);w.Write(e.Ended);w.Write(e.Shield);w.Write(e.Interceptor);w.Write(e.CollisionRadius);
                foreach(float v in new[]{e.X,e.Y,e.VX,e.VY,e.Rotation,e.ScaleX,e.ScaleY,e.R,e.G,e.B,e.A,e.Amount})w.Write(v);
                w.Write(e.LinePoints.Count);foreach(float v in e.LinePoints)w.Write(v);
            }
        }
        private static CombatFrame ReadFrame(BinaryReader r)
        {
            var f=new CombatFrame {Owner=r.ReadInt32(),Sequence=r.ReadInt32(),Alive=r.ReadBoolean(),Authority=r.ReadBoolean()};
            int count=r.ReadInt32();if(count<0 || count>4096)throw new InvalidDataException();
            for(int i=0;i<count;i++)
            {
                var e=new CombatEntityState {Id=r.ReadInt32(),Prefab=String(r),Kind=r.ReadByte(),PlayerSafe=r.ReadBoolean(),Ended=r.ReadBoolean(),Shield=r.ReadBoolean(),Interceptor=r.ReadBoolean(),CollisionRadius=r.ReadSingle(),X=r.ReadSingle(),Y=r.ReadSingle(),VX=r.ReadSingle(),VY=r.ReadSingle(),Rotation=r.ReadSingle(),ScaleX=r.ReadSingle(),ScaleY=r.ReadSingle(),R=r.ReadSingle(),G=r.ReadSingle(),B=r.ReadSingle(),A=r.ReadSingle(),Amount=r.ReadSingle()};
                int n=r.ReadInt32();if(n<0||n>192)throw new InvalidDataException();
                for(int k=0;k<n;k++)e.LinePoints.Add(r.ReadSingle());f.Entities.Add(e);
            }
            Validate(f);return f;
        }

        public sealed class Assembler
        {
            private sealed class Pending
            {
                public int Length, Received, MissingCursor;
                public byte[][] Chunks;
            }

            private readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
            public int LastSnapshotId { get; private set; }
            public byte[][] CompletedChunks { get; private set; }

            public int[] GetMissingChunks(int maxCount, out int snapshotId)
            {
                snapshotId = 0;
                if (maxCount <= 0) return new int[0];
                foreach (int key in pending.Keys) snapshotId = Math.Max(snapshotId, key);
                if (snapshotId == 0) return new int[0];
                Pending snapshot = pending[snapshotId];
                var missing = new List<int>();
                for (int checkedCount = 0; checkedCount < snapshot.Chunks.Length && missing.Count < maxCount; checkedCount++)
                {
                    int index = snapshot.MissingCursor;
                    snapshot.MissingCursor = (index + 1) % snapshot.Chunks.Length;
                    if (snapshot.Chunks[index] == null) missing.Add(index);
                }
                return missing.ToArray();
            }

            public bool TryAdd(byte[] data, out CombatFrame state)
            {
                state = null;
                if (data == null || data.Length <= HeaderBytes || data.Length > HeaderBytes + ChunkBytes) return false;
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    int owner=reader.ReadInt32();
                    if (owner<=0 || reader.ReadInt32() != Version) return false;
                    int id = reader.ReadInt32(), index = reader.ReadInt32(), total = reader.ReadInt32(), length = reader.ReadInt32();
                    if (id <= LastSnapshotId || length < 4 || length > MaxSnapshotBytes ||
                        total != (length + ChunkBytes - 1) / ChunkBytes || index < 0 || index >= total ||
                        data.Length != HeaderBytes + Math.Min(ChunkBytes, length - index * ChunkBytes)) return false;

                    Pending snapshot;
                    if (!pending.TryGetValue(id, out snapshot))
                    {
                        if (pending.Count >= 3)
                        {
                            int oldest = int.MaxValue;
                            foreach (int key in pending.Keys) oldest = Math.Min(oldest, key);
                            if (id < oldest) return false;
                            pending.Remove(oldest);
                        }
                        snapshot = new Pending { Length = length, Chunks = new byte[total][] };
                        pending.Add(id, snapshot);
                    }
                    if (snapshot.Length != length || snapshot.Chunks.Length != total || snapshot.Chunks[index] != null) return false;
                    snapshot.Chunks[index] = data;
                    if (++snapshot.Received != total) return false;
                    pending.Remove(id);
                    byte[] payload = new byte[length];
                    for (int i = 0; i < total; i++)
                        Buffer.BlockCopy(snapshot.Chunks[i], HeaderBytes, payload, i * ChunkBytes, snapshot.Chunks[i].Length - HeaderBytes);
                    try
                    {
                        CombatFrame result;
                        using (var input = new MemoryStream(payload))
                        using (var bodyReader = new BinaryReader(input))
                        {
                            result = ReadFrame(bodyReader);
                            if(result.Owner!=owner || result.Sequence!=id) throw new InvalidDataException("Combat identity mismatch.");
                            if (input.Position != input.Length) throw new InvalidDataException("Trailing progress data.");
                        }
                        LastSnapshotId = id;
                        CompletedChunks = snapshot.Chunks;
                        foreach (int key in new List<int>(pending.Keys)) if (key <= id) pending.Remove(key);
                        state = result;
                        return true;
                    }
                    catch (InvalidDataException) { return false; }
                    catch (IOException) { return false; }
                    catch (ArgumentException) { return false; }
                }
            }
        }
    }
}
