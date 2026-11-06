using System.IO;
using System.Collections.Generic;

namespace Orbit_Us
{
    public class EnemyTransformData
    {
        public int EnemyId;
        public int Type;
        public float X;
        public float Y;
        public float HP;
        public float HeadHP;
        public float Rotation;
        public List<EnemyNodePose> Nodes=new List<EnemyNodePose>();

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
                writer.Write(HeadHP);
                writer.Write(Rotation);

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
                    HP = reader.ReadSingle(),
                    HeadHP = reader.ReadSingle(),
                    Rotation = reader.ReadSingle()
                };
            }
        }
    }
    public sealed class EnemyNodePose
    {
        public string Path="",Sprite="";
        public float X,Y,Z,Rotation,ScaleX=1,ScaleY=1,ScaleZ=1,R=1,G=1,B=1,A=1;
        public bool Active=true,RendererEnabled=true;
        public int HealthId;
        public float HP,MaxHP;
        public List<float> LinePoints=new List<float>();
    }

}
