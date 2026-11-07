using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit_Us
{
    public partial class EnemyReplicator
    {
        private sealed class ChildHealth { public int Id;public float HP,MaxHP; }
        private readonly Dictionary<GameObject,int> extraVisualPartIds=new Dictionary<GameObject,int>();
        private readonly Dictionary<health,int> extraHealthIds=new Dictionary<health,int>();
        private readonly Dictionary<int,health> extraHealthById=new Dictionary<int,health>();
        private readonly Dictionary<health,ChildHealth> remoteChildHealth=new Dictionary<health,ChildHealth>();
        private readonly Dictionary<string,Sprite> articulationSprites=new Dictionary<string,Sprite>();
        public static bool IsReplica(Component c)=>c!=null && c.GetComponentInParent<EnemyReplicaMarker>()!=null;
        private static bool HasBossAncestor(Transform t)
        {
            while(t!=null)
            {
                if(t.GetComponent<plantBoss>()!=null || t.GetComponent<planetBoss>()!=null || t.GetComponent<worm>()!=null || t.GetComponent<hunterController>()!=null)return true;
                t=t.parent;
            }
            return false;
        }
        private int ExtraId(health h)
        {
            if(!extraHealthIds.TryGetValue(h,out int id)){id=nextEnemyId++;extraHealthIds.Add(h,id);extraHealthById.Add(id,h);}return id;
        }
        private void RemoveUnauthorizedParts()
        {
            var objects=new HashSet<GameObject>();
            foreach(var foot in UnityEngine.Object.FindObjectsOfType<plantFoot>())objects.Add(foot.gameObject);
            foreach(var part in UnityEngine.Object.FindObjectsOfType<wormPart>())objects.Add(part.gameObject);
            foreach(var obj in objects)if(!IsReplica(obj.transform)){obj.SetActive(false);UnityEngine.Object.Destroy(obj);}
        }
        private void AppendDetachedParts(List<EnemyTransformData> list,HashSet<enemy> roots)
        {
            var extras=new HashSet<GameObject>();
            foreach(var foot in UnityEngine.Object.FindObjectsOfType<plantFoot>())extras.Add(foot.gameObject);
            foreach(var part in UnityEngine.Object.FindObjectsOfType<wormPart>())extras.Add(part.gameObject);
            foreach(var obj in extras)
            {
                var e=obj.GetComponent<enemy>();if(e!=null && roots.Contains(e))continue;
                var h=obj.GetComponent<health>();if(h!=null && h.dead)continue;
                int type=GetEnemyType(obj);if(type<0)continue;
                // The actual plantFoot prefab has no health or enemy component.
                // Its visual existence cannot be conditioned on having hit points.
                int id;
                if(h!=null)id=ExtraId(h);
                else if(!extraVisualPartIds.TryGetValue(obj,out id))
                {id=nextEnemyId++;extraVisualPartIds.Add(obj,id);}
                list.Add(new EnemyTransformData {EnemyId=id,Type=type,X=obj.transform.position.x,Y=obj.transform.position.y,Rotation=obj.transform.eulerAngles.z,HP=h!=null?h.hp:0,Nodes=CaptureArticulation(obj,type)});
            }
            foreach(var obj in new List<GameObject>(extraVisualPartIds.Keys))if(obj==null)extraVisualPartIds.Remove(obj);
            foreach(var h in new List<health>(extraHealthIds.Keys))if(h==null){extraHealthById.Remove(extraHealthIds[h]);extraHealthIds.Remove(h);}
        }
        private List<EnemyNodePose> CaptureArticulation(GameObject obj,int type)
        {
            var list=new List<EnemyNodePose>();
            if(obj.GetComponent<plantFoot>()==null && obj.GetComponent<wormPart>()==null && !HasBossAncestor(obj.transform))return list;
            if(!prefabs.TryGetValue(type,out GameObject prefab) || prefab==null)return list;
            CaptureNodes(obj.transform,prefab.transform,obj.transform,"",list);
            return list;
        }
        private void CaptureNodes(Transform live,Transform template,Transform root,string path,List<EnemyNodePose> list)
        {
            if(list.Count>=512)return;
            var sr=live.GetComponent<SpriteRenderer>();var h=live.GetComponent<health>();
            Vector3 scale=live==root?live.lossyScale:live.localScale;
            var n=new EnemyNodePose {Path=path,X=live.localPosition.x,Y=live.localPosition.y,Z=live.localPosition.z,Rotation=live.localEulerAngles.z,
                ScaleX=scale.x,ScaleY=scale.y,ScaleZ=scale.z,Active=live.gameObject.activeSelf,RendererEnabled=sr==null||sr.enabled,Sprite=sr!=null && sr.sprite!=null?sr.sprite.name:"",
                R=sr!=null?sr.color.r:1,G=sr!=null?sr.color.g:1,B=sr!=null?sr.color.b:1,A=sr!=null?sr.color.a:1,
                HealthId=h!=null && live!=root?ExtraId(h):0,HP=h!=null?h.hp:0,MaxHP=h!=null?h.hpMax:0};
            var line=live.GetComponent<LineRenderer>();
            if(line!=null)for(int i=0;i<Math.Min(line.positionCount,128);i++)
            {
                Vector3 point=line.GetPosition(i);if(!line.useWorldSpace)point=live.TransformPoint(point);
                point=root.InverseTransformPoint(point);n.LinePoints.Add(point.x);n.LinePoints.Add(point.y);n.LinePoints.Add(point.z);
            }
            list.Add(n);
            var used=new HashSet<Transform>();
            for(int i=0;i<template.childCount;i++)
            {
                var expected=template.GetChild(i);Transform child=null;
                for(int j=0;j<live.childCount;j++)if(live.GetChild(j).name==expected.name && !used.Contains(live.GetChild(j))){child=live.GetChild(j);break;}
                if(child==null)continue;used.Add(child);
                CaptureNodes(child,expected,root,path==""?i.ToString():path+"/"+i,list);
            }
        }
        private static Transform NodeAt(Transform t,string path)
        {
            if(path=="")return t;
            foreach(var part in path.Split('/')){if(!int.TryParse(part,out int i)||i<0||i>=t.childCount)return null;t=t.GetChild(i);}return t;
        }
        private void ApplyArticulation(RemoteEnemy remote,bool snap)
        {
            var root=remote.GameObject.transform;
            if(articulationSprites.Count==0)foreach(var sprite in Resources.FindObjectsOfTypeAll<Sprite>())if(sprite!=null)articulationSprites[sprite.name]=sprite;
            float alpha=snap?1:Mathf.Clamp01(20*Time.deltaTime);
            var seen=new HashSet<string>();
            foreach(var n in remote.Nodes)
            {
                seen.Add(n.Path);
                var t=NodeAt(root,n.Path);if(t==null)continue;
                if(t!=root)
                {
                    t.localPosition=Vector3.Lerp(t.localPosition,new Vector3(n.X,n.Y,n.Z),alpha);
                    t.localRotation=Quaternion.Slerp(t.localRotation,Quaternion.Euler(0,0,n.Rotation),alpha);
                }
                t.localScale=new Vector3(n.ScaleX,n.ScaleY,n.ScaleZ);t.gameObject.SetActive(n.Active);
                var sr=t.GetComponent<SpriteRenderer>();
                if(sr!=null)
                {
                    sr.enabled=n.RendererEnabled;sr.color=new Color(n.R,n.G,n.B,n.A);
                    if(n.Sprite=="")sr.sprite=null;
                    else if((sr.sprite==null || sr.sprite.name!=n.Sprite) && articulationSprites.TryGetValue(n.Sprite,out Sprite sprite))sr.sprite=sprite;
                }
                var line=t.GetComponent<LineRenderer>();
                if(line!=null && n.LinePoints.Count>0)
                {
                    line.useWorldSpace=true;line.positionCount=n.LinePoints.Count/3;
                    for(int i=0;i<line.positionCount;i++)line.SetPosition(i,root.TransformPoint(new Vector3(n.LinePoints[i*3],n.LinePoints[i*3+1],n.LinePoints[i*3+2])));
                }
                var h=t.GetComponent<health>();
                if(h!=null)
                {
                    if(t!=root && n.HealthId>0)
                    {
                        CaptureChildDamage(h);remoteChildHealth[h]=new ChildHealth {Id=n.HealthId,HP=n.HP,MaxHP=n.MaxHP};h.hp=n.HP;
                    }
                    h.hpMax=n.MaxHP;h.dead=false;
                }
            }
            if(remote.Nodes.Count>0 && prefabs.TryGetValue(remote.PrefabType,out GameObject template))HideRemovedNodes(root,template.transform,"",seen);
        }
        private static void HideRemovedNodes(Transform root,Transform template,string path,HashSet<string> seen)
        {
            if(path!="" && !seen.Contains(path))
            {var node=NodeAt(root,path);if(node!=null)node.gameObject.SetActive(false);return;}
            for(int i=0;i<template.childCount;i++)HideRemovedNodes(root,template.GetChild(i),path==""?i.ToString():path+"/"+i,seen);
        }
        private bool CaptureChildDamage(health h)
        {
            if(!remoteChildHealth.TryGetValue(h,out ChildHealth state))return false;
            float damage=Mathf.Max(0,state.HP-h.hp);h.hp=state.HP;h.hpMax=state.MaxHP;h.dead=false;
            if(damage>0)QueueDamage(state.Id,damage,0);return true;
        }
    }
    public sealed class EnemyReplicaMarker:MonoBehaviour { }
}
