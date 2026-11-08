using System;
using System.IO;
using System.IO.Compression;

namespace Orbit_Us
{
    public static class PacketSerializer
    {
        public static byte[] Serialize(NetworkPacket packet)
        {
            byte[] payload=packet.Data;PacketType type=packet.Type;
            if(Compressible(type) && payload.Length>=160 && payload.Length<=1100)
            {
                using(var stream=new MemoryStream())
                {
                    byte[] prefix=new byte[8];WriteInt(prefix,0,(int)type);WriteInt(prefix,4,payload.Length);stream.Write(prefix,0,8);
                    using(var compressed=new DeflateStream(stream,CompressionLevel.Fastest,true))compressed.Write(payload,0,payload.Length);
                    if(stream.Length+16<payload.Length){payload=stream.ToArray();type=PacketType.CompressedSnapshot;}
                }
            }
            var result=new byte[8+payload.Length];WriteInt(result,0,(int)type);WriteInt(result,4,payload.Length);
            Buffer.BlockCopy(payload,0,result,8,payload.Length);return result;
        }
        public static NetworkPacket Deserialize(byte[] data)
        {
            if (data == null || data.Length < 8)
                throw new InvalidDataException("Incomplete packet header.");
            int kind=ReadInt(data,0),length=ReadInt(data,4);
            if(kind<0 || kind>(int)PacketType.CompressedSnapshot || length<0 || length!=data.Length-8)
                throw new InvalidDataException("Invalid packet type or length.");
            var type=(PacketType)kind;
            if(type==PacketType.CompressedSnapshot)
            {
                if(length<9 || data.Length>1108)throw new InvalidDataException("Invalid compressed packet.");
                type=(PacketType)ReadInt(data,8);int expanded=ReadInt(data,12);
                if(!Compressible(type) || expanded<1 || expanded>1100)throw new InvalidDataException("Invalid compressed payload.");
                byte[] payload=new byte[expanded];
                using(var stream=new MemoryStream(data,16,data.Length-16))
                using(var compressed=new DeflateStream(stream,CompressionMode.Decompress))
                {
                    int offset=0;
                    while(offset<expanded)
                    {int read=compressed.Read(payload,offset,expanded-offset);if(read==0)throw new InvalidDataException("Truncated compressed payload.");offset+=read;}
                    if(compressed.ReadByte()!=-1)throw new InvalidDataException("Oversized compressed payload.");
                }
                return new NetworkPacket {Type=type,Data=payload};
            }
            byte[] bytes=new byte[length];Buffer.BlockCopy(data,8,bytes,0,length);
            return new NetworkPacket {Type=type,Data=bytes};
        }
        private static bool Compressible(PacketType type)=>type==PacketType.EnemySnapshot || type==PacketType.PlanetSnapshot ||
            type==PacketType.ProgressSnapshot || type==PacketType.CombatSnapshot || type==PacketType.EnemyMotion || type==PacketType.PlanetMotion;
        private static int ReadInt(byte[] data,int at)=>data[at] | data[at+1]<<8 | data[at+2]<<16 | data[at+3]<<24;
        private static void WriteInt(byte[] data,int at,int value)
        {data[at]=(byte)value;data[at+1]=(byte)(value>>8);data[at+2]=(byte)(value>>16);data[at+3]=(byte)(value>>24);}
    }
}
