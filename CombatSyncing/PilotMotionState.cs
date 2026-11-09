using System;

namespace Orbit_Us
{
    // Combat must use the pilot's reported velocity, not packet arrival spacing.
    // Multiple physics samples can arrive in one frame or arrive in a burst.
    public sealed class PilotMotionState
    {
        private bool initialized;
        private uint sequence;
        private double sourceTime;
        private float x,y,vx,vy,scale=1;
        public bool Alive {get;private set;}
        public bool HasLifeState {get;private set;}
        public double Received {get;private set;}=double.NegativeInfinity;

        public bool Observe(PlayerTransformData motion,double arrival)
        {
            if(motion==null || double.IsNaN(arrival) || double.IsInfinity(arrival) ||
                !CombatRules.Finite(motion.X) || !CombatRules.Finite(motion.Y) || !CombatRules.Finite(motion.VX) || !CombatRules.Finite(motion.VY) ||
                !CombatRules.Finite(motion.TimeScale) || motion.TimeScale<0 || motion.TimeScale>100)return false;
            if(initialized && unchecked((int)(motion.Sequence-sequence))<=0)return false;
            double stamp=motion.HasSampleTime?motion.SampleTime:arrival;
            if(double.IsNaN(stamp) || double.IsInfinity(stamp))return false;
            if(motion.HasVelocity){vx=motion.VX;vy=motion.VY;scale=motion.TimeScale;}
            else if(initialized && stamp>sourceTime && stamp-sourceTime<1)
            {
                // Compatibility with older position-only movement. Prefer sender
                // timestamps where available; retain velocity on repeated ticks.
                vx=(float)((motion.X-x)/(stamp-sourceTime));vy=(float)((motion.Y-y)/(stamp-sourceTime));scale=1;
                float speed=(float)Math.Sqrt((double)vx*vx+(double)vy*vy);
                if(speed>500){vx*=500/speed;vy*=500/speed;}
            }
            x=motion.X;y=motion.Y;sourceTime=stamp;sequence=motion.Sequence;Received=arrival;initialized=true;
            if(motion.HasLifeState){Alive=motion.Alive;HasLifeState=true;}
            return true;
        }
        public void Presence(bool alive){if(!HasLifeState)Alive=alive;}
        public bool IsLiving(double now)=>initialized && Alive && now>=Received && now-Received<3;
        public MotionPose Predict(double now,float hostTimeScale)
        {
            float age=(float)Math.Min(.15,Math.Max(0,now-Received));
            float factor=hostTimeScale>0?scale/hostTimeScale:0;
            return new MotionPose {X=x+vx*scale*age,Y=y+vy*scale*age,VX=vx*factor,VY=vy*factor};
        }
    }
}
