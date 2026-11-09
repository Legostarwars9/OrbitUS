using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit_Us
{
    public sealed class CombatReplicator
    {
        private sealed class Shot
        {public int Id;public string Prefab;public projectile Source;public CombatEntityState State;public float EndedAt=-1;}
        private sealed class Drop
        {public item Source;public CombatEntityState State;public float Expires;}
        private sealed class Replica
        {public GameObject Object;public CombatEntityState State;public float Received;public bool Ending;public readonly MotionInterpolation Motion=new MotionInterpolation();}
        private readonly NetworkUDPConnection connection;
        private readonly bool host;
        private readonly Dictionary<projectile,Shot> shots=new Dictionary<projectile,Shot>();
        private readonly Dictionary<item,Drop> drops=new Dictionary<item,Drop>();
        private readonly Dictionary<string,GameObject> prefabs=new Dictionary<string,GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<long,Replica> replicas=new Dictionary<long,Replica>();
        private readonly HashSet<long> claimed=new HashSet<long>();
        private readonly Dictionary<int,CombatSnapshotCodec.Assembler> incoming=new Dictionary<int,CombatSnapshotCodec.Assembler>();
        private readonly Dictionary<int,float> ownerSeen=new Dictionary<int,float>();
        private readonly Dictionary<int,CombatHit> pendingHits=new Dictionary<int,CombatHit>();
        private readonly Dictionary<int,CombatHit> hitTotals=new Dictionary<int,CombatHit>();
        private readonly DamageRequestHistory receivedHits=new DamageRequestHistory();
        private readonly HashSet<string> warnings=new HashSet<string>();
        private GameObject staging;
        private int nextId=1,sequence=1,nextHit=1;
        private float timer,scanTimer,retryTimer,hitTimer;
        private bool initialized,sending;
        public NetworkPilotRegistry Pilots {get;private set;}
        public static CombatReplicator Instance {get;private set;}
        public CombatReplicator(NetworkUDPConnection connection,bool host){this.connection=connection;this.host=host;Pilots=new NetworkPilotRegistry(connection,host);}
        private static long Key(int owner,int id)=>((long)owner<<32)|(uint)id;
        public void Initialize()
        {
            Instance=this;initialized=true;connection.OnPacketReceived+=Handle;
            RefreshCatalog();
            foreach(var p in UnityEngine.Object.FindObjectsOfType<projectile>())RegisterProjectile(p);
            foreach(var pickup in UnityEngine.Object.FindObjectsOfType<item>())RegisterPickup(pickup);
        }
        private void RefreshCatalog()
        {
            foreach(var p in Resources.FindObjectsOfTypeAll<projectile>())
                if(p!=null && !p.gameObject.scene.IsValid())prefabs[AssetKey(p)]=p.gameObject;
            foreach(var pickup in Resources.FindObjectsOfTypeAll<item>())
                if(pickup!=null && !pickup.gameObject.scene.IsValid() && (pickup is fuelBlob || pickup is healthBlob || pickup is scrap))prefabs[AssetKey(pickup)]=pickup.gameObject;
        }
        private static string AssetKey(Component c)=>c.GetType().Name+":"+c.gameObject.name.Replace("(Clone)","").Trim();
        private string ResolvePrefab(Component source)
        {
            string direct=AssetKey(source);if(prefabs.ContainsKey(direct))return direct;
            // item.Start can replace the clone name with item.name. Match that
            // serialized identity and sprite, rather than losing renamed drops.
            var itemSource=source as item;
            if(itemSource!=null)
            {
                var sr=source.GetComponent<SpriteRenderer>();
                foreach(var pair in prefabs)
                {
                    var candidate=pair.Value.GetComponent(source.GetType()) as item;
                    if(candidate==null || candidate.name!=itemSource.name)continue;
                    var visual=candidate.GetComponent<SpriteRenderer>();
                    if(sr==null || (visual!=null && sr.sprite==visual.sprite))return pair.Key;
                }
            }
            return null;
        }
        private static void DisableDecay(GameObject obj)
        {
            foreach(var timer in obj.GetComponentsInChildren<decay>(true))
            {timer.StopAllCoroutines();timer.enabled=false;}
        }
        public void RegisterProjectile(projectile p)
        {
            if(!initialized || p==null || p.GetComponent<CombatReplicaMarker>()!=null || shots.ContainsKey(p))return;
            if(!host && !p.playerSafe){UnityEngine.Object.Destroy(p.gameObject);return;}
            string key=ResolvePrefab(p);
            if(key==null){RefreshCatalog();key=ResolvePrefab(p);}
            if(key==null){Warn("Missing projectile prefab: "+AssetKey(p));return;}
            shots.Add(p,new Shot {Id=nextId++,Source=p,Prefab=key});
            CaptureShot(shots[p]);
        }
        public void RegisterPickup(item pickup)
        {
            if(!initialized || pickup==null || !(pickup is fuelBlob || pickup is healthBlob || pickup is scrap) || pickup.GetComponent<CombatPickupMarker>()!=null || drops.ContainsKey(pickup))return;
            if(!host){UnityEngine.Object.Destroy(pickup.gameObject);return;}
            string key=ResolvePrefab(pickup);
            if(key==null){RefreshCatalog();key=ResolvePrefab(pickup);}
            if(key==null){Warn("Missing resource prefab: "+AssetKey(pickup));return;}
            DisableDecay(pickup.gameObject);
            var marker=pickup.gameObject.AddComponent<CombatPickupMarker>();marker.Owner=connection.LocalPlayerId;marker.Id=nextId++;
            var state=CaptureBase(pickup.gameObject,marker.Id,key);
            state.Kind=pickup is fuelBlob?(byte)1:pickup is healthBlob?(byte)2:(byte)3;
            state.Amount=pickup is fuelBlob?((fuelBlob)pickup).size:pickup is healthBlob?((healthBlob)pickup).quantity:((scrap)pickup).scraps;
            drops.Add(pickup,new Drop {Source=pickup,State=state,Expires=Time.unscaledTime+30f});
        }
        public void Claim(CombatPickupMarker marker)
        {
            if(marker!=null && marker.Owner>0 && !(host && marker.Owner==connection.LocalPlayerId))claimed.Add(Key(marker.Owner,marker.Id));
        }
        public bool StepPickup(item pickup)
        {
            if(pickup==null || pickup.GetComponent<CombatPickupMarker>()==null)return false;
            var local=player.Instance;if(local==null || local.dead)return true;
            pickup.playerTrans=local.transform;pickup.playerRb=local.rb;
            if(pickup.chasing)
            {
                Vector2 offset=local.rb.position-pickup.rb.position;
                pickup.rb.AddForce(offset.normalized*(offset.sqrMagnitude<100?100:25)*pickup.rb.mass);
            }
            return true;
        }
        private CombatEntityState CaptureBase(GameObject obj,int id,string prefab)
        {
            var rb=obj.GetComponent<Rigidbody2D>();var sr=obj.GetComponent<SpriteRenderer>();
            Color color=sr!=null?sr.color:Color.white;
            return new CombatEntityState {Id=id,Prefab=prefab,X=obj.transform.position.x,Y=obj.transform.position.y,VX=rb!=null?rb.velocity.x:0,VY=rb!=null?rb.velocity.y:0,
                Rotation=obj.transform.eulerAngles.z,ScaleX=obj.transform.lossyScale.x,ScaleY=obj.transform.lossyScale.y,R=color.r,G=color.g,B=color.b,A=color.a};
        }
        private void CaptureShot(Shot shot)
        {
            var p=shot.Source;if(p==null)return;
            var state=CaptureBase(p.gameObject,shot.Id,shot.Prefab);state.PlayerSafe=p.playerSafe;state.Shield=p.shield;state.Interceptor=p.interceptor;
            var circle=p.GetComponent<CircleCollider2D>();if(circle!=null)state.CollisionRadius=circle.radius;
            var line=p.GetComponentInChildren<LineRenderer>();
            if(line!=null)
            {
                for(int i=0;i<Math.Min(line.positionCount,64);i++)
                {
                    Vector3 point=line.GetPosition(i);if(!line.useWorldSpace)point=line.transform.TransformPoint(point);
                    state.LinePoints.Add(point.x);state.LinePoints.Add(point.y);state.LinePoints.Add(point.z);
                }
            }
            shot.State=state;
        }
        public void Update()
        {
            if(!initialized)return;
            scanTimer+=Time.unscaledDeltaTime;
            if(scanTimer>=5){scanTimer=0;RefreshCatalog();}
            Pilots.Update();
            foreach(var pair in new List<KeyValuePair<long,Replica>>(replicas))
            {
                var r=pair.Value;
                if(r.Object==null)continue;
                if(r.State.Kind==0)
                {
                    if((r.Ending && Time.unscaledTime-r.Received>0.08f) || Time.unscaledTime-r.Received>2f)
                    {if(r.Ending)claimed.Add(pair.Key);RemoveReplica(pair.Key);continue;}
                    if(r.Motion.Evaluate(NetworkTime.Now,out MotionPose pose))
                    {r.Object.transform.position=new Vector3(pose.X,pose.Y,0);r.Object.transform.rotation=Quaternion.Euler(0,0,pose.Rotation);}
                }
            }
            hitTimer+=Time.unscaledDeltaTime;
            if(host && hitTimer>=0.1f)
            {
                hitTimer=0;
                foreach(var total in hitTotals.Values)
                {
                    if(pendingHits.Count>=512)break;
                    total.Id=nextHit++;pendingHits.Add(total.Id,total);_=connection.SendCombatHit(total);
                }
                hitTotals.Clear();
            }
            retryTimer+=Time.unscaledDeltaTime;
            if(host && retryTimer>=0.3f)
            {retryTimer=0;foreach(var hit in pendingHits.Values)_=connection.SendCombatHit(hit);}
        }
        public void LateUpdate()
        {
            if(!initialized)return;
            timer+=Time.unscaledDeltaTime;
            if(timer<.1f || sending || connection.LocalPlayerId<=0)return;
            timer%=.1f;
            foreach(var pair in new List<KeyValuePair<projectile,Shot>>(shots))
            {
                var shot=pair.Value;
                if(shot.Source!=null)CaptureShot(shot);
                else
                {
                    if(shot.EndedAt<0){shot.EndedAt=Time.unscaledTime;shot.State.Ended=true;}
                    if(Time.unscaledTime-shot.EndedAt>0.25f)shots.Remove(pair.Key);
                }
            }
            foreach(var key in new List<item>(drops.Keys))
                if(Time.unscaledTime>drops[key].Expires)
                {
                    if(drops[key].Source!=null)UnityEngine.Object.Destroy(drops[key].Source.gameObject);
                    drops.Remove(key);
                }
            Send();
        }
        public void CaptureProjectileImpact(projectile source)
        {if(source!=null && shots.TryGetValue(source,out Shot shot))CaptureShot(shot);}
        private async void Send()
        {
            sending=true;
            try
            {
                var local=player.Instance;
                var frame=new CombatFrame {Owner=connection.LocalPlayerId,Sequence=sequence++,Authority=host,Alive=local!=null && !local.dead && local.gameObject.activeInHierarchy};
                foreach(var shot in shots.Values)
                {
                    if(frame.Entities.Count>=4096)break;
                    if(shot.State!=null)frame.Entities.Add(shot.State.Copy());
                }
                if(host)foreach(var drop in drops.Values)
                {
                    if(frame.Entities.Count>=4096)break;
                    // Retain the offer for other pilots after the host collects its copy.
                    if(drop.Source!=null)
                    {
                        var pose=CaptureBase(drop.Source.gameObject,drop.State.Id,drop.State.Prefab);
                        pose.Kind=drop.State.Kind;pose.Amount=drop.State.Amount;drop.State=pose;
                    }
                    frame.Entities.Add(drop.State.Copy());
                }
                await connection.SendCombatFrame(frame);
            }
            catch(Exception ex){Debug.LogError("[Orbit-Us] Combat snapshot failed: "+ex);}
            finally{sending=false;}
        }
        public void HitPilot(int id,float damage,float heat)
        {
            if(!host || damage<0 || !CombatRules.Finite(damage) || !CombatRules.Finite(heat))return;
            if(!hitTotals.TryGetValue(id,out CombatHit total)){total=new CombatHit {PlayerId=id};hitTotals.Add(id,total);}
            total.Damage+=damage;total.Heat+=heat;
        }
        private void Handle(NetworkPacket packet)
        {
            if(!initialized)return;
            if(packet.Type==PacketType.PlayerTransform)
            {
                var motion=PlayerTransformData.Deserialize(packet.Data);Pilots.Motion(motion,packet.ReceivedAt>0?packet.ReceivedAt:NetworkTime.Now);
                if(host && motion.HasLifeState && !motion.Alive)
                {
                    hitTotals.Remove(motion.PlayerId);
                    foreach(int id in new List<int>(pendingHits.Keys))if(pendingHits[id].PlayerId==motion.PlayerId)pendingHits.Remove(id);
                }
                return;
            }
            if(packet.Type==PacketType.PlayerDisconnected && packet.Data.Length==4)
            {
                int id=BitConverter.ToInt32(packet.Data,0);Pilots.Remove(id);incoming.Remove(id);ownerSeen.Remove(id);
                foreach(long key in new List<long>(claimed))if((int)(key>>32)==id)claimed.Remove(key);
                foreach(long key in new List<long>(replicas.Keys))if((int)(key>>32)==id)RemoveReplica(key);
                foreach(int hit in new List<int>(pendingHits.Keys))if(pendingHits[hit].PlayerId==id)pendingHits.Remove(hit);
                hitTotals.Remove(id);return;
            }
            if(host && packet.Type==PacketType.CombatHitAck && packet.Data.Length==8)
            {
                int id=BitConverter.ToInt32(packet.Data,4);
                if(pendingHits.TryGetValue(id,out CombatHit hit) && hit.PlayerId==BitConverter.ToInt32(packet.Data,0))pendingHits.Remove(id);
                return;
            }
            if(!host && packet.Type==PacketType.CombatHit)
            {
                var hit=CombatHit.Deserialize(packet.Data);
                if(hit.PlayerId!=connection.LocalPlayerId)return;
                if(receivedHits.Accept(hit.Id))
                {
                    var local=player.Instance;
                    if(local!=null && !local.dead && PlayerReplicator.Instance?.Life.IgnoreDamage!=true)
                    {local.HandleDamageImpulse(hit.Damage);if(local.temperature!=null)local.temperature.heat+=hit.Heat;}
                }
                _=connection.SendCombatHitAck(hit.Id);return;
            }
            if(packet.Type!=PacketType.CombatSnapshot)return;
            if(packet.SnapshotValidated && packet.Decoded==null)return;
            int owner=CombatSnapshotCodec.ReadOwner(packet.Data);
            if(owner==connection.LocalPlayerId || owner<=0)return;
            if(!incoming.TryGetValue(owner,out CombatSnapshotCodec.Assembler assembler))
            {assembler=new CombatSnapshotCodec.Assembler();incoming.Add(owner,assembler);}
            var frame=packet.Decoded as CombatFrame;
            if(frame==null && (packet.SnapshotValidated || !assembler.TryAdd(packet.Data,out frame)))return;
            Pilots.Presence(owner,frame.Alive);PlayerReplicator.Instance?.ObserveCombatLife(owner,frame.Alive);ownerSeen[owner]=Time.unscaledTime;Apply(frame);
        }
        private void Apply(CombatFrame frame)
        {
            var keep=new HashSet<long>();
            foreach(var state in frame.Entities)
            {
                long key=Key(frame.Owner,state.Id);keep.Add(key);
                if(claimed.Contains(key))continue;
                if(!prefabs.TryGetValue(state.Prefab,out GameObject prefab)) {Warn("Missing combat prefab: "+state.Prefab);continue;}
                if(!replicas.TryGetValue(key,out Replica replica) || replica.Object==null)
                {
                    if(state.Kind>0 && replica!=null)continue; // a collected/expired personal copy never respawns
                    if(staging==null){staging=new GameObject("OrbitUs_CombatStaging");staging.SetActive(false);}
                    var obj=UnityEngine.Object.Instantiate(prefab,staging.transform);obj.SetActive(false);
                    if(state.Kind==0)
                    {
                        obj.AddComponent<CombatReplicaMarker>();
                        foreach(var behaviour in obj.GetComponentsInChildren<MonoBehaviour>(true))
                            if(!(behaviour is CombatReplicaMarker)){behaviour.StopAllCoroutines();behaviour.enabled=false;}
                        foreach(var collider in obj.GetComponentsInChildren<Collider2D>(true))collider.enabled=false;
                        foreach(var body in obj.GetComponentsInChildren<Rigidbody2D>(true)){body.bodyType=RigidbodyType2D.Kinematic;body.simulated=false;}
                        var visualProjectile=obj.GetComponent<projectile>();visualProjectile.playerSafe=state.PlayerSafe;
                        if(projectileManager.instance!=null)projectileManager.instance.RegisterProjectile(visualProjectile);
                        if(host && state.PlayerSafe && (state.Shield || state.Interceptor))obj.AddComponent<RemoteDefenseProxy>();
                    }
                    else
                    {
                        var marker=obj.AddComponent<CombatPickupMarker>();marker.Owner=frame.Owner;marker.Id=state.Id;
                        DisableDecay(obj);
                        var pickup=obj.GetComponent<item>();pickup.rb=obj.GetComponent<Rigidbody2D>();
                        if(state.Kind==1 && pickup is fuelBlob)((fuelBlob)pickup).size=state.Amount;
                        if(state.Kind==2 && pickup is healthBlob)((healthBlob)pickup).quantity=state.Amount;
                        if(state.Kind==3 && pickup is scrap)((scrap)pickup).scraps=(int)state.Amount;
                    }
                    replica=new Replica {Object=obj,State=state};replicas[key]=replica;
                    obj.transform.position=new Vector3(state.X,state.Y,0);obj.transform.rotation=Quaternion.Euler(0,0,state.Rotation);
                    obj.transform.localScale=new Vector3(state.ScaleX,state.ScaleY,1);
                    var rb=obj.GetComponent<Rigidbody2D>();if(rb!=null)rb.velocity=new Vector2(state.VX,state.VY);
                    obj.transform.SetParent(null,true);obj.SetActive(true);
                }
                replica.State=state;
                if(state.Kind>0)continue; // resource copies chase and collect independently on each peer
                replica.Received=Time.unscaledTime;replica.Ending=state.Ended;
                var go=replica.Object;
                if(!state.Ended)
                {
                    double now=NetworkTime.Now;
                    replica.Motion.Add((uint)frame.Sequence,now,now,new MotionPose {X=state.X,Y=state.Y,Rotation=state.Rotation,VX=state.VX*Time.timeScale,VY=state.VY*Time.timeScale});
                }
                go.transform.localScale=new Vector3(state.ScaleX,state.ScaleY,1);
                var visualBody=go.GetComponent<Rigidbody2D>();if(visualBody!=null)visualBody.velocity=new Vector2(state.VX,state.VY);
                var defense=go.GetComponent<RemoteDefenseProxy>();
                if(defense!=null)
                {
                    defense.Shield=state.Shield;defense.Interceptor=state.Interceptor;
                    var circle=go.GetComponent<CircleCollider2D>();if(circle==null)circle=go.AddComponent<CircleCollider2D>();
                    circle.radius=Mathf.Max(0.05f,state.CollisionRadius);circle.isTrigger=true;circle.enabled=true;
                    if(visualBody!=null)visualBody.simulated=true;
                }
                var sr=go.GetComponent<SpriteRenderer>();if(sr!=null)sr.color=new Color(state.R,state.G,state.B,state.A);
                var line=go.GetComponentInChildren<LineRenderer>();
                if(line!=null && state.LinePoints.Count>0)
                {
                    line.useWorldSpace=true;line.positionCount=state.LinePoints.Count/3;
                    for(int i=0;i<line.positionCount;i++)line.SetPosition(i,new Vector3(state.LinePoints[i*3],state.LinePoints[i*3+1],state.LinePoints[i*3+2]));
                }
            }
            foreach(long key in new List<long>(replicas.Keys))if((int)(key>>32)==frame.Owner && !keep.Contains(key))RemoveReplica(key);
            // A newer complete frame proves these IDs are gone. Its sequence
            // prevents delayed frames reviving them, so their tombstones can go.
            foreach(long key in new List<long>(claimed))if((int)(key>>32)==frame.Owner && !keep.Contains(key))claimed.Remove(key);
        }
        private void RemoveReplica(long key)
        {
            var obj=replicas[key].Object;
            if(obj!=null){obj.SetActive(false);UnityEngine.Object.Destroy(obj);}replicas.Remove(key);
        }
        private void Warn(string message){if(warnings.Add(message))Debug.LogWarning("[Orbit-Us] "+message);}
        public void Destroy()
        {
            connection.OnPacketReceived-=Handle;initialized=false;
            foreach(long key in new List<long>(replicas.Keys))RemoveReplica(key);
            foreach(var drop in drops.Values)if(drop.Source!=null)UnityEngine.Object.Destroy(drop.Source.GetComponent<CombatPickupMarker>());
            Pilots.Destroy();pendingHits.Clear();hitTotals.Clear();
            if(staging!=null)UnityEngine.Object.Destroy(staging);
            if(Instance==this)Instance=null;
        }
    }
    public sealed class CombatReplicaMarker:MonoBehaviour { }
    public sealed class CombatPickupMarker:MonoBehaviour {public int Owner,Id;}
}
