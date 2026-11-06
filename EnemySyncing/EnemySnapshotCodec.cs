using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace Orbit_Us
{
    public static class EnemySnapshotCodec
    {
        private const int Version=3,HeaderBytes=20,ChunkBytes=1080;
        public const int MaxEnemies=20000,MaxSnapshotBytes=8*1024*1024;
        private static readonly Encoding Utf8=new UTF8Encoding(false,true);
        public static List<byte[]> Encode(List<EnemyTransformData> state, int snapshotId)
        {
            if (snapshotId <= 0 || state == null) throw new InvalidDataException("Invalid progress snapshot.");
            byte[] payload;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteEnemies(writer, state);
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
                if (reader.ReadInt32() != Version) return false;
                int id = reader.ReadInt32(), index = reader.ReadInt32(), total = reader.ReadInt32(), length = reader.ReadInt32();
                if (id <= 0 || length < 4 || length > MaxSnapshotBytes ||
                    total != (length + ChunkBytes - 1) / ChunkBytes || index < 0 || index >= total ||
                    data.Length != HeaderBytes + Math.Min(ChunkBytes, length - index * ChunkBytes)) return false;
                snapshotId = id; chunkIndex = index; chunkCount = total;
                return true;
            }
        }


        private static void String(BinaryWriter w,string text)
        {byte[] b=Utf8.GetBytes(text);if(b.Length>256)throw new InvalidDataException();w.Write(b.Length);w.Write(b);}
        private static string String(BinaryReader r)
        {int n=Count(r,256);byte[] b=r.ReadBytes(n);if(b.Length!=n)throw new EndOfStreamException();return Utf8.GetString(b);}
        private static int Count(BinaryReader r,int max)
        {int n=r.ReadInt32();if(n<0 || n>max)throw new InvalidDataException();return n;}
        private static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
        private static void Validate(List<EnemyTransformData> list)
        {
            if(list.Count>MaxEnemies)throw new InvalidDataException();var ids=new HashSet<int>();
            foreach(var e in list)
            {
                if(e.EnemyId<=0 || !ids.Add(e.EnemyId) || e.Type<0 || e.Nodes.Count>512)throw new InvalidDataException();
                foreach(float v in new[]{e.X,e.Y,e.Rotation,e.HP,e.HeadHP})if(!Finite(v))throw new InvalidDataException();
                var paths=new HashSet<string>();
                foreach(var n in e.Nodes)
                {
                    if(n.Path==null || n.Sprite==null || !paths.Add(n.Path) || n.HealthId<0 || n.LinePoints.Count>384 || n.LinePoints.Count%3!=0)throw new InvalidDataException();
                    if(n.Path!="")foreach(string part in n.Path.Split('/'))if(!int.TryParse(part,out int index) || index<0 || index>2048)throw new InvalidDataException();
                    foreach(float v in new[]{n.X,n.Y,n.Z,n.Rotation,n.ScaleX,n.ScaleY,n.ScaleZ,n.R,n.G,n.B,n.A,n.HP,n.MaxHP})if(!Finite(v))throw new InvalidDataException();
                    foreach(float v in n.LinePoints)if(!Finite(v))throw new InvalidDataException();
                }
            }
        }
        private static void WriteEnemies(BinaryWriter w,List<EnemyTransformData> list)
        {
            Validate(list);w.Write(list.Count);
            foreach(var e in list)
            {
                w.Write(e.Serialize());w.Write(e.Nodes.Count);
                foreach(var n in e.Nodes)
                {
                    String(w,n.Path);String(w,n.Sprite);w.Write(n.Active);w.Write(n.RendererEnabled);w.Write(n.HealthId);
                    foreach(float v in new[]{n.X,n.Y,n.Z,n.Rotation,n.ScaleX,n.ScaleY,n.ScaleZ,n.R,n.G,n.B,n.A,n.HP,n.MaxHP})w.Write(v);
                    w.Write(n.LinePoints.Count);foreach(float v in n.LinePoints)w.Write(v);
                }
            }
        }
        private static List<EnemyTransformData> ReadEnemies(BinaryReader r)
        {
            var list=new List<EnemyTransformData>();int count=Count(r,MaxEnemies);
            for(int i=0;i<count;i++)
            {
                var e=EnemyTransformData.Deserialize(r.ReadBytes(28));int nodes=Count(r,512);
                for(int j=0;j<nodes;j++)
                {
                    var n=new EnemyNodePose {Path=String(r),Sprite=String(r),Active=r.ReadBoolean(),RendererEnabled=r.ReadBoolean(),HealthId=r.ReadInt32(),X=r.ReadSingle(),Y=r.ReadSingle(),Z=r.ReadSingle(),Rotation=r.ReadSingle(),ScaleX=r.ReadSingle(),ScaleY=r.ReadSingle(),ScaleZ=r.ReadSingle(),R=r.ReadSingle(),G=r.ReadSingle(),B=r.ReadSingle(),A=r.ReadSingle(),HP=r.ReadSingle(),MaxHP=r.ReadSingle()};
                    int points=Count(r,384);for(int k=0;k<points;k++)n.LinePoints.Add(r.ReadSingle());e.Nodes.Add(n);
                }
                list.Add(e);
            }
            Validate(list);return list;
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

            public bool TryAdd(byte[] data, out List<EnemyTransformData> state)
            {
                state = null;
                if (data == null || data.Length <= HeaderBytes || data.Length > HeaderBytes + ChunkBytes) return false;
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    if (reader.ReadInt32() != Version) return false;
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
                        List<EnemyTransformData> result;
                        using (var input = new MemoryStream(payload))
                        using (var bodyReader = new BinaryReader(input))
                        {
                            result = ReadEnemies(bodyReader);
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
