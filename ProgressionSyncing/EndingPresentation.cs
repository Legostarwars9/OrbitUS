using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Orbit_Us
{
    // Replicate the native sequence's state, not its world-mutating coroutines.
    // A late join can enter any phase without rerunning boss spawns or rewards.
    public sealed class EndingPresentation
    {
        private int phase,amplitude,frequency,appliedPhase,appliedAmplitude,appliedFrequency;
        private bool exoticsMade,saved,explosionPlayed,seenBlackHole;
        private float oldBloom,oldThreshold,oldLens;
        private bool oldRelativistic;
        private static readonly FieldInfo lens=AccessTools.Field(typeof(devourerWarper),"lensDistortion");
        public void Notify(string method)
        {
            switch(method)
            {
                case "SpawnInfallParticle":phase=System.Math.Max(phase,1);break;
                case "StartOscillation":phase=System.Math.Max(phase,2);break;
                case "AllApproachPlayer":phase=System.Math.Max(phase,3);break;
                case "SpawnExotics":phase=System.Math.Max(phase,4);break;
                case "DeleteParticles":phase=System.Math.Max(phase,5);break;
                case "IncreaseParticleAmps":amplitude++;break;
                case "IncreaseParticleFrequency":frequency++;break;
            }
        }
        public EndingEffectsState Capture(devourerWarper w,singularityManager s)
        {
            var fx=new EndingEffectsState();if(w==null || s==null)return fx;
            bool exotic=false;if(s.bodies!=null)foreach(var b in s.bodies)if(b!=null && b.GetComponent<player>()==null){exotic=true;break;}
            int count=0;bool oscillating=false;
            if(s.allParticles!=null)foreach(var p in s.allParticles)if(p!=null){count++;oscillating|=p.oscillating;}
            // Hosting can begin in an already-running scene/sequence.
            if(w.warped && s.enabled)phase=System.Math.Max(phase,1);
            if(oscillating)phase=System.Math.Max(phase,2);
            if(exotic)phase=System.Math.Max(phase,count==0?5:4);
            fx.Phase=phase;fx.AmplitudeSteps=amplitude;fx.FrequencySteps=frequency;fx.ParticleCount=System.Math.Min(count,1000);
            fx.Spawning=s.spawning;fx.Exotics=exotic;fx.Controls=playerController.Instance!=null && playerController.Instance.enabled;
            fx.RelativisticCamera=w.relCamEf!=null && w.relCamEf.enabled;
            fx.CameraAtBlackHole=w.cameraMove.leader==w.blackHole.transform;fx.BlackHoleActive=w.blackHole.gameObject.activeSelf;
            if(w.bloom!=null){fx.BloomIntensity=w.bloom.intensity.value;fx.BloomThreshold=w.bloom.threshold.value;}
            var distortion=lens.GetValue(w) as LensDistortion;if(distortion!=null)fx.LensDistortion=distortion.intensity.value;
            foreach(var t in w.centralDarknesses)
                fx.Darkness.Add(new DarknessPose {Active=t!=null && t.gameObject.activeSelf,X=t!=null?t.localScale.x:0,Y=t!=null?t.localScale.y:0,Z=t!=null?t.localScale.z:0});
            return fx;
        }
        public void Apply(EndingEffectsState fx,devourerWarper w,singularityManager s)
        {
            if(w==null || s==null)return;
            if(s.allParticles==null)s.allParticles=new List<infallParticle>();
            if(s.bodies==null)s.bodies=new List<gravity>();
            if(!saved)
            {
                saved=true;oldRelativistic=w.relCamEf!=null && w.relCamEf.enabled;
                if(w.bloom!=null){oldBloom=w.bloom.intensity.value;oldThreshold=w.bloom.threshold.value;}
                var distortion=lens.GetValue(w) as LensDistortion;if(distortion!=null)oldLens=distortion.intensity.value;
            }
            if(w.relCamEf!=null)w.relCamEf.enabled=fx.RelativisticCamera;
            if(w.bloom!=null){w.bloom.intensity.value=fx.BloomIntensity;w.bloom.threshold.value=fx.BloomThreshold;}
            var lensEffect=lens.GetValue(w) as LensDistortion;if(lensEffect!=null)lensEffect.intensity.value=fx.LensDistortion;
            for(int i=0;i<w.centralDarknesses.Length && i<fx.Darkness.Count;i++)
            {
                var t=w.centralDarknesses[i];if(t==null)continue;var d=fx.Darkness[i];
                t.localScale=new Vector3(d.X,d.Y,d.Z);t.gameObject.SetActive(d.Active);
            }
            if(playerController.Instance!=null)playerController.Instance.enabled=fx.Controls;
            if(fx.CameraAtBlackHole)w.cameraMove.leader=w.blackHole.transform;
            else if(player.Instance!=null)w.cameraMove.leader=player.Instance.transform;
            s.spawning=fx.Spawning && fx.Phase<5;
            if(fx.Phase>0 && fx.Phase<5)
            {
                for(int i=s.allParticles.Count;i<System.Math.Min(fx.ParticleCount,300);i++)
                    s.allParticles.Add(s.SpawnInfallParticle((Vector2)player.Instance.transform.position+spawner.RandomPos(5,100)));
            }
            if(fx.Phase>=2 && appliedPhase<2){s.StartOscillation();appliedAmplitude=0;appliedFrequency=0;}
            for(int i=appliedAmplitude;i<fx.AmplitudeSteps;i++)s.IncreaseParticleAmps();
            for(int i=appliedFrequency;i<fx.FrequencySteps;i++)s.IncreaseParticleFrequency();
            appliedAmplitude=fx.AmplitudeSteps;appliedFrequency=fx.FrequencySteps;
            if(fx.Phase>=3 && appliedPhase<3)s.AllApproachPlayer();
            if(fx.Exotics && !exoticsMade){s.SpawnExotics();exoticsMade=true;}
            if(!fx.Exotics && exoticsMade)
            {
                for(int i=s.bodies.Count-1;i>=0;i--)
                    if(s.bodies[i]==null || s.bodies[i].GetComponent<player>()==null)
                    {if(s.bodies[i]!=null)UnityEngine.Object.Destroy(s.bodies[i].gameObject);s.bodies.RemoveAt(i);}
                exoticsMade=false;
            }
            if(fx.Phase>=5 && appliedPhase<5)s.DeleteParticles();
            // The good-ending explosion is transient; infer it from the host's
            // active-to-inactive black hole edge rather than replaying at every snapshot.
            if(seenBlackHole && !fx.BlackHoleActive && !explosionPlayed && fx.CameraAtBlackHole)
            {
                explosionPlayed=true;
                UnityEngine.Object.Instantiate(s.blackHoleExplosionParticles,w.blackHole.transform.position,Quaternion.identity);
                audioManager.Instance?.Play("Supernova");
            }
            w.blackHole.gameObject.SetActive(fx.BlackHoleActive);
            seenBlackHole|=fx.BlackHoleActive;
            appliedPhase=fx.Phase;
        }
        public void Restore(devourerWarper w)
        {
            if(!saved || w==null)return;
            if(w.relCamEf!=null)w.relCamEf.enabled=oldRelativistic;
            if(w.bloom!=null){w.bloom.intensity.value=oldBloom;w.bloom.threshold.value=oldThreshold;}
            var distortion=lens.GetValue(w) as LensDistortion;if(distortion!=null)distortion.intensity.value=oldLens;
        }
    }
    [HarmonyPatch]
    public static class EndingEffectPhasePatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach(string name in new[]{"SpawnInfallParticle","StartOscillation","AllApproachPlayer","SpawnExotics","DeleteParticles","IncreaseParticleAmps","IncreaseParticleFrequency"})
                yield return AccessTools.DeclaredMethod(typeof(singularityManager),name);
        }
        public static void Postfix(MethodBase __originalMethod)
        {if(OrbitUs.IsHost)ProgressReplicator.Instance?.NotifyEndingEffect(__originalMethod.Name);}
    }
}
