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
        public float VX, VY;
        public bool IsHost, HasVelocity;
        public bool HasLifeState,Alive;

        public static bool IsSupportedLength(int length)=>length==20 || length==32;

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
                writer.Write(VX);
                writer.Write(VY);
                writer.Write((IsHost?1:0) | (HasVelocity?2:0) | (HasLifeState?4:0) | (HasLifeState && Alive?8:0));

                return stream.ToArray();
            }
        }

        public static PlayerTransformData Deserialize(byte[] data)
        {
            if(!TryDeserialize(data,out PlayerTransformData motion))
                throw new InvalidDataException("Invalid player movement.");
            return motion;
        }

        public static bool TryDeserialize(byte[] data,out PlayerTransformData motion)
        {
            motion=null;
            if(data==null || !IsSupportedLength(data.Length))return false;
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                var value=new PlayerTransformData
                {
                    PlayerId = reader.ReadInt32(),
                    X = reader.ReadSingle(),
                    Y = reader.ReadSingle(),
                    Rotation = reader.ReadSingle(),
                    Sequence = reader.ReadUInt32()
                };
                if(data.Length==32)
                {
                    value.VX=reader.ReadSingle();value.VY=reader.ReadSingle();
                    int flags=reader.ReadInt32();if((flags&~15)!=0 || ((flags&8)!=0 && (flags&4)==0))return false;
                    value.IsHost=(flags&1)!=0;value.HasVelocity=(flags&2)!=0;
                    value.HasLifeState=(flags&4)!=0;value.Alive=(flags&8)!=0;
                }
                foreach(float v in new[]{value.X,value.Y,value.Rotation,value.VX,value.VY})
                    if(float.IsNaN(v) || float.IsInfinity(v))return false;
                motion=value;return true;
            }
        }
    }
}
