using System;
using System.Collections.Generic;
using System.IO;

namespace Orbit_Us
{
    // PlayerId is supplied by the relay using the sender's registered endpoint.
    public class EnemyDamageData
    {
        public int PlayerId;
        public int RequestId;
        public int EnemyId;
        public float Damage;
        public float HeadDamage;

        public byte[] Serialize()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PlayerId);
                writer.Write(RequestId);
                writer.Write(EnemyId);
                writer.Write(Damage);
                writer.Write(HeadDamage);
                return stream.ToArray();
            }
        }

        public static EnemyDamageData Deserialize(byte[] data)
        {
            if (data == null || data.Length != 20)
                throw new InvalidDataException("Invalid enemy damage packet.");

            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                var result = new EnemyDamageData
                {
                    PlayerId = reader.ReadInt32(),
                    RequestId = reader.ReadInt32(),
                    EnemyId = reader.ReadInt32(),
                    Damage = reader.ReadSingle(),
                    HeadDamage = reader.ReadSingle()
                };
                if (result.RequestId <= 0 || result.EnemyId <= 0 ||
                    !IsValidDamage(result.Damage) || !IsValidDamage(result.HeadDamage) ||
                    (result.Damage == 0f && result.HeadDamage == 0f))
                    throw new InvalidDataException("Invalid enemy damage values.");
                return result;
            }
        }

        private static bool IsValidDamage(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        }
    }

    // Retransmission must never cause a second hit, including out-of-order UDP.
    internal class DamageRequestHistory
    {
        private readonly HashSet<int> received = new HashSet<int>();
        private int newest;

        public bool Accept(int requestId)
        {
            if (requestId <= 0 || requestId <= newest - 4096 || !received.Add(requestId))
                return false;
            if (requestId > newest)
            {
                newest = requestId;
                received.RemoveWhere(id => id <= newest - 4096);
            }
            return true;
        }
    }
}
