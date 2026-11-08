using System;
using System.Collections.Generic;

namespace Orbit_Us
{
    // Follows the living host, or the living pilot selected by the dead host.
    public sealed class HostRecallState
    {
        public const float MaximumDistance=10000f;
        public const double CountdownSeconds=5;
        private const double MaximumAge=1.0;
        private sealed class Sample {public PlayerTransformData Motion;public double Received;}
        private readonly Dictionary<int,Sample> motions=new Dictionary<int,Sample>();
        private Sample host;
        private double outsideSince=double.NaN;
        public int SecondsRemaining {get;private set;}
        public bool FollowingSpectator=>host!=null && host.Motion.HasLifeState && !host.Motion.Alive;
        public int TargetPlayerId=>host==null?0:FollowingSpectator?host.Motion.SpectatingPlayerId:host.Motion.PlayerId;

        public void Observe(PlayerTransformData motion,double now)
        {
            if(motion==null || motion.PlayerId<=0 ||
                !Finite(now) || !Finite(motion.X) || !Finite(motion.Y) || !Finite(motion.VX) || !Finite(motion.VY))return;
            if(motions.TryGetValue(motion.PlayerId,out Sample previous) && unchecked((int)(motion.Sequence-previous.Motion.Sequence))<=0)return;
            int previousTarget=TargetPlayerId,previousHost=host!=null?host.Motion.PlayerId:0;
            var sample=new Sample {Received=now,Motion=new PlayerTransformData {PlayerId=motion.PlayerId,Sequence=motion.Sequence,
                X=motion.X,Y=motion.Y,VX=motion.VX,VY=motion.VY,IsHost=motion.IsHost,HasVelocity=motion.HasVelocity,
                HasLifeState=motion.HasLifeState,Alive=motion.Alive,SpectatingPlayerId=motion.SpectatingPlayerId}};
            motions[motion.PlayerId]=sample;
            if(motion.IsHost)host=sample;
            else if(previousHost==motion.PlayerId)host=null;
            if(previousTarget!=TargetPlayerId || previousHost!=(host!=null?host.Motion.PlayerId:0) ||
                (motion.PlayerId==TargetPlayerId && motion.HasLifeState && !motion.Alive))CancelCountdown();
        }

        public bool TryRecall(float x,float y,double now,out PlayerTransformData destination)
        {
            destination=null;
            if(!Finite(x) || !Finite(y) || !Finite(now) || !Fresh(host,now) || TargetPlayerId<=0 ||
                !motions.TryGetValue(TargetPlayerId,out Sample target) || !Fresh(target,now) || !target.Motion.HasVelocity ||
                (FollowingSpectator && (!target.Motion.HasLifeState || !target.Motion.Alive || TargetPlayerId==host.Motion.PlayerId)))
            {CancelCountdown();return false;}
            var anchor=target.Motion;
            double dx=(double)x-anchor.X,dy=(double)y-anchor.Y;
            if(dx*dx+dy*dy<=(double)MaximumDistance*MaximumDistance)
            {CancelCountdown();return false;}
            if(double.IsNaN(outsideSince) || now<outsideSince)outsideSince=now;
            double remaining=CountdownSeconds-(now-outsideSince);
            if(remaining>0){SecondsRemaining=(int)Math.Ceiling(remaining);return false;}
            destination=new PlayerTransformData {PlayerId=anchor.PlayerId,X=anchor.X,Y=anchor.Y,VX=anchor.VX,VY=anchor.VY};
            CancelCountdown();return true;
        }

        public void CancelCountdown(){outsideSince=double.NaN;SecondsRemaining=0;}
        public void Remove(int playerId)
        {
            if(host!=null && host.Motion.PlayerId==playerId){Clear();return;}
            motions.Remove(playerId);if(TargetPlayerId==playerId)CancelCountdown();
        }
        public void Clear(){host=null;motions.Clear();CancelCountdown();}
        private static bool Fresh(Sample sample,double now)=>sample!=null && now>=sample.Received && now-sample.Received<=MaximumAge;
        private static bool Finite(double v)=>!double.IsNaN(v) && !double.IsInfinity(v);
    }
}
