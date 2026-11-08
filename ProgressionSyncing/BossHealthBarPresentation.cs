using System;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    public sealed class BossHealthFillLayout
    {
        public float Width { get; private set; }
        public float Height { get; private set; }
        public float Depth { get; private set; }
        private bool initialized;

        public void Initialize(float width, float height, float depth)
        {
            if (initialized) return;
            Width = width;
            Height = height;
            Depth = depth;
            initialized = true;
        }

        public float Fraction(float hp, float maximum)
        {
            if (float.IsNaN(hp) || float.IsInfinity(hp) ||
                float.IsNaN(maximum) || float.IsInfinity(maximum) || maximum <= 0f)
                return 0f;
            return Math.Max(0f, Math.Min(1f, hp / maximum));
        }
    }

    public sealed class BossHealthBarPresentation : MonoBehaviour
    {
        private readonly BossHealthFillLayout layout = new BossHealthFillLayout();
        private bossHealthBar target;
        public GameObject ProgressDataBar;

        public static BossHealthBarPresentation Prepare(bossHealthBar ui)
        {
            var view = ui.GetComponent<BossHealthBarPresentation>();
            if (view == null) view = ui.gameObject.AddComponent<BossHealthBarPresentation>();
            view.target=ui;
            Vector3 scale = ui.bar.localScale;
            // A bar already initialized in single player has a reliable full size.
            float width = ui.startingSize.x != 0f ? ui.startingSize.x : scale.x;
            float height = ui.startingSize.y != 0f ? ui.startingSize.y : scale.y;
            view.layout.Initialize(width, height, scale.z);
            ui.startingSize = new Vector2(view.layout.Width, view.layout.Height);
            return view;
        }

        private void LateUpdate()
        {
            if(!OrbitUs.MultiplayerActive || target==null || !target.gameObject.activeInHierarchy)return;
            // All bosses share this HUD. Client bossHealthBar.Update is suppressed,
            // so preserve its data-display exclusion even after other HUD updates.
            if(target.dataBar!=null)target.dataBar.SetActive(false);
            if(ProgressDataBar!=null)ProgressDataBar.SetActive(false);
            if(target.dataBar!=null && transform.parent==target.dataBar.transform.parent &&
                transform.GetSiblingIndex()<target.dataBar.transform.GetSiblingIndex())transform.SetAsLastSibling();
        }

        public void Draw(bossHealthBar ui, float hp, float maximum)
        {
            float fraction = layout.Fraction(hp, maximum);
            ui.bar.localScale = new Vector3(layout.Width * fraction, layout.Height, layout.Depth);
            ui.bar.anchoredPosition = new Vector2(-97.5f + 97.5f * fraction, 0f);
        }
    }

    [HarmonyPatch(typeof(bossHealthBar), "Start")]
    public static class ClientBossHealthBarStartPatch
    {
        public static bool Prefix(bossHealthBar __instance)
        {
            if (ProgressReplicator.Instance?.IsClient != true) return true;
            BossHealthBarPresentation.Prepare(__instance);
            // Native Start would capture the already partially filled scale.
            return false;
        }
    }
}
