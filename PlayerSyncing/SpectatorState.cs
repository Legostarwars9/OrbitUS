using System;
using System.Collections.Generic;
using System.Reflection;

namespace Orbit_Us
{
    public sealed class SpectatorRoster
    {
        private sealed class Peer {public bool Alive;public double Received;}
        private readonly Dictionary<int,Peer> peers=new Dictionary<int,Peer>();
        public int TargetId {get;private set;}
        public void Observe(int id,bool alive,double now)
        {if(id>0)peers[id]=new Peer {Alive=alive,Received=now};}
        public bool IsLiving(int id,double now)
            =>peers.TryGetValue(id,out Peer peer) && peer.Alive && now>=peer.Received && now-peer.Received<=3;
        public bool ConfirmedDead(int id,double now)
            =>peers.TryGetValue(id,out Peer peer) && !peer.Alive && now>=peer.Received && now-peer.Received<=3;
        public List<int> Living(double now)
        {
            var result=new List<int>();foreach(int id in peers.Keys)if(IsLiving(id,now))result.Add(id);
            result.Sort();return result;
        }
        public int EnsureTarget(double now)
        {
            if(!IsLiving(TargetId,now)){var living=Living(now);TargetId=living.Count>0?living[0]:0;}
            return TargetId;
        }
        public int Cycle(int direction,double now)
        {
            var living=Living(now);if(living.Count==0)return TargetId=0;
            int index=living.IndexOf(TargetId);
            if(index<0)return TargetId=direction<0?living[living.Count-1]:living[0];
            return TargetId=living[(index+(direction<0?-1:1)+living.Count)%living.Count];
        }
        public void Remove(int id){peers.Remove(id);if(TargetId==id)TargetId=0;}
        public void Clear(){peers.Clear();TargetId=0;}
    }

    // Copy only build data, never scene references, health or shared mission state.
    public sealed class BuildFieldSnapshot
    {
        private readonly Dictionary<FieldInfo,object> values=new Dictionary<FieldInfo,object>();
        public static FieldInfo Field(Type type,string name)
        {
            for(var current=type;current!=null;current=current.BaseType)
            {
                var field=current.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
                if(field!=null)return field;
            }
            throw new MissingFieldException(type.FullName,name);
        }
        public static BuildFieldSnapshot Capture(object target,IEnumerable<string> names)
        {
            var saved=new BuildFieldSnapshot();
            foreach(string name in names){var field=Field(target.GetType(),name);saved.values[field]=Copy(field.GetValue(target));}
            return saved;
        }
        private static object Copy(object value)
        {
            if(value is List<int>)return new List<int>((List<int>)value);
            if(value is Array)return ((Array)value).Clone();
            return value;
        }
        public void Restore(object target)
        {
            foreach(var entry in values)
            {
                var list=entry.Key.GetValue(target) as List<int>;
                if(list!=null && entry.Value is List<int>){list.Clear();list.AddRange((List<int>)entry.Value);}
                else if(entry.Key.GetValue(target) is Array current && entry.Value is Array saved && current.Length==saved.Length)
                    Array.Copy(saved,current,saved.Length);
                else entry.Key.SetValue(target,Copy(entry.Value));
            }
        }
        public static int RetainedMoney(int saved,int current)=>Math.Max(saved,current);
    }
}
