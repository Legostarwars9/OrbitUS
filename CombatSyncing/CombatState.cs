using System;
using System.Collections.Generic;
using System.IO;

namespace Orbit_Us
{
    public sealed class CombatEntityState
    {
        public int Id;
        public string Prefab="";
        public byte Kind; // 0 projectile, 1 fuel, 2 health
        public float X,Y,VX,VY,Rotation,ScaleX=1,ScaleY=1,R=1,G=1,B=1,A=1,Amount=1;
        public bool PlayerSafe, Ended, Shield, Interceptor;
        public float CollisionRadius=0.5f;
        public List<float> LinePoints=new List<float>();
        public CombatEntityState Copy(){var copy=(CombatEntityState)MemberwiseClone();copy.LinePoints=new List<float>(LinePoints);return copy;}
    }
    public sealed class CombatFrame
    {
        public int Owner, Sequence;
        public bool Alive, Authority;
        public List<CombatEntityState> Entities=new List<CombatEntityState>();
    }
    public sealed class CombatHit
    {
        public int Id,PlayerId;
        public float Damage,Heat;
        public byte[] Serialize()
        {
            using(var s=new MemoryStream())using(var w=new BinaryWriter(s))
            {w.Write(Id);w.Write(PlayerId);w.Write(Damage);w.Write(Heat);return s.ToArray();}
        }
        public static CombatHit Deserialize(byte[] bytes)
        {
            if(bytes==null || bytes.Length!=16)throw new InvalidDataException("Invalid combat hit.");
            using(var r=new BinaryReader(new MemoryStream(bytes)))
            {
                var h=new CombatHit {Id=r.ReadInt32(),PlayerId=r.ReadInt32(),Damage=r.ReadSingle(),Heat=r.ReadSingle()};
                if(h.Id<=0 || h.PlayerId<=0 || !CombatRules.Finite(h.Damage) || !CombatRules.Finite(h.Heat) || h.Damage<0)
                    throw new InvalidDataException("Invalid combat hit values.");
                return h;
            }
        }
    }
    public static class CombatRules
    {
        public static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
        // Stable tie breaking prevents peers at equal distance causing target flicker.
        public static bool Prefer(float distance,int id,float bestDistance,int bestId)
            =>Finite(distance) && distance>=0 && (distance<bestDistance || (distance==bestDistance && id<bestId));
    }
}
