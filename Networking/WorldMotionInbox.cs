using System;
using System.Collections.Generic;

namespace Orbit_Us
{
    // Coalesce per entity, not per batch: losing one datagram must not suppress
    // the other independently usable batches from the same host update.
    internal sealed class WorldMotionInbox
    {
        private sealed class Entry {public WorldMotion Motion;public WorldMotionBatch Batch;public NetworkPacket Packet;}
        private readonly Dictionary<int,Entry> pending=new Dictionary<int,Entry>();
        public void Add(NetworkPacket packet)
        {
            if(!WorldMotionCodec.TryDecode(packet.Data,out WorldMotionBatch batch) || batch.Planets!=(packet.Type==PacketType.PlanetMotion))return;
            foreach(var motion in batch.Entities)
            {
                if(pending.TryGetValue(motion.Id,out Entry previous))
                {if(unchecked((int)(batch.Sequence-previous.Batch.Sequence))<=0)continue;}
                else if(pending.Count>=20000)continue;
                pending[motion.Id]=new Entry {Motion=motion,Batch=batch,Packet=packet};
            }
        }
        public void Drain(List<NetworkPacket> result)
        {
            var groups=new Dictionary<WorldMotionBatch,WorldMotionBatch>();
            foreach(var entry in pending.Values)
            {
                if(!groups.TryGetValue(entry.Batch,out WorldMotionBatch group))
                {
                    group=new WorldMotionBatch {Planets=entry.Batch.Planets,Sequence=entry.Batch.Sequence,Time=entry.Batch.Time,TimeScale=entry.Batch.TimeScale};
                    groups.Add(entry.Batch,group);
                    result.Add(new NetworkPacket {Type=entry.Packet.Type,Data=entry.Packet.Data,Decoded=group,ReceivedAt=entry.Packet.ReceivedAt});
                }
                group.Entities.Add(entry.Motion);
            }
            pending.Clear();
        }
    }
}
