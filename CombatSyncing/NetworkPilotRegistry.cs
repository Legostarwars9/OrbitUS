using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    public sealed class NetworkPilotRegistry
    {
        private sealed class Pilot
        {
            public GameObject Object;
            public Rigidbody2D Body;
            public CircleCollider2D Collider;
            public readonly PilotMotionState Motion=new PilotMotionState();
        }
        private readonly Dictionary<int,Pilot> pilots=new Dictionary<int,Pilot>();
        private readonly Dictionary<Type,FieldInfo[]> fields=new Dictionary<Type,FieldInfo[]>();
        private readonly bool host;
        private readonly NetworkUDPConnection connection;
        public NetworkPilotRegistry(NetworkUDPConnection connection,bool host){this.connection=connection;this.host=host;}
        public void Motion(PlayerTransformData t)
            =>Motion(t,NetworkTime.Now);
        public void Motion(PlayerTransformData t,double arrival)
        {
            if(!host || t.PlayerId==connection.LocalPlayerId)return;
            Get(t.PlayerId).Motion.Observe(t,arrival);
        }
        public void Presence(int id,bool alive)
        {if(host && id!=connection.LocalPlayerId)Get(id).Motion.Presence(alive);}
        public bool IsLiving(int id)
            =>pilots.TryGetValue(id,out Pilot p) && p.Motion.IsLiving(NetworkTime.Now) && p.Object!=null;
        private Pilot Get(int id)
        {
            if(pilots.TryGetValue(id,out Pilot existing))
            {if(existing.Object!=null)return existing;pilots.Remove(id);}
            var obj=new GameObject("OrbitUs_PilotTarget_"+id);
            obj.tag="NoDelete"; // native black-hole entry deletes ordinary rigidbodies
            var body=obj.AddComponent<Rigidbody2D>();body.bodyType=RigidbodyType2D.Kinematic;body.gravityScale=0;
            var collider=obj.AddComponent<CircleCollider2D>();collider.isTrigger=true;
            collider.radius=player.Instance!=null?Mathf.Max(0.5f,player.Instance.transform.lossyScale.x/2):0.5f;
            collider.enabled=false;
            var proxy=obj.AddComponent<NetworkPilotProxy>();proxy.PlayerId=id;
            var p=new Pilot {Object=obj,Body=body,Collider=collider};pilots.Add(id,p);return p;
        }
        public void Update()
        {
            double now=NetworkTime.Now;
            foreach(var pair in pilots)
            {
                var p=pair.Value;
                if(p.Object==null || p.Body==null)continue;
                bool active=p.Motion.IsLiving(now);
                p.Object.SetActive(active);
                p.Collider.enabled=active;
                MotionPose pose=p.Motion.Predict(now,Time.timeScale);
                // Aim and damage share the same position the host actually sees.
                // Presentation smoothing must never leave a separate hitbox ahead.
                p.Body.position=PlayerReplicator.Instance!=null && PlayerReplicator.Instance.TryGetRemotePosition(pair.Key,out Vector3 visible)
                    ?(Vector2)visible:new Vector2(pose.X,pose.Y);
                p.Body.velocity=active?new Vector2(pose.VX,pose.VY):Vector2.zero;
            }
        }
        public bool IsLivingTarget(Rigidbody2D body)
        {
            if(body==null)return false;
            var local=player.Instance;
            if(local!=null && body==local.rb)return !local.dead && local.gameObject.activeInHierarchy;
            foreach(var pair in pilots)if(pair.Value.Body==body)return IsLiving(pair.Key);
            return false;
        }
        public Rigidbody2D Nearest(Vector2 position)
        {
            Rigidbody2D best=null;float distance=float.PositiveInfinity;int bestId=int.MaxValue;
            var local=player.Instance;
            if(local!=null && !local.dead && local.gameObject.activeInHierarchy)
            {best=local.rb;distance=((Vector2)local.transform.position-position).sqrMagnitude;bestId=connection.LocalPlayerId;}
            foreach(var pair in pilots)
            {
                var p=pair.Value;if(p.Body==null || !p.Motion.IsLiving(NetworkTime.Now))continue;
                float candidate=(p.Body.position-position).sqrMagnitude;
                if(CombatRules.Prefer(candidate,pair.Key,distance,bestId))
                {best=p.Body;distance=candidate;bestId=pair.Key;}
            }
            return best;
        }
        public bool Bind(MonoBehaviour actor)
        {
            if(!host || actor==null)return true;
            if(actor is orbiter && ((orbiter)actor).hostilityLevel<=0)return true;
            if(actor is boxController && ((boxController)actor).hostilityLevel<=0)return true;
            Rigidbody2D nearest=Nearest(actor.transform.position);
            if(nearest==null)return false;
            if(!fields.TryGetValue(actor.GetType(),out FieldInfo[] cached))
            {
                var found=new List<FieldInfo>();
                foreach(string name in new[]{"target","targetRb","player","playerTrans","playerRb"})
                {
                    var f=AccessTools.Field(actor.GetType(),name);
                    if(f!=null && (f.FieldType==typeof(Transform)||f.FieldType==typeof(Rigidbody2D)))found.Add(f);
                }
                cached=found.ToArray();fields.Add(actor.GetType(),cached);
            }
            foreach(var f in cached)f.SetValue(actor,f.FieldType==typeof(Transform)?(object)nearest.transform:nearest);
            return true;
        }
        public void Remove(int id)
        {
            if(!pilots.TryGetValue(id,out Pilot p))return;
            if(p.Object!=null){p.Object.SetActive(false);UnityEngine.Object.Destroy(p.Object);}pilots.Remove(id);
        }
        public void Destroy(){foreach(int id in new List<int>(pilots.Keys))Remove(id);}
    }
    public sealed class NetworkPilotProxy:MonoBehaviour
    {
        public int PlayerId;
        private readonly HashSet<projectile> hits=new HashSet<projectile>();
        private readonly HashSet<projectile> overlapping=new HashSet<projectile>();
        private readonly List<Collider2D> contacts=new List<Collider2D>();
        private Collider2D hitbox;
        private static bool Hostile(projectile p)
            =>p!=null && !p.playerSafe && p.GetComponent<CombatReplicaMarker>()==null;
        private void Enter(projectile p)
        {
            if(p.dead)return;
            CombatReplicator.Instance?.HitPilot(PlayerId,p.damage,p.projHeat*0.001f);
            bool field=p is railBeam || p.fieldMultiplier!=1f || p.explosionRadius>0;
            if(!field)p.ProjectileExplodeLight();
        }
        private void OnTriggerEnter2D(Collider2D other)
        {
            // Keep immediate contacts too, including shots that move through the
            // entire hitbox within one physics step. The query shares this ledger.
            var p=other.GetComponent<projectile>();
            if(Hostile(p) && hits.Add(p))Enter(p);
        }
        private void FixedUpdate()
        {
            if(hitbox==null)hitbox=GetComponent<Collider2D>();
            if(hitbox==null || !hitbox.enabled)return;
            // Query geometry directly: kinematic targets and teleported proxies
            // must not depend on Unity delivering trigger enter/stay messages.
            // Ignore the layer matrix here, but reject all visual/player shots.
            hitbox.OverlapCollider(new ContactFilter2D {useTriggers=true},contacts);
            overlapping.Clear();
            foreach(var other in contacts)
            {
                var p=other.GetComponent<projectile>();
                if(!Hostile(p) || !overlapping.Add(p))continue;
                if(!hits.Contains(p))Enter(p);
                else if(p is railBeam || p.fieldMultiplier>0)
                    CombatReplicator.Instance?.HitPilot(PlayerId,p.damage*0.1f,p.projHeat*0.00001f);
            }
            hits.Clear();hits.UnionWith(overlapping);
        }
        private void OnDisable(){hits.Clear();overlapping.Clear();contacts.Clear();}
    }
    public sealed class RemoteDefenseProxy:MonoBehaviour
    {
        public bool Shield,Interceptor;
        private readonly HashSet<projectile> deflected=new HashSet<projectile>();
        private void OnTriggerEnter2D(Collider2D collider)
        {
            var incoming=collider.GetComponent<projectile>();
            if(incoming==null || incoming.playerSafe || incoming.GetComponent<CombatReplicaMarker>()!=null)return;
            if(Shield){UnityEngine.Object.Destroy(incoming.gameObject);return;}
            foreach(var old in new List<projectile>(deflected))if(old==null)deflected.Remove(old);
            if(Interceptor && deflected.Add(incoming))
                incoming.rb.AddForce(((Vector2)incoming.transform.position-(Vector2)transform.position).normalized*5f*incoming.rb.mass,ForceMode2D.Impulse);
        }
    }

}
