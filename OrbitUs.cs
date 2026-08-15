namespace Orbit_Us
{
    using BepInEx;
    using System.IO;
    using UnityEngine;
    using UnityEngine.UI;
    
    [BepInPlugin("Orbit-Us.test", "Orbit-Us", "0.0.2")]
    public class OrbitUs : BaseUnityPlugin
    {
        private void Awake()
        {
            Logger.LogInfo("Orbit Us Loaded");
            LoadAssets();
            LoadImage();
        }

        void LoadAssets()
        {
            string modPath = Path.GetDirectoryName(Info.Location);
            string assetPath = Path.Combine(modPath, "Orbit_Us-Assets");
           
            if (!Directory.Exists(assetPath))
            {
                Logger.LogWarning("Assets Folder Not Found");
            }
            else
            {
                Logger.LogInfo($"Assets loaded at: {assetPath}");
            }
        }

        void LoadImage()
        {
            string modPath = Path.GetDirectoryName(Info.Location);
            string imagePath = Path.Combine(modPath, "Orbit_Us-Assets", "Fing.png");

            if (!File.Exists(imagePath))
            {
                Logger.LogWarning("Image Not Found");
            }
            
            byte[] data = File.ReadAllBytes(imagePath);
            
            Texture2D tex = new Texture2D(2, 2);
            tex.LoadImage(data);
            
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            Logger.LogInfo($"Sprite loaded at: {imagePath}");
            Logger.LogInfo($"Sprite loaded with dimentions: {tex.width}x{tex.height}");
            
            GameObject canvasObject = new GameObject("OrbitUsCanvas");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            GameObject imageObject = new GameObject("OrbitUsTestImage");
            imageObject.transform.SetParent(canvasObject.transform, false);
            Image image = imageObject.AddComponent<Image>();
            image.sprite = sprite;
            RectTransform rectTransform = imageObject.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(tex.width, tex.height);
            rectTransform.anchoredPosition = new Vector2(120, 120);
        }
    }
}