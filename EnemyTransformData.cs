using System.IO;

namespace Orbit_Us
{
    public class EnemyTransformData
    {
        public int EnemyId;
        public int Type;
        public float X;
        public float Y;
        public float HP;

        public byte[] Serialize()
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(EnemyId);
                writer.Write(Type);
                writer.Write(X);
                writer.Write(Y);
                writer.Write(HP);

                return stream.ToArray();
            }
        }

        public static EnemyTransformData Deserialize(byte[] data)
        {
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                return new EnemyTransformData
                {
                    EnemyId = reader.ReadInt32(),
                    Type = reader.ReadInt32(),
                    X = reader.ReadSingle(),
                    Y = reader.ReadSingle(),
                    HP = reader.ReadSingle()
                };
            }
        }
    }
}