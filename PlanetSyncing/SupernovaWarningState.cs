using System;
using System.Collections.Generic;

namespace Orbit_Us
{
    // Mirrors the native star's nine-second warning window without simulating a star.
    public sealed class SupernovaWarningState
    {
        private readonly HashSet<int> warned=new HashSet<int>();
        public static int Remaining(bool mainSequence,bool blackHole,float mass,int lifetime)
            =>!mainSequence && !blackHole && mass>=500 && mass<2000 && lifetime>0 && lifetime<10?lifetime:0;
        public bool TryWarn(int id,int seconds,double age,double distanceSquared,bool alive)
        {
            if(!alive || id<=0 || seconds<=0 || seconds>=10 || double.IsNaN(age) || age<0 || age>=seconds ||
                double.IsNaN(distanceSquared) || distanceSquared<0 || distanceSquared>=2000d*2000d)return false;
            return warned.Add(id);
        }
        public void Remove(int id)=>warned.Remove(id);
        public void Clear()=>warned.Clear();
    }
}
