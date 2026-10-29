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
            if (data == null || data.Length < 8)
                throw new InvalidDataException("Incomplete packet header.");
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                PacketType type = (PacketType)reader.ReadInt32();
                int dataLength = reader.ReadInt32();
                if (!Enum.IsDefined(typeof(PacketType), type) || dataLength < 0 || dataLength != data.Length - 8)
                    throw new InvalidDataException("Invalid packet type or length.");
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
