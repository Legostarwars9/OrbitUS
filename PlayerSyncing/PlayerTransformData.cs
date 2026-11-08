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
        public bool HasVitals;
        public float Health,MaxHealth,Fuel,MaxFuel;
        public int SpectatingPlayerId;
        public bool HasSampleTime;
        public double SampleTime;
        public float TimeScale=1;

        public static bool IsSupportedLength(int length)=>length==20 || length==32 || length==36 || length==44 || length==48 || length==52 || length==60 || length==64;

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
                writer.Write((IsHost?1:0) | (HasVelocity?2:0) | (HasLifeState?4:0) | (HasLifeState && Alive?8:0) | (HasSampleTime?16:0));
                if(HasVitals)
                {
                    writer.Write(Health);writer.Write(MaxHealth);
                    writer.Write(Fuel);writer.Write(MaxFuel);
                }
                if(SpectatingPlayerId!=0)writer.Write(SpectatingPlayerId);
                if(HasSampleTime){writer.Write(SampleTime);writer.Write(TimeScale);}

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
                int payloadLength=data.Length;
                if(data.Length>=32)
                {
                    value.VX=reader.ReadSingle();value.VY=reader.ReadSingle();
                    int flags=reader.ReadInt32();if((flags&~31)!=0 || ((flags&8)!=0 && (flags&4)==0))return false;
                    value.IsHost=(flags&1)!=0;value.HasVelocity=(flags&2)!=0;
                    value.HasLifeState=(flags&4)!=0;value.Alive=(flags&8)!=0;
                    value.HasSampleTime=(flags&16)!=0;
                    if(value.HasSampleTime)payloadLength-=12;
                    if(payloadLength!=32 && payloadLength!=36 && payloadLength!=48 && payloadLength!=52)return false;
                }
                if(payloadLength>=48)
                {
                    value.HasVitals=true;
                    value.Health=reader.ReadSingle();value.MaxHealth=reader.ReadSingle();
                    value.Fuel=reader.ReadSingle();value.MaxFuel=reader.ReadSingle();
                    foreach(float v in new[]{value.Health,value.MaxHealth,value.Fuel,value.MaxFuel})
                        if(float.IsNaN(v) || float.IsInfinity(v) || v<0)return false;
                    if(value.MaxHealth<=0 || value.MaxFuel<=0 || value.Health>value.MaxHealth || value.Fuel>value.MaxFuel)return false;
                }
                if(payloadLength==36 || payloadLength==52)
                {
                    value.SpectatingPlayerId=reader.ReadInt32();
                    if(value.SpectatingPlayerId<=0 || value.SpectatingPlayerId==value.PlayerId || !value.IsHost ||
                        !value.HasVelocity || !value.HasLifeState || value.Alive)return false;
                }
                if(value.HasSampleTime)
                {
                    value.SampleTime=reader.ReadDouble();value.TimeScale=reader.ReadSingle();
                    if(double.IsNaN(value.SampleTime) || double.IsInfinity(value.SampleTime) || value.SampleTime<0 ||
                        float.IsNaN(value.TimeScale) || float.IsInfinity(value.TimeScale) || value.TimeScale<0 || value.TimeScale>100)return false;
                }
                foreach(float v in new[]{value.X,value.Y,value.Rotation,value.VX,value.VY})
                    if(float.IsNaN(v) || float.IsInfinity(v))return false;
                if(float.IsInfinity(value.VX*value.TimeScale) || float.IsInfinity(value.VY*value.TimeScale))return false;
                motion=value;return true;
            }
        }
    }
}
