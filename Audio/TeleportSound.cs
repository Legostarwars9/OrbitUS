using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Orbit_Us
{
    // One local, non-spatial voice. Call only after a teleport event is accepted.
    public sealed class TeleportSound:MonoBehaviour
    {
        private static TeleportSound instance;
        private AudioSource source;
        private AudioClip clip;
        private UnityWebRequest request;
        private string path;
        private bool playWhenReady,warnedMissing;

        public void Initialize(string path)
        {
            instance=this;
            this.path=Path.GetFullPath(path);
            source=gameObject.AddComponent<AudioSource>();
            source.playOnAwake=false;source.loop=false;source.spatialBlend=0;source.dopplerLevel=0;
            EnsureLoaded();
        }
        private void EnsureLoaded()
        {
            if(clip!=null || request!=null)return;
            if(!File.Exists(path))
            {
                if(!warnedMissing)Debug.LogWarning("[Orbit-Us] Teleport sound not found: "+path);
                warnedMissing=true;playWhenReady=false;return;
            }
            warnedMissing=false;StartCoroutine(Load(path));
        }
        private IEnumerator Load(string path)
        {
            var loading=UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri,AudioType.WAV);
            request=loading;
            try
            {
                yield return loading.SendWebRequest();
                if(loading.result!=UnityWebRequest.Result.Success)
                {playWhenReady=false;Debug.LogWarning("[Orbit-Us] Could not load teleport.wav: "+loading.error);yield break;}
                clip=DownloadHandlerAudioClip.GetContent(loading);
                source.clip=clip;
                if(playWhenReady){playWhenReady=false;PlayLocal();}
            }
            finally
            {
                if(ReferenceEquals(request,loading))request=null;
                loading.Dispose();
            }
        }
        public static void PlayOnce()
        {
            var sound=instance;
            if(sound==null || sound.source==null)return;
            if(sound.clip==null)
            {
                // One pending cue, even if several peers teleport while loading.
                sound.playWhenReady=true;sound.EnsureLoaded();return;
            }
            sound.PlayLocal();
        }
        private void PlayLocal()
        {
            if(source==null || clip==null)return;
            var manager=audioManager.Instance;
            source.volume=manager!=null?Mathf.Clamp01(manager.GetMasterVolumeMult()):1f;
            // Play rather than PlayOneShot: a new teleport cannot layer extra voices.
            source.Stop();source.Play();
        }
        private void OnDestroy()
        {
            StopAllCoroutines();
            if(request!=null){request.Abort();request.Dispose();request=null;}
            if(source!=null)UnityEngine.Object.Destroy(source);
            if(clip!=null)UnityEngine.Object.Destroy(clip);
            if(instance==this)instance=null;
        }
    }
}
