using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Orbit_Us
{
    public sealed class PlanetState
    {
        public int Id, PrefabKey, ParentId;
        public string Name = "", Tag = "";
        public bool Active, CentralBlackHole, IsBlackHole, MainSequence, PostMainSequence, HasBoss;
        public float X, Y, Rotation, ScaleX = 1f, ScaleY = 1f, Mass = 1f;
        public float VelocityX, VelocityY, AngularVelocity, Heat;
        public List<PlanetVisualState> Visuals = new List<PlanetVisualState>();
        public List<PlanetShopState> Shops = new List<PlanetShopState>();
    }

    public sealed class PlanetVisualState
    {
        // Sibling indices, separated by '/', relative to the celestial root.
        public string Path = "", SpriteName = "";
        public float R = 1f, G = 1f, B = 1f, A = 1f, ScaleX = 1f, ScaleY = 1f;
        public bool Active = true, Enabled = true;
        public float AtmosphereDrag = -1f;
    }

    public sealed class PlanetShopState
    {
        public string Path = "";
        public int[] ItemIds = new int[6];
        public int[] ItemTypes = new int[6];
        public int[] PriceMultipliers = new[] { 1, 1, 1, 1, 1, 1 };
    }

    // The variable-size world is serialized once, then split into datagrams.
    // Only a completely validated world may cause spawning or removal.
    public static class PlanetSnapshotCodec
    {
        private const int Version = 1;
        private const int HeaderBytes = 20;
        private const int ChunkBytes = 1080;
        private const int MaxStringBytes = 256;
        private const int MaxChildren = 256;
        public const int MaxPlanets = 10000;
        public const int MaxSnapshotBytes = 8 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static List<byte[]> Encode(List<PlanetState> planets, int snapshotId)
        {
            if (snapshotId <= 0 || planets == null || planets.Count > MaxPlanets)
                throw new InvalidDataException("Invalid planet snapshot size or ID.");
            byte[] payload;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(planets.Count);
                var ids = new HashSet<int>();
                foreach (PlanetState planet in planets)
                {
                    Validate(planet);
                    if (!ids.Add(planet.Id)) throw new InvalidDataException("Duplicate planet ID.");
                    WritePlanet(writer, planet);
                    if (stream.Length > MaxSnapshotBytes)
                        throw new InvalidDataException("Planet snapshot exceeds the byte budget.");
                }
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

        private static void WritePlanet(BinaryWriter writer, PlanetState p)
        {
            writer.Write(p.Id); writer.Write(p.PrefabKey); writer.Write(p.ParentId);
            WriteString(writer, p.Name); WriteString(writer, p.Tag);
            writer.Write(p.Active); writer.Write(p.CentralBlackHole); writer.Write(p.IsBlackHole);
            writer.Write(p.MainSequence); writer.Write(p.PostMainSequence); writer.Write(p.HasBoss);
            writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Rotation);
            writer.Write(p.ScaleX); writer.Write(p.ScaleY); writer.Write(p.Mass);
            writer.Write(p.VelocityX); writer.Write(p.VelocityY); writer.Write(p.AngularVelocity); writer.Write(p.Heat);
            writer.Write(p.Visuals.Count);
            foreach (var visual in p.Visuals)
            {
                WriteString(writer, visual.Path); WriteString(writer, visual.SpriteName);
                writer.Write(visual.R); writer.Write(visual.G); writer.Write(visual.B); writer.Write(visual.A);
                writer.Write(visual.ScaleX); writer.Write(visual.ScaleY);
                writer.Write(visual.Active); writer.Write(visual.Enabled); writer.Write(visual.AtmosphereDrag);
            }
            writer.Write(p.Shops.Count);
            foreach (var shop in p.Shops)
            {
                WriteString(writer, shop.Path);
                for (int i = 0; i < 6; i++)
                {
                    writer.Write(shop.ItemIds[i]); writer.Write(shop.ItemTypes[i]); writer.Write(shop.PriceMultipliers[i]);
                }
            }
        }

        private static PlanetState ReadPlanet(BinaryReader reader)
        {
            var p = new PlanetState
            {
                Id = reader.ReadInt32(), PrefabKey = reader.ReadInt32(), ParentId = reader.ReadInt32(),
                Name = ReadString(reader), Tag = ReadString(reader),
                Active = ReadBool(reader), CentralBlackHole = ReadBool(reader), IsBlackHole = ReadBool(reader),
                MainSequence = ReadBool(reader), PostMainSequence = ReadBool(reader), HasBoss = ReadBool(reader),
                X = reader.ReadSingle(), Y = reader.ReadSingle(), Rotation = reader.ReadSingle(),
                ScaleX = reader.ReadSingle(), ScaleY = reader.ReadSingle(), Mass = reader.ReadSingle(),
                VelocityX = reader.ReadSingle(), VelocityY = reader.ReadSingle(), AngularVelocity = reader.ReadSingle(),
                Heat = reader.ReadSingle()
            };
            int visuals = ReadCount(reader, MaxChildren);
            for (int i = 0; i < visuals; i++)
                p.Visuals.Add(new PlanetVisualState
                {
                    Path = ReadString(reader), SpriteName = ReadString(reader),
                    R = reader.ReadSingle(), G = reader.ReadSingle(), B = reader.ReadSingle(), A = reader.ReadSingle(),
                    ScaleX = reader.ReadSingle(), ScaleY = reader.ReadSingle(),
                    Active = ReadBool(reader), Enabled = ReadBool(reader), AtmosphereDrag = reader.ReadSingle()
                });
            int shops = ReadCount(reader, MaxChildren);
            for (int i = 0; i < shops; i++)
            {
                var shop = new PlanetShopState { Path = ReadString(reader) };
                for (int slot = 0; slot < 6; slot++)
                {
                    shop.ItemIds[slot] = reader.ReadInt32();
                    shop.ItemTypes[slot] = reader.ReadInt32();
                    shop.PriceMultipliers[slot] = reader.ReadInt32();
                }
                p.Shops.Add(shop);
            }
            Validate(p);
            return p;
        }

        private static int ReadCount(BinaryReader reader, int maximum)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum) throw new InvalidDataException("Invalid planet collection size.");
            return count;
        }

        private static bool ReadBool(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new InvalidDataException("Invalid boolean value.");
            return value == 1;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value ?? "");
            if (bytes.Length > MaxStringBytes) throw new InvalidDataException("Planet string is too long.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length > MaxStringBytes) throw new InvalidDataException("Planet string is too long.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Utf8.GetString(bytes);
        }

        private static void Validate(PlanetState p)
        {
            if (p == null || p.Id <= 0 || p.ParentId < 0 || p.ParentId == p.Id ||
                p.Visuals == null || p.Visuals.Count > MaxChildren || p.Shops == null || p.Shops.Count > MaxChildren ||
                !Finite(p.X, p.Y, p.Rotation, p.ScaleX, p.ScaleY, p.Mass, p.VelocityX, p.VelocityY, p.AngularVelocity, p.Heat) ||
                p.Mass <= 0f)
                throw new InvalidDataException("Invalid planet state.");
            var paths = new HashSet<string>();
            foreach (var visual in p.Visuals)
            {
                if (visual == null || !ValidPath(visual.Path) || !paths.Add(visual.Path) ||
                    !Finite(visual.R, visual.G, visual.B, visual.A, visual.ScaleX, visual.ScaleY, visual.AtmosphereDrag))
                    throw new InvalidDataException("Invalid planet visual state.");
            }
            paths.Clear();
            foreach (var shop in p.Shops)
            {
                if (shop == null || !ValidPath(shop.Path) || !paths.Add(shop.Path) ||
                    shop.ItemIds == null || shop.ItemIds.Length != 6 || shop.ItemTypes == null || shop.ItemTypes.Length != 6 ||
                    shop.PriceMultipliers == null || shop.PriceMultipliers.Length != 6)
                    throw new InvalidDataException("Invalid planet shop state.");
                for (int i = 0; i < 6; i++)
                    if (shop.ItemIds[i] < -2 || shop.ItemTypes[i] < 0 || shop.ItemTypes[i] > 2 || shop.PriceMultipliers[i] < 1)
                        throw new InvalidDataException("Invalid shop slot.");
            }
        }

        private static bool ValidPath(string path)
        {
            if (path == null || path.Length > MaxStringBytes) return false;
            if (path.Length == 0) return true;
            string[] parts = path.Split('/');
            if (parts.Length > 32) return false;
            foreach (string part in parts)
            {
                int index;
                if (part.Length == 0 || !int.TryParse(part, out index) || index < 0 || index > 4095) return false;
                foreach (char c in part) if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static bool Finite(params float[] values)
        {
            foreach (float value in values)
                if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            return true;
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

            public bool TryAdd(byte[] data, out List<PlanetState> planets)
            {
                planets = null;
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
                        var result = new List<PlanetState>();
                        var ids = new HashSet<int>();
                        using (var input = new MemoryStream(payload))
                        using (var bodyReader = new BinaryReader(input))
                        {
                            int count = ReadCount(bodyReader, MaxPlanets);
                            for (int i = 0; i < count; i++)
                            {
                                PlanetState planet = ReadPlanet(bodyReader);
                                if (!ids.Add(planet.Id)) throw new InvalidDataException("Duplicate planet ID.");
                                result.Add(planet);
                            }
                            if (input.Position != input.Length) throw new InvalidDataException("Trailing planet snapshot data.");
                        }
                        LastSnapshotId = id;
                        CompletedChunks = snapshot.Chunks;
                        foreach (int key in new List<int>(pending.Keys)) if (key <= id) pending.Remove(key);
                        planets = result;
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
