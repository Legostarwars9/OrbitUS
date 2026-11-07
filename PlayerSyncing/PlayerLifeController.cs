using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit_Us
{
    public sealed class PlayerLifeController
    {
        private readonly PlayerReplicator network;
        private readonly Dictionary<Behaviour,bool> behaviours=new Dictionary<Behaviour,bool>();
        private readonly Dictionary<Collider2D,bool> colliders=new Dictionary<Collider2D,bool>();
        private readonly Dictionary<Renderer,bool> renderers=new Dictionary<Renderer,bool>();
        private player local;
        private PlayerBuildSnapshot saved;
        private cameraMove camera;
        private GameObject deathScreen,hud,hurtScreen,pressE;
        private bool hudVisible,simulated,controllerEnabled,active=true,restoreColliders;
        private float protectedUntil;
        private GUIStyle titleStyle,detailStyle;
        public Vector2 DeathPosition {get;private set;}
        public bool IsSpectating=>saved!=null && local!=null;
        public bool IgnoreDamage=>IsSpectating || Time.unscaledTime<protectedUntil;
        public bool ControlsBeforeDeath=>controllerEnabled;
        public static bool Spectating=>PlayerReplicator.Instance?.Life.IsSpectating==true;
        public PlayerLifeController(PlayerReplicator network){this.network=network;}

        private static GameObject UI(player source,string field)
            =>BuildFieldSnapshot.Field(typeof(player),field).GetValue(source) as GameObject;
        private static void Hide(GameObject value){if(value!=null)value.SetActive(false);}
        public bool Begin(player source)
        {
            if(!active || source==null || source!=player.Instance || source.rb==null)return false;
            if(IsSpectating)return true;
            if(Time.unscaledTime<protectedUntil){source.dead=false;source.hp=Mathf.Max(1,source.hpMax);return true;}
            if(source.shooter!=null)
            {
                foreach(var button in source.shooter.modifierButtons)
                    if(button!=null && (bool)BuildFieldSnapshot.Field(typeof(modifierButton),"held").GetValue(button))button.OnPointerUp(null);
                foreach(var button in source.shooter.projButtons)
                    if(button!=null && (bool)BuildFieldSnapshot.Field(typeof(projectileButton),"held").GetValue(button))button.OnPointerUp(null);
            }
            local=source;saved=PlayerBuildSnapshot.Capture(source);DeathPosition=source.rb.position;
            deathScreen=UI(local,"deathScreen");hud=UI(local,"inGameUI");hurtScreen=UI(local,"hurtScreen");pressE=UI(local,"pressEScreen");
            hudVisible=hud!=null && (hud.activeSelf || (deathScreen!=null && deathScreen.activeSelf));simulated=source.rb.simulated;
            bool nativeDeath=deathScreen!=null && deathScreen.activeSelf;
            var controller=local.GetComponent<playerController>();controllerEnabled=controller!=null && (controller.enabled || nativeDeath);
            behaviours.Clear();colliders.Clear();renderers.Clear();
            foreach(var behaviour in local.GetComponentsInChildren<MonoBehaviour>(true))
                if(behaviour is player || behaviour is playerController || behaviour is shooterWithLoadout || behaviour is movement ||
                    behaviour is rotateToMouse || behaviour is gravity || behaviour is temperature || behaviour is collisionBody ||
                    behaviour is dragBody || behaviour is kickStart || behaviour is magicOrbit || behaviour is relativisticCamEffects)
                {
                    bool enabled=behaviour.enabled;
                    if(nativeDeath && (behaviour is playerController || behaviour is rotateToMouse))enabled=true;
                    behaviours[behaviour]=enabled;behaviour.StopAllCoroutines();behaviour.enabled=false;
                }
            foreach(var collider in local.GetComponentsInChildren<Collider2D>(true)){colliders[collider]=collider.enabled;collider.enabled=false;}
            foreach(var renderer in local.GetComponentsInChildren<Renderer>(true)){renderers[renderer]=renderer.enabled;renderer.enabled=false;}
            var movement=local.GetComponent<movement>();
            if(movement!=null){movement.BurnerToggle(false);movement.SuperBurnerToggle(false);}
            local.StopAllCoroutines();local.dead=true;local.hp=0;local.rb.velocity=Vector2.zero;local.rb.angularVelocity=0;local.rb.simulated=false;
            local.rb.mass=0.0001f;
            if(local.shooter!=null){local.shooter.grappleOut=false;local.shooter.inventoryOpen=false;}
            Hide(local.inventory);Hide(local.shopUI);Hide(local.healParticles);Hide(UI(local,"fuelWarning"));Hide(UI(local,"supernovaWarning"));
            var time=UnityEngine.Object.FindObjectOfType<timeManager>();if(time!=null)time.playerTimeWarping=false;
            camera=Camera.main!=null?Camera.main.GetComponent<cameraMove>():null;
            Enforce();return true;
        }
        private void Enforce()
        {
            if(!IsSpectating)return;
            local.dead=true;local.hp=0;local.rb.simulated=false;
            foreach(var pair in behaviours)if(pair.Key!=null)pair.Key.enabled=false;
            foreach(var pair in colliders)if(pair.Key!=null)pair.Key.enabled=false;
            foreach(var pair in renderers)if(pair.Key!=null)pair.Key.enabled=false;
            Hide(deathScreen);Hide(hud);Hide(hurtScreen);Hide(pressE);Hide(local.inventory);Hide(local.shopUI);
        }
        public void Update()
        {
            if(!active)return;
            if(restoreColliders && !IsSpectating && Time.unscaledTime>=protectedUntil)RestoreColliders();
            if(!IsSpectating && player.Instance!=null && player.Instance.dead)network.EnterSpectator(player.Instance);
            if(!IsSpectating)return;
            network.Spectators.EnsureTarget(Time.unscaledTime);
            if(Input.GetKeyDown(KeyCode.A))network.Spectators.Cycle(-1,Time.unscaledTime);
            if(Input.GetKeyDown(KeyCode.D))network.Spectators.Cycle(1,Time.unscaledTime);
            Enforce();Follow();
        }
        private void Follow()
        {
            if(!IsSpectating)return;
            Transform target=network.SpectatorTarget();
            if(camera==null && Camera.main!=null)camera=Camera.main.GetComponent<cameraMove>();
            if(camera!=null)camera.leader=target;
            if(target==null)return;
            // Keep the host's world streaming around the living pilot it is watching.
            // Outgoing dead-player movement still reports the original death position.
            local.transform.position=target.position;local.rb.position=target.position;
            local.rb.velocity=network.RemoteVelocity(network.Spectators.TargetId);
            if(camera!=null)camera.transform.position=new Vector3(target.position.x+camera.offset.x,target.position.y+camera.offset.y,camera.transform.position.z);
        }
        public void LateUpdate(){if(IsSpectating){Enforce();Follow();}}
        public bool Revive(Vector2 position,Vector2 velocity)
        {
            if(!IsSpectating)return false;
            saved.Restore(local);local.dead=false;
            foreach(var pair in behaviours)if(pair.Key!=null)pair.Key.enabled=pair.Value;
            // Delay collisions briefly so queued damage from the old life cannot
            // immediately kill the freshly restored player at the gather point.
            restoreColliders=true;
            foreach(var pair in renderers)if(pair.Key!=null)pair.Key.enabled=pair.Value;
            local.rb.simulated=simulated;local.rb.angularVelocity=0;local.rb.position=position;
            local.transform.position=new Vector3(position.x,position.y,local.transform.position.z);local.rb.velocity=velocity;local.rb.WakeUp();
            foreach(var trail in local.GetComponentsInChildren<TrailRenderer>(true))trail.Clear();
            Hide(deathScreen);Hide(hurtScreen);Hide(pressE);Hide(local.inventory);Hide(local.shopUI);
            if(hud!=null)hud.SetActive(hudVisible);
            if(camera!=null)camera.leader=local.transform;
            protectedUntil=Time.unscaledTime+1f;saved=null;behaviours.Clear();renderers.Clear();
            return true;
        }
        public void Draw()
        {
            if(!IsSpectating)return;
            if(titleStyle==null)
            {
                titleStyle=new GUIStyle(GUI.skin.label){fontSize=22,fontStyle=FontStyle.Bold,alignment=TextAnchor.MiddleCenter};
                detailStyle=new GUIStyle(GUI.skin.label){fontSize=15,alignment=TextAnchor.MiddleCenter};
            }
            Rect panel=new Rect((Screen.width-520)/2f,Screen.height-135,520,115);
            GUI.Box(panel,GUIContent.none);
            int target=network.Spectators.EnsureTarget(Time.unscaledTime);
            string title=target>0?"Spectating "+network.PlayerLabel(target):"No living players to spectate";
            GUI.Label(new Rect(panel.x,panel.y+4,panel.width,30),title,titleStyle);
            GUI.Label(new Rect(panel.x,panel.y+34,panel.width,26),target>0?"Build and money saved. Revive on the next upload or boss defeat.":"Build saved. Waiting for a task revive. Escape opens the menu.",detailStyle);
            if(GUI.Button(new Rect(panel.x+70,panel.y+72,145,30),"A  Previous"))network.Spectators.Cycle(-1,Time.unscaledTime);
            if(GUI.Button(new Rect(panel.x+305,panel.y+72,145,30),"Next  D"))network.Spectators.Cycle(1,Time.unscaledTime);
        }
        public void Dispose()
        {
            active=false;
            if(IsSpectating)
            {
                Vector2 position=DeathPosition;Revive(position,Vector2.zero);
                local.hp=0;local.dead=true;local.Die();
            }
            RestoreColliders();
            saved=null;
        }
        public void EndRun()
        {
            active=false;protectedUntil=0;
            // Release the spectator shell without restoring the saved build.
            // The native reset now owns inventory, money, story and death UI.
            if(IsSpectating)
            {
                foreach(var pair in behaviours)if(pair.Key!=null)pair.Key.enabled=pair.Value;
                foreach(var pair in renderers)if(pair.Key!=null)pair.Key.enabled=pair.Value;
                local.rb.simulated=simulated;
                if(camera!=null)camera.leader=local.transform;
            }
            RestoreColliders();saved=null;behaviours.Clear();renderers.Clear();
        }
        private void RestoreColliders()
        {foreach(var pair in colliders)if(pair.Key!=null)pair.Key.enabled=pair.Value;colliders.Clear();restoreColliders=false;}
    }
}
