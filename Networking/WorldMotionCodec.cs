using System;
using System.Collections.Generic;
using System.IO;

namespace Orbit_Us
{
    public struct WorldMotion
    {
        public int Id;
        public float X,Y,VX,VY,Rotation,AngularVelocity,HP,HeadHP;
        public MotionPose Pose(float scale)=>new MotionPose {X=X,Y=Y,Rotation=Rotation,VX=VX*scale,VY=VY*scale,AngularVelocity=AngularVelocity*scale};
    }
    public sealed class WorldMotionBatch
    {
        public bool Planets;
        public uint Sequence;
        public double Time;
        public float TimeScale=1;
        public List<WorldMotion> Entities=new List<WorldMotion>();
    }
    public static class WorldMotionCodec
    {
        public const int MaximumPacketBytes=920;
        public static List<byte[]> Encode(WorldMotionBatch batch)
        {
            ValidateHeader(batch);
            int size=batch.Planets?26:34,capacity=(MaximumPacketBytes-20)/size;
            var packets=new List<byte[]>();var ids=new HashSet<int>();
            for(int first=0;first<batch.Entities.Count;first+=capacity)
            {
                int count=Math.Min(capacity,batch.Entities.Count-first);
                using(var stream=new MemoryStream(20+count*size))using(var w=new BinaryWriter(stream))
                {
                    w.Write((byte)1);w.Write(batch.Planets);w.Write((ushort)count);w.Write(batch.Sequence);w.Write(batch.Time);w.Write(batch.TimeScale);
                    for(int i=first;i<first+count;i++)
                    {
                        var e=batch.Entities[i];Validate(e,batch.TimeScale);if(!ids.Add(e.Id))throw new InvalidDataException("Duplicate motion ID.");
                        w.Write(e.Id);w.Write(e.X);w.Write(e.Y);w.Write(e.VX);w.Write(e.VY);
                        double angle=(e.Rotation%360+360)%360;w.Write((ushort)Math.Round(angle*65535/360));w.Write(e.AngularVelocity);
                        if(!batch.Planets){w.Write(e.HP);w.Write(e.HeadHP);}
                    }
                    packets.Add(stream.ToArray());
                }
            }
            return packets;
        }
        public static bool TryDecode(byte[] bytes,out WorldMotionBatch batch)
        {
            batch=null;if(bytes==null || bytes.Length<20 || bytes.Length>MaximumPacketBytes)return false;
            try
            {
                using(var r=new BinaryReader(new MemoryStream(bytes)))
                {
                    if(r.ReadByte()!=1)return false;
                    byte kind=r.ReadByte();if(kind>1)return false;
                    int count=r.ReadUInt16();
                    var result=new WorldMotionBatch {Planets=kind==1,Sequence=r.ReadUInt32(),Time=r.ReadDouble(),TimeScale=r.ReadSingle()};
                    if(count==0 || bytes.Length!=20+count*(result.Planets?26:34))return false;
                    ValidateHeader(result);var ids=new HashSet<int>();
                    for(int i=0;i<count;i++)
                    {
                        var e=new WorldMotion {Id=r.ReadInt32(),X=r.ReadSingle(),Y=r.ReadSingle(),VX=r.ReadSingle(),VY=r.ReadSingle(),Rotation=r.ReadUInt16()*(360f/65535),AngularVelocity=r.ReadSingle()};
                        if(!result.Planets){e.HP=r.ReadSingle();e.HeadHP=r.ReadSingle();}
                        Validate(e,result.TimeScale);if(!ids.Add(e.Id))return false;result.Entities.Add(e);
                    }
                    batch=result;return true;
                }
            }
            catch(IOException){return false;}
        }
        private static bool Finite(double value)=>!double.IsNaN(value) && !double.IsInfinity(value);
        private static void ValidateHeader(WorldMotionBatch b)
        {if(b==null || !Finite(b.Time) || b.Time<0 || !Finite(b.TimeScale) || b.TimeScale<0 || b.TimeScale>100 || b.Entities.Count>20000)throw new InvalidDataException("Invalid motion frame.");}
        private static void Validate(WorldMotion e,float scale)
        {if(e.Id<=0 || !Finite(e.X) || !Finite(e.Y) || !Finite(e.VX*scale) || !Finite(e.VY*scale) || !Finite(e.Rotation) || !Finite(e.AngularVelocity*scale) || !Finite(e.HP) || !Finite(e.HeadHP))throw new InvalidDataException("Invalid motion values.");}
    }

    public sealed class WorldMotionSender
    {
        private struct Sent {public WorldMotion Motion;public double Time;public float Scale;}
        private readonly Dictionary<int,Sent> last=new Dictionary<int,Sent>();
        public bool Changed(WorldMotion motion,double now,float scale)
        {
            bool send=!last.TryGetValue(motion.Id,out Sent previous);
            if(!send)
            {
                double dt=now-previous.Time;
                double dx=motion.X-(previous.Motion.X+previous.Motion.VX*previous.Scale*dt),dy=motion.Y-(previous.Motion.Y+previous.Motion.VY*previous.Scale*dt);
                double vx=motion.VX-previous.Motion.VX,vy=motion.VY-previous.Motion.VY;
                send=dt<0 || dt>=.2 || scale!=previous.Scale || dx*dx+dy*dy>.04 || vx*vx+vy*vy>.25 ||
                    Math.Abs(MotionInterpolation.AngleDelta(previous.Motion.Rotation+previous.Motion.AngularVelocity*previous.Scale*(float)dt,motion.Rotation))>1 ||
                    Math.Abs(motion.AngularVelocity-previous.Motion.AngularVelocity)>1 || motion.HP!=previous.Motion.HP || motion.HeadHP!=previous.Motion.HeadHP;
            }
            if(send)last[motion.Id]=new Sent {Motion=motion,Time=now,Scale=scale};return send;
        }
        public void Remove(int id)=>last.Remove(id);
        public void Clear()=>last.Clear();
    }

    public sealed class WorldMotionStream
    {
        private sealed class Track {public readonly MotionInterpolation History=new MotionInterpolation();public WorldMotion Latest;}
        private readonly Dictionary<int,Track> tracks=new Dictionary<int,Track>();
        private readonly RemoteMotionClock clock=new RemoteMotionClock();
        public void Observe(WorldMotionBatch batch,double arrival)
        {
            clock.Observe(batch.Time,arrival);
            foreach(var entity in batch.Entities)
            {
                if(!tracks.TryGetValue(entity.Id,out Track track))
                {
                    if(tracks.Count>=20000)continue;
                    track=new Track();tracks.Add(entity.Id,track);
                }
                if(track.History.Add(batch.Sequence,batch.Time,arrival,entity.Pose(batch.TimeScale)))track.Latest=entity;
            }
        }
        public bool Sample(int id,double now,out MotionPose pose)
        {pose=default(MotionPose);return tracks.TryGetValue(id,out Track t) && t.History.Evaluate(clock.SourceTime(now)-.075,out pose);}
        public bool Latest(int id,out WorldMotion motion)
        {motion=default(WorldMotion);if(!tracks.TryGetValue(id,out Track t))return false;motion=t.Latest;return true;}
        public bool Fresh(int id,double now)=>tracks.TryGetValue(id,out Track t) && now-t.History.LastArrival<=.35;
        public void Remove(int id)=>tracks.Remove(id);
        public void Clear(){tracks.Clear();clock.Clear();}
    }
}
