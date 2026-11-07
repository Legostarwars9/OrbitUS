using System;

namespace Orbit_Us
{
    // Uses the newest relay-authenticated host motion, without Unity dependencies.
    public sealed class HostRecallState
    {
        public const float MaximumDistance=10000f;
        public const double CountdownSeconds=5;
        private const double MaximumAge=1.0;
        private PlayerTransformData host;
        private double received, outsideSince=double.NaN;
        public int SecondsRemaining {get;private set;}

        public void Observe(PlayerTransformData motion,double now)
        {
            if(motion==null || !motion.IsHost || motion.PlayerId<=0 || !motion.HasVelocity ||
                !Finite(now) || !Finite(motion.X) || !Finite(motion.Y) || !Finite(motion.VX) || !Finite(motion.VY))return;
            if(host!=null && host.PlayerId==motion.PlayerId && unchecked((int)(motion.Sequence-host.Sequence))<=0)return;
            if(host!=null && host.PlayerId!=motion.PlayerId)CancelCountdown();
            host=new PlayerTransformData {PlayerId=motion.PlayerId,Sequence=motion.Sequence,
                X=motion.X,Y=motion.Y,VX=motion.VX,VY=motion.VY,IsHost=true,HasVelocity=true};
            received=now;
        }

        public bool TryRecall(float x,float y,double now,out PlayerTransformData destination)
        {
            destination=null;
            if(host==null || !Finite(x) || !Finite(y) || !Finite(now) || now<received || now-received>MaximumAge)
            {CancelCountdown();return false;}
            double dx=(double)x-host.X,dy=(double)y-host.Y;
            if(dx*dx+dy*dy<=(double)MaximumDistance*MaximumDistance)
            {CancelCountdown();return false;}
            if(double.IsNaN(outsideSince) || now<outsideSince)outsideSince=now;
            double remaining=CountdownSeconds-(now-outsideSince);
            if(remaining>0){SecondsRemaining=(int)Math.Ceiling(remaining);return false;}
            destination=new PlayerTransformData {X=host.X,Y=host.Y,VX=host.VX,VY=host.VY};
            CancelCountdown();return true;
        }

        public void CancelCountdown(){outsideSince=double.NaN;SecondsRemaining=0;}
        public void Remove(int playerId){if(host!=null && host.PlayerId==playerId)Clear();}
        public void Clear(){host=null;received=0;CancelCountdown();}
        private static bool Finite(double v)=>!double.IsNaN(v) && !double.IsInfinity(v);
    }
}
