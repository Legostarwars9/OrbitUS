using System;
using System.Collections.Generic;

namespace Orbit_Us
{
    public sealed class TeamWipeState
    {
        public bool Ended {get;private set;}
        private readonly HashSet<int> pending=new HashSet<int>();
        public List<int> Pending=>new List<int>(pending);
        public bool Begin(bool localDead,IEnumerable<int> peers,Func<int,bool> confirmedDead)
        {
            if(Ended || !localDead)return false;
            var connected=new HashSet<int>(peers);
            foreach(int id in connected)if(!confirmedDead(id))return false;
            Ended=true;pending.UnionWith(connected);return true;
        }
        public void Receive(){Ended=true;}
        public void Add(int id){if(Ended && id>0)pending.Add(id);}
        public void Acknowledge(int id)=>pending.Remove(id);
        public void Clear(){Ended=false;pending.Clear();}
    }
}
