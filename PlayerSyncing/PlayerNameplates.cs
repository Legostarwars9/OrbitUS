using System.Collections.Generic;
using UnityEngine;

namespace Orbit_Us
{
    // Project the rendered ships into screen space so camera zoom cannot shrink labels.
    public sealed class PlayerNameplates
    {
        private sealed class Marker
        {
            public SpriteRenderer Ship;
            public GUIContent Text;
            public Color Color;
            public bool Positioned,Host;
        }
        private readonly Dictionary<int,Marker> markers=new Dictionary<int,Marker>();
        private readonly List<Rect> occupied=new List<Rect>();
        private GUIStyle textStyle;

        public static Color ColorFor(int playerId)
        {
            var random=new System.Random(unchecked(playerId*5023));
            return Color.HSVToRGB((float)random.NextDouble(),0.75f,1f);
        }
        public void Register(int id,SpriteRenderer ship)
        {
            markers[id]=new Marker {Ship=ship,Text=new GUIContent("Player "+id),Color=ColorFor(id)};
        }
        public void Observe(int id,bool host)
        {
            if(!markers.TryGetValue(id,out Marker marker))return;
            marker.Positioned=true;
            string name=(OrbitUs.Instance?.Connection?.GetPlayerName(id)??"Player "+id)+(host?" (Host)":"");
            if(marker.Host!=host || marker.Text.text!=name)
            {marker.Host=host;marker.Text.text=name;}
        }
        public void Remove(int id)=>markers.Remove(id);
        public void Clear(){markers.Clear();occupied.Clear();textStyle=null;}

        public void Draw()
        {
            if(Event.current==null || Event.current.type!=EventType.Repaint || markers.Count==0)return;
            var camera=Camera.main;if(camera==null)return;
            if(textStyle==null)
            {
                textStyle=new GUIStyle(GUI.skin.label) {alignment=TextAnchor.MiddleCenter,fontStyle=FontStyle.Normal,wordWrap=false,richText=false};
                textStyle.normal.textColor=Color.white;
            }
            float scale=Mathf.Clamp(Screen.height/1080f,1f,2f);
            textStyle.fontSize=Mathf.RoundToInt(14*scale);
            Rect viewport=camera.pixelRect;
            Rect area=new Rect(viewport.x+4,Screen.height-viewport.yMax+4,viewport.width-8,viewport.height-8);
            if(area.width<100 || area.height<40)return;
            occupied.Clear();
            Color previousColor=GUI.color;Matrix4x4 previousMatrix=GUI.matrix;
            try
            {
                GUI.matrix=Matrix4x4.identity;
                foreach(var marker in markers.Values)
                {
                    var ship=marker.Ship;
                    if(!marker.Positioned || ship==null || !ship.enabled || !ship.gameObject.activeInHierarchy)continue;
                    if((camera.cullingMask&(1<<ship.gameObject.layer))==0)continue;
                    Vector3 center=camera.WorldToScreenPoint(ship.transform.position);
                    if(center.z<=0 || !viewport.Contains(new Vector2(center.x,center.y)))continue;

                    // Use all four bounds corners so labels stay below rotated ships/cameras.
                    Bounds bounds=ship.bounds;float bottom=center.y;
                    for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)
                    {
                        Vector3 corner=bounds.center+new Vector3(x*bounds.extents.x,y*bounds.extents.y,0);
                        bottom=Mathf.Min(bottom,camera.WorldToScreenPoint(corner).y);
                    }
                    float labelY=Mathf.Max(Screen.height-bottom+4*scale,Screen.height-center.y+10*scale);
                    float width=Mathf.Min(area.width,Mathf.Max(80*scale,textStyle.CalcSize(marker.Text).x+16*scale));
                    Rect label=new Rect(Mathf.Clamp(center.x-width/2,area.xMin,area.xMax-width),labelY,width,20*scale);
                    label=Place(label,area,3*scale);
                    occupied.Add(label);

                    // Plain colored text with a small shadow stays readable on planets.
                    GUI.color=new Color(0,0,0,0.9f);
                    GUI.Label(new Rect(label.x+scale,label.y+scale,label.width,label.height),marker.Text,textStyle);
                    GUI.color=marker.Color;
                    GUI.Label(label,marker.Text,textStyle);
                }
            }
            finally{GUI.color=previousColor;GUI.matrix=previousMatrix;}
        }
        private Rect Place(Rect desired,Rect area,float gap)
        {
            Rect candidate=desired;
            // Prefer below the ship, then use available rows above near the bottom edge.
            for(int direction=1;direction>=-1;direction-=2)
            {
                candidate.y=Mathf.Clamp(desired.y,area.yMin,area.yMax-desired.height);
                for(int attempt=0;attempt<=occupied.Count;attempt++)
                {
                    bool collision=false;
                    foreach(var other in occupied)
                        if(candidate.Overlaps(other))
                        {candidate.y=direction>0?other.yMax+gap:other.yMin-gap-candidate.height;collision=true;break;}
                    if(candidate.y<area.yMin || candidate.yMax>area.yMax)break;
                    if(!collision)return candidate;
                }
            }
            desired.y=Mathf.Clamp(desired.y,area.yMin,area.yMax-desired.height);return desired;
        }
    }
}
