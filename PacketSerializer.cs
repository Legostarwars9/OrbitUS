using System;
using System.IO;

namespace Orbit_Us
{
    public static class PacketSerializer
    {
        public static byte[] Serialize(NetworkPacket packet)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((int)packet.Type);
                writer.Write(packet.Data.Length);
                writer.Write(packet.Data);

                return stream.ToArray();
            }
        }
        public static NetworkPacket Deserialize(byte[] data)
        {
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                PacketType type = (PacketType)reader.ReadInt32();
                int dataLength = reader.ReadInt32();
                byte[] packetData = reader.ReadBytes(dataLength);

                return new NetworkPacket
                {
                    Type = type,
                    Data = packetData
                };
            }
        }
    }
}