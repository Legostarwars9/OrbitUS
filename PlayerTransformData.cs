using System.IO;

namespace Orbit_Us
{
    public class PlayerTransformData
    {
        public int PlayerId;
        public uint Sequence;
        public float X;
        public float Y;
        public float Rotation;

        public byte[] Serialize()
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(PlayerId);
                writer.Write(X);
                writer.Write(Y);
                writer.Write(Rotation);
                writer.Write(Sequence);

                return stream.ToArray();
            }
        }

        public static PlayerTransformData Deserialize(byte[] data)
        {
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                return new PlayerTransformData 
                {
                    PlayerId = reader.ReadInt32(),
                    X = reader.ReadSingle(),
                    Y = reader.ReadSingle(),
                    Rotation = reader.ReadSingle(),
                    Sequence = reader.ReadUInt32()
                };
            }
        }
    }
}