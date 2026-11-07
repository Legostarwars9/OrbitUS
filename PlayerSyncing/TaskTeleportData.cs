using System;
using System.Collections.Generic;
using System.IO;

namespace Orbit_Us
{
    public enum TaskTeleportReason { Upload, Boss }

    public sealed class TaskTeleportData
    {
        public int Sequence, RecipientId, AnchorId;
        public TaskTeleportReason Reason;
        public float X,Y,VX,VY;
        public TaskTeleportData ForPlayer(int id){var copy=(TaskTeleportData)MemberwiseClone();copy.RecipientId=id;return copy;}
        public byte[] Serialize()
        {
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream))
            {
                writer.Write(Sequence);writer.Write(RecipientId);writer.Write(AnchorId);writer.Write((int)Reason);
                writer.Write(X);writer.Write(Y);writer.Write(VX);writer.Write(VY);return stream.ToArray();
            }
        }
        public static TaskTeleportData Deserialize(byte[] bytes)
        {
            if(bytes==null || bytes.Length!=32)throw new InvalidDataException("Invalid task teleport.");
            using(var reader=new BinaryReader(new MemoryStream(bytes)))
            {
                var value=new TaskTeleportData {Sequence=reader.ReadInt32(),RecipientId=reader.ReadInt32(),AnchorId=reader.ReadInt32(),Reason=(TaskTeleportReason)reader.ReadInt32(),
                    X=reader.ReadSingle(),Y=reader.ReadSingle(),VX=reader.ReadSingle(),VY=reader.ReadSingle()};
                if(value.Sequence<=0 || value.RecipientId<=0 || value.AnchorId<=0 || !Enum.IsDefined(typeof(TaskTeleportReason),value.Reason))throw new InvalidDataException("Invalid task teleport identity.");
                foreach(float v in new[]{value.X,value.Y,value.VX,value.VY})if(float.IsNaN(v)||float.IsInfinity(v))throw new InvalidDataException("Invalid task teleport motion.");
                return value;
            }
        }
    }

    // A newer task supersedes an unacknowledged older destination for each peer.
    public sealed class TaskTeleportQueue
    {
        private int sequence;
        private readonly Dictionary<int,TaskTeleportData> pending=new Dictionary<int,TaskTeleportData>();
        public List<TaskTeleportData> Pending=>new List<TaskTeleportData>(pending.Values);
        public List<TaskTeleportData> Create(int localId,int anchorId,TaskTeleportReason reason,float x,float y,float vx,float vy,IEnumerable<int> peers)
        {
            var local=new TaskTeleportData {Sequence=++sequence,RecipientId=localId,AnchorId=anchorId,Reason=reason,X=x,Y=y,VX=vx,VY=vy};
            var result=new List<TaskTeleportData>{local};
            foreach(int peer in new HashSet<int>(peers))
                if(peer>0 && peer!=localId){var command=local.ForPlayer(peer);pending[peer]=command;result.Add(command);}
            return result;
        }
        public void Acknowledge(int playerId,int id)
        {if(pending.TryGetValue(playerId,out TaskTeleportData command) && command.Sequence==id)pending.Remove(playerId);}
        public void Remove(int playerId)=>pending.Remove(playerId);
        public void Clear(){pending.Clear();sequence=0;}
    }

    public sealed class TaskTeleportReceiver
    {
        private int applied;
        // Return true only when the sender may stop retrying this command.
        public bool TryApply(TaskTeleportData command,int localId,Func<TaskTeleportData,bool> teleport)
        {
            if(command.RecipientId!=localId || command.Sequence<=0)return false;
            if(command.Sequence<=applied)return true;
            if(!teleport(command))return false;
            applied=command.Sequence;return true;
        }
        public void Clear()=>applied=0;
    }

    public sealed class UploadCompletionTracker
    {
        private int uploader,mission,reward;
        public bool Active=>uploader>0;
        public bool Begin(int playerId,int transmission,int researchReward)
        {
            if(Active || playerId<=0)return false;
            uploader=playerId;mission=transmission;reward=researchReward;return true;
        }
        public int Complete(int transmission,int researchReward)
        {
            if(!Active)return 0;
            if(transmission<mission || researchReward<reward){Clear();return 0;}
            if(transmission!=mission+1 || researchReward<=reward)return 0;
            int result=uploader;Clear();return result;
        }
        public void Clear(){uploader=0;mission=reward=0;}
    }
}
