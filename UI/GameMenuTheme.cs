using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace Orbit_Us
{
    // Borrow scene UI styling without changing the game's menus or input handlers.
    internal static class GameMenuTheme
    {
        private static GUISkin skin;
        private static int scene=-1;
        private static float nextScan;
        private static bool foundNativeStyle;
        private static readonly List<Texture2D> textures=new List<Texture2D>();
        private static readonly FieldInfo MainMenu=AccessTools.Field(typeof(startManager),"mainMenuScreen");
        private static readonly FieldInfo PauseMenu=AccessTools.Field(typeof(menuManager),"pauseMenu");
        public static Color TextColor {get;private set;}=new Color(.8396226f,.8396226f,.8396226f);
        public static Color MutedColor=>Color.Lerp(TextColor,new Color(.6f,.6f,.6f),.3f);
        public static Color BorderColor {get;private set;}=new Color(.48f,.53f,.58f);
        public static GUISkin Skin
        {
            get
            {
                int current=SceneManager.GetActiveScene().handle;
                if(current!=scene){Dispose();scene=current;nextScan=0;}
                if(skin==null || (!foundNativeStyle && Time.unscaledTime>=nextScan))
                {nextScan=Time.unscaledTime+1;Create();}
                return skin;
            }
        }
        private static Texture2D Fill(Color fill,Color edge,int size=8)
        {
            var texture=new Texture2D(size,size,TextureFormat.RGBA32,false) {hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point};
            var pixels=new Color[size*size];
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)pixels[y*size+x]=(x==0 || y==0 || x==size-1 || y==size-1)?edge:fill;
            texture.SetPixels(pixels);texture.Apply();textures.Add(texture);return texture;
        }
        private static Color Readable(Color color,Color fallback)=>color.a>.05f?new Color(color.r,color.g,color.b,1):fallback;
        private static void Create()
        {
            // The death screen also contains a QuickPlayButton (wired to Replay).
            // Select by its action inside MainMenu, not the GameObject's name.
            var start=UnityEngine.Object.FindObjectOfType<startManager>(true);
            var menu=UnityEngine.Object.FindObjectOfType<menuManager>(true);
            GameObject main=start!=null?MainMenu.GetValue(start) as GameObject:null;
            GameObject pause=menu!=null?PauseMenu.GetValue(menu) as GameObject:null;
            Button nativeButton=null;
            var candidates=main!=null?main.GetComponentsInChildren<Button>(true):UnityEngine.Object.FindObjectsOfType<Button>(true);
            foreach(var candidate in candidates)
            {
                for(int i=0;i<candidate.onClick.GetPersistentEventCount();i++)
                    if(candidate.onClick.GetPersistentMethodName(i)=="PressQuickPlay"){nativeButton=candidate;break;}
                if(nativeButton!=null)break;
            }
            Text nativeText=nativeButton!=null?nativeButton.GetComponentInChildren<Text>(true):null;
            Image nativeImage=nativeButton!=null?nativeButton.targetGraphic as Image:null;
            Font font=nativeText!=null?nativeText.font:GUI.skin.font;
            if(nativeText==null)
                foreach(var loaded in Resources.FindObjectsOfTypeAll<Font>())
                    if(loaded.name=="Abel-Regular"){font=loaded;break;}
            Color text=Readable(nativeText!=null?nativeText.color:TextColor,TextColor);
            // Exact fallback values read from level0's PauseMenu and Play button.
            Image background=pause!=null?pause.GetComponent<Image>():null;
            Color panel=background!=null?background.color:new Color(.0660377f,.0660377f,.0660377f,.8784314f);
            Color button=nativeImage!=null?nativeImage.color:new Color(.1320755f,.1320755f,.1320755f,.7647059f);
            Color hover=button*.9607843f,pressed=button*.7843137f;hover.a=pressed.a=button.a;
            Color border=new Color(.4f,.4f,.4f,.65f);
            if(nativeButton!=null)
            {
                Color baseColor=button;
                button=baseColor*nativeButton.colors.normalColor;
                hover=baseColor*nativeButton.colors.highlightedColor;
                pressed=baseColor*nativeButton.colors.pressedColor;
            }
            ClearOwned();TextColor=text;BorderColor=border;
            skin=UnityEngine.Object.Instantiate(GUI.skin);skin.hideFlags=HideFlags.HideAndDontSave;skin.font=font;
            skin.label.font=font;skin.label.fontSize=14;skin.label.richText=false;skin.label.wordWrap=false;
            skin.label.normal.textColor=text;
            skin.box.normal.background=Fill(panel,panel);skin.box.border=new RectOffset();
            skin.box.padding=new RectOffset(14,14,12,12);
            var normalTexture=Fill(button,button);var hoverTexture=Fill(hover,hover);var pressedTexture=Fill(pressed,pressed);
            skin.button.font=font;skin.button.fontSize=14;skin.button.fontStyle=nativeText!=null?nativeText.fontStyle:FontStyle.Normal;
            skin.button.richText=false;skin.button.alignment=TextAnchor.MiddleCenter;
            skin.button.border=new RectOffset();skin.button.padding=new RectOffset(7,7,5,5);
            skin.button.margin=new RectOffset(3,3,3,3);skin.button.fixedHeight=30;
            skin.button.normal.background=normalTexture;skin.button.normal.textColor=text;
            skin.button.hover.background=hoverTexture;skin.button.hover.textColor=text;
            skin.button.active.background=pressedTexture;skin.button.active.textColor=text;
            skin.button.focused.background=hoverTexture;skin.button.focused.textColor=text;
            skin.textField.font=font;skin.textField.fontSize=14;skin.textField.richText=false;
            Color input=new Color(.1f,.1f,.1f,.8f),inputFocus=new Color(.15f,.15f,.15f,.9f);
            skin.textField.normal.background=Fill(input,input);
            skin.textField.focused.background=Fill(inputFocus,inputFocus);
            skin.textField.hover.background=skin.textField.focused.background;
            skin.textField.normal.textColor=skin.textField.focused.textColor=skin.textField.hover.textColor=text;
            skin.textField.border=new RectOffset();skin.textField.padding=new RectOffset(8,8,5,5);skin.textField.fixedHeight=30;
            skin.toggle.font=font;skin.toggle.fontSize=13;skin.toggle.richText=false;
            skin.toggle.normal.textColor=skin.toggle.hover.textColor=skin.toggle.onNormal.textColor=skin.toggle.onHover.textColor=text;
            skin.toggle.normal.background=normalTexture;skin.toggle.hover.background=hoverTexture;
            skin.toggle.active.background=pressedTexture;skin.toggle.onNormal.background=hoverTexture;
            skin.toggle.onHover.background=hoverTexture;skin.toggle.onActive.background=pressedTexture;
            skin.toggle.border=new RectOffset();skin.toggle.padding=new RectOffset(8,8,5,5);skin.toggle.fixedHeight=30;
            skin.horizontalScrollbarThumb.normal.background=normalTexture;
            skin.verticalScrollbarThumb.normal.background=normalTexture;
            skin.horizontalScrollbarThumb.hover.background=hoverTexture;skin.horizontalScrollbarThumb.active.background=pressedTexture;
            skin.verticalScrollbarThumb.hover.background=hoverTexture;skin.verticalScrollbarThumb.active.background=pressedTexture;
            skin.settings.cursorColor=text;skin.settings.selectionColor=new Color(border.r,border.g,border.b,.5f);
            foundNativeStyle=nativeText!=null;
        }
        public static void Panel(Rect rect,string caption=null)
        {
            GUI.Box(rect,GUIContent.none);
            if(caption!=null)GUI.Label(new Rect(rect.x+18,rect.y+12,rect.width-36,30),caption);
        }
        private static void ClearOwned()
        {
            if(skin!=null)UnityEngine.Object.Destroy(skin);skin=null;
            foreach(var texture in textures)if(texture!=null)UnityEngine.Object.Destroy(texture);
            textures.Clear();
        }
        public static void Dispose(){ClearOwned();foundNativeStyle=false;scene=-1;}
    }
}
