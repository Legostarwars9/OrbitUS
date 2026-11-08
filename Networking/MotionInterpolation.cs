using System;
using System.Diagnostics;

namespace Orbit_Us
{
    public static class NetworkTime
    {
        private static double frame;
        public static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        public static void BeginFrame(double frameAge){frame=Now-Math.Max(0,frameAge);}
        public static double PhysicsSample(double lag,float scale)=>(frame==0?Now:frame)-(scale>0?Math.Max(0,lag)/scale:0);
    }

    public struct MotionPose
    {
        public float X,Y,Rotation,VX,VY,AngularVelocity;
    }

    // Maps a remote monotonic clock to this process without assuming matching
    // game start times. A better delay estimate slews in rather than jumping.
    public sealed class RemoteMotionClock
    {
        private bool initialized;
        private double offset,minimum,lastSource=double.NegativeInfinity;
        public void Observe(double source,double arrival)
        {
            if(source<=lastSource)return;
            lastSource=source;
            double observed=arrival-source;
            if(!initialized){offset=minimum=observed;initialized=true;return;}
            minimum=Math.Min(minimum,observed);
            offset=Math.Max(minimum,offset-.001);
        }
        public double SourceTime(double now)=>now-offset;
        public void Clear(){initialized=false;offset=minimum=0;lastSource=double.NegativeInfinity;}
    }

    public sealed class MotionInterpolation
    {
        private struct Sample {public double Time;public MotionPose Pose;}
        private readonly Sample[] samples=new Sample[16];
        private int count;
        private uint sequence;
        public double LastArrival {get;private set;}
        public bool Add(uint next,double time,double arrival,MotionPose pose)
        {
            if(double.IsNaN(time) || double.IsInfinity(time) || double.IsNaN(arrival) || double.IsInfinity(arrival) ||
                !Finite(pose.X) || !Finite(pose.Y) || !Finite(pose.Rotation) || !Finite(pose.VX) || !Finite(pose.VY) || !Finite(pose.AngularVelocity))return false;
            if(count>0 && (unchecked((int)(next-sequence))<=0 || time<samples[count-1].Time))return false;
            if(count>0)
            {
                Sample last=samples[count-1];double dt=time-last.Time;
                double dx=pose.X-(last.Pose.X+last.Pose.VX*dt),dy=pose.Y-(last.Pose.Y+last.Pose.VY*dt);
                if(dt>2 || dx*dx+dy*dy>128*128)count=0;
            }
            if(count>0 && time==samples[count-1].Time)
            {samples[count-1]=new Sample {Time=time,Pose=pose};sequence=next;LastArrival=arrival;return true;}
            if(count==samples.Length){Array.Copy(samples,1,samples,0,--count);}
            samples[count++]=new Sample {Time=time,Pose=pose};sequence=next;LastArrival=arrival;
            return true;
        }
        public bool Evaluate(double time,out MotionPose pose)
        {
            pose=default(MotionPose);if(count==0)return false;
            if(time<=samples[0].Time){pose=samples[0].Pose;return true;}
            for(int i=1;i<count;i++)
            {
                Sample a=samples[i-1],b=samples[i];
                if(time>b.Time)continue;
                float dt=(float)(b.Time-a.Time),t=(float)((time-a.Time)/dt);
                pose=b.Pose;
                pose.X=Hermite(a.Pose.X,b.Pose.X,a.Pose.VX,b.Pose.VX,dt,t);
                pose.Y=Hermite(a.Pose.Y,b.Pose.Y,a.Pose.VY,b.Pose.VY,dt,t);
                pose.Rotation=a.Pose.Rotation+AngleDelta(a.Pose.Rotation,b.Pose.Rotation)*t;
                return true;
            }
            Sample latest=samples[count-1];pose=latest.Pose;
            float age=(float)Math.Min(.25,Math.Max(0,time-latest.Time));
            pose.X+=pose.VX*age;pose.Y+=pose.VY*age;pose.Rotation+=pose.AngularVelocity*age;
            return true;
        }
        private static float Hermite(float a,float b,float av,float bv,float dt,float t)
        {
            float limit=3*Math.Abs(b-a);
            float m0=Math.Max(-limit,Math.Min(limit,av*dt)),m1=Math.Max(-limit,Math.Min(limit,bv*dt));
            float t2=t*t,t3=t2*t;
            return (2*t3-3*t2+1)*a+(t3-2*t2+t)*m0+(-2*t3+3*t2)*b+(t3-t2)*m1;
        }
        public static float AngleDelta(float a,float b)
        {float delta=(b-a)%360;if(delta>180)delta-=360;if(delta< -180)delta+=360;return delta;}
        private static bool Finite(float value)=>!float.IsNaN(value) && !float.IsInfinity(value);
    }
}
