using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    public sealed class ProgressReplicator
    {
        private sealed class Piece
        {
            public dataBlob Blob;
            public DataPieceState State;
            public Vector2 Offset;
            public bool HasParent;
        }
        private sealed class BossCredit {public int PlayerId;public float Received;}
        private readonly NetworkUDPConnection connection;
        private readonly bool isHost;
        private readonly Dictionary<dataBlob,int> ids = new Dictionary<dataBlob,int>();
        private readonly Dictionary<int,dataBlob> hostPieces = new Dictionary<int,dataBlob>();
        private readonly HashSet<int> collected = new HashSet<int>();
        private readonly Dictionary<int,Piece> pieces = new Dictionary<int,Piece>();
        private readonly Dictionary<string,GameObject> prefabs = new Dictionary<string,GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<int,ProgressAction> pending = new Dictionary<int,ProgressAction>();
        private readonly Dictionary<int,DamageRequestHistory> history = new Dictionary<int,DamageRequestHistory>();
        private readonly Dictionary<int,Vector2> positions = new Dictionary<int,Vector2>();
        private readonly Dictionary<int,PlayerTransformData> motions=new Dictionary<int,PlayerTransformData>();
        private readonly Dictionary<int,float> motionReceived=new Dictionary<int,float>();
        private readonly Dictionary<health,BossCredit> bossCredits=new Dictionary<health,BossCredit>();
        private readonly HashSet<health> completedBosses=new HashSet<health>();
        private readonly UploadCompletionTracker uploadCompletion=new UploadCompletionTracker();
        private int transmissionRequester;
        private readonly ProgressSnapshotCodec.Assembler snapshots = new ProgressSnapshotCodec.Assembler();
        private static readonly FieldInfo checklist = AccessTools.Field(typeof(storyManager), "missionChecklist");
        private static readonly FieldInfo targetType = AccessTools.Field(typeof(tracker), "targetType");
        private storyManager story;
        private player player;
        private bossManager bosses;
        private singularityManager singularity;
        private devourerWarper warper;
        private GameObject staging;
        private ProgressState latest;
        private int nextPiece = 1, nextRequest = 1, nextSnapshot = 1, ending;
        private readonly EndingPresentation endingPresentation=new EndingPresentation();
        public void NotifyEndingEffect(string name)=>endingPresentation.Notify(name);
        private bool initialized, sending, applying, wasWarped, needsApply;
        private float timer, retryTimer, scanTimer;
        private int lastReward;
        public static ProgressReplicator Instance { get; private set; }
        public bool IsClient => initialized && !isHost;
        public bool HasState => latest != null;
        public bool Applying => applying;

        public ProgressReplicator(NetworkUDPConnection connection, bool host) { this.connection=connection; isHost=host; }
        public void Initialize()
        {
            Instance=this; initialized=true;
            connection.OnPacketReceived+=HandlePacket;
            Refresh();
        }
        private void Refresh()
        {
            var current=UnityEngine.Object.FindObjectOfType<storyManager>(true);
            if(current!=story)
            {
                story=current; player=story!=null?story.player:null; bosses=story!=null?story.bossManager:null;
                if(IsClient && story!=null)
                {
                    story.StopAllCoroutines();
                    foreach(var d in Dialogues()) if(d!=null) d.StopAllCoroutines();
                }
                lastReward=0;
                uploadCompletion.Clear();bossCredits.Clear();completedBosses.Clear();
            }
            singularity=UnityEngine.Object.FindObjectOfType<singularityManager>(true);
            warper=UnityEngine.Object.FindObjectOfType<devourerWarper>(true);
            foreach(var blob in Resources.FindObjectsOfTypeAll<dataBlob>())
                if(blob!=null && !blob.gameObject.scene.IsValid()) prefabs[blob.gameObject.name]=blob.gameObject;
        }
        private List<dialogue> Dialogues()
        {
            var list=new List<dialogue>();
            if(story==null) return list;
            list.Add(story.dialogue);list.Add(story.secondaryDialogue);list.Add(story.livelyDialogue);
            list.AddRange(story.otherDialogues);
            list.Add(warper!=null?warper.dialogueBox:null);
            list.Add(singularity!=null?singularity.endingDialogue:null);
            list.Add(singularity!=null?singularity.badEndingDialogue:null);
            return list;
        }
        public void Update()
        {
            if(!initialized) return;
            scanTimer+=Time.unscaledDeltaTime;
            if(scanTimer>=1 || story==null) {scanTimer=0;Refresh();}
            if(story==null || player==null) return;
            if(IsClient && needsApply && latest!=null) Apply(latest);
            if(isHost)
            {
                int uploader=uploadCompletion.Complete(story.transmissionIndex,story.researchReward);
                if(uploader>0)GatherAt(uploader,TaskTeleportReason.Upload);
                if(player.hasFinalData && !story.fragmentHunting && warper!=null && !warper.warped)
                {story.tracker.ChangeType(2);story.tracker.tooFarRange=1E12f;story.tracker.ChangeTarget(warper.transform);}
                timer+=Time.unscaledDeltaTime;
                if(timer>=0.5f && !sending && connection.LocalPlayerId>0) {timer=0;SendSnapshot();}
            }
            else
            {
                if(latest!=null)
                {
                    // The late-join state can arrive before planet/enemy snapshots.
                    ApplyReferences(latest);
                    foreach(var pair in pieces)
                    {
                        var piece=pair.Value;
                        if(piece.Blob==null) continue;
                        var parent=PlanetReplicator.Instance?.ResolveObject(piece.State.PlanetId);
                        if(parent!=null)
                        {
                            if(!piece.HasParent) {piece.Offset=new Vector2(piece.State.X,piece.State.Y)-(Vector2)parent.transform.position;piece.HasParent=true;}
                            piece.Blob.planet=parent.GetComponent<Rigidbody2D>();piece.Blob.planetTrans=parent.transform;
                            piece.Blob.rb.position=(Vector2)parent.transform.position+piece.Offset;
                            piece.Blob.rb.velocity=piece.Blob.planet.velocity;
                        }
                        if(piece.State.Active && Vector2.Distance(player.transform.position,piece.Blob.transform.position)<=4f)
                            Collect(piece.Blob);
                    }
                }
                retryTimer+=Time.unscaledDeltaTime;
                if(retryTimer>=0.35f)
                {
                    retryTimer=0;
                    foreach(var action in new List<ProgressAction>(pending.Values)) _=connection.SendProgressAction(action);
                }
            }
        }
        public void StepData(dataBlob blob)
        {
            if(!isHost || blob==null || player==null) return;
            blob.Extra();
            if(blob==null || !blob.chasing) return;
            Vector2 nearest=player.transform.position;
            float distance=Vector2.SqrMagnitude(nearest-(Vector2)blob.transform.position);
            foreach(var position in positions.Values)
            {
                float candidate=Vector2.SqrMagnitude(position-(Vector2)blob.transform.position);
                if(candidate<distance) {distance=candidate;nearest=position;}
            }
            // Data drops follow the nearest pilot; the original item's 1000-unit
            // host-only despawn would otherwise delete drops near remote pilots.
            Vector2 direction=(nearest-(Vector2)blob.transform.position).normalized;
            blob.rb.AddForce(direction*(distance<100?100:25)*blob.rb.mass);
        }

        private int EnsureId(dataBlob blob)
        {
            if(!ids.TryGetValue(blob,out int id)) {id=nextPiece++;ids.Add(blob,id);hostPieces[id]=blob;}
            return id;
        }
        public void Collect(dataBlob blob)
        {
            if(!initialized || blob==null || story==null || player==null || player.dead || !blob.gameObject.activeInHierarchy) return;
            if(isHost) {CollectHost(EnsureId(blob),player.transform.position);return;}
            if(latest==null || (!blob.isFinal && latest.Data>=latest.DataMax) || (!blob.chasing && !latest.FragmentHunting)) return;
            foreach(var pair in pieces) if(pair.Value.Blob==blob) {Request(ProgressActionKind.Collect,pair.Key);return;}
        }
        private void CollectHost(int id,Vector2 position)
        {
            if(collected.Contains(id) || !hostPieces.TryGetValue(id,out dataBlob blob) || blob==null || !blob.gameObject.activeInHierarchy) return;
            if(Vector2.Distance(position,blob.transform.position)>32f || (!blob.chasing && !story.fragmentHunting) || (!blob.isFinal && player.data>=player.dataMax)) return;
            collected.Add(id);
            player.CollectData();
            player.takenDataBlobs.Add(blob);
            if(blob.isFinal)
            {
                player.hasFinalData=true;
                SetFinalEffects(true);
                story.StartPlanetBossAftermath();
            }
            blob.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(blob.gameObject);
            timer=1;
        }
        public void Request(ProgressActionKind kind,int arg=0)
        {
            if(!initialized || story==null || player==null || player.dead || applying) return;
            if(isHost) return;
            if(connection.LocalPlayerId<=0 || pending.Count>=64) return;
            foreach(var a in pending.Values) if(a.Kind==kind && a.Argument==arg) return;
            var request=new ProgressAction {RequestId=nextRequest++,Kind=kind,Argument=arg,ExpectedTransmission=latest!=null?latest.Transmission:story.transmissionIndex};
            pending.Add(request.RequestId,request);
            _=connection.SendProgressAction(request);
        }
        public bool BeginTransmission()
        {
            if(IsClient) {Request(ProgressActionKind.Transmit);return false;}
            if(transmissionRequester<=0 && player!=null && player.dead)return false;
            if(story==null || player==null || uploadCompletion.Active || player.data<player.dataMax || story.transmissionIndex>=7 || !story.PlayerIsSafe()) return false;
            int uploader=transmissionRequester>0?transmissionRequester:connection.LocalPlayerId;
            return uploadCompletion.Begin(uploader,story.transmissionIndex,story.researchReward);
        }
        public void RecordEnding(int value) {if(isHost && ending==0) ending=value;}
        public bool EndingStarted => ending!=0;
        public bool CanEnd => player!=null && player.hasFinalData && warper!=null && warper.warped;
        private bool Position(int id,out Vector2 position)
        {
            if(id==connection.LocalPlayerId && player!=null) {position=player.transform.position;return true;}
            return positions.TryGetValue(id,out position);
        }
        private void Execute(ProgressAction action)
        {
            if(story==null || player==null || !Position(action.PlayerId,out Vector2 position)) return;
            if(motions.TryGetValue(action.PlayerId,out PlayerTransformData actorState) && actorState.HasLifeState && !actorState.Alive)return;
            switch(action.Kind)
            {
                case ProgressActionKind.Collect: CollectHost(action.Argument,position); break;
                case ProgressActionKind.OpenComms:
                    if(NearRadio(position) && story.transmissionIndex<7)
                    {
                        transmissionRequester=action.PlayerId;
                        try{story.OpenComms();}finally{transmissionRequester=0;}
                    }
                    break;
                case ProgressActionKind.CloseComms:
                    if(action.ExpectedTransmission==story.transmissionIndex && story.transmissionIndex<7 && story.currentTalking!=null && NearRadio(position)) story.CloseComms(); break;
                case ProgressActionKind.Transmit:
                    if(action.ExpectedTransmission==story.transmissionIndex && NearRadio(position))
                    {
                        transmissionRequester=action.PlayerId;
                        try{story.StartTransmission();}finally{transmissionRequester=0;}
                    }
                    break;
                case ProgressActionKind.PlantBoss:
                    var planet=PlanetReplicator.Instance?.ResolveObject(action.Argument);
                    if(planet!=null && !bosses.plantSummoned && planet.GetComponent<livelyPlanet>()!=null && planet.GetComponent<livelyPlanet>().hasBoss &&
                        Vector2.Distance(position,planet.transform.position)<planet.transform.localScale.x+50)
                        bosses.SummonPlantBoss(position,planet.GetComponent<Rigidbody2D>().velocity,planet);
                    break;
                case ProgressActionKind.EnterBlackHole:
                    if(warper!=null && player.hasFinalData && !warper.warped && Vector2.Distance(position,warper.transform.position)<warper.transform.localScale.x+50)
                        warper.MergePlayer(player); break;
                case ProgressActionKind.GoodEnding: if(CanEnd && ending==0) singularity.StartGoodEnding(); break;
                case ProgressActionKind.BadEnding: if(CanEnd && ending==0) singularity.StartBadEnding(); break;
            }
            timer=1;
        }
        private bool NearRadio(Vector2 position)
        {
            foreach(var radio in UnityEngine.Object.FindObjectsOfType<radarPlanet>())
                if(Vector2.Distance(position,radio.transform.position)<Mathf.Abs(radio.transform.lossyScale.x)*2+100) return true;
            return false;
        }
        public bool TryTransmissionSafety(out bool safe)
        {
            safe=false;if(!isHost || transmissionRequester<=0)return false;
            if(!Position(transmissionRequester,out Vector2 position))return true;
            foreach(var enemy in UnityEngine.Object.FindObjectsOfType<enemy>())
                if(Vector2.Distance(enemy.transform.position,position)<100f)return true;
            safe=true;return true;
        }
        private void HandlePacket(NetworkPacket packet)
        {
            if(!initialized) return;
            if(isHost && packet.Type==PacketType.PlayerTransform)
            {
                var t=PlayerTransformData.Deserialize(packet.Data);positions[t.PlayerId]=new Vector2(t.X,t.Y);
                motions[t.PlayerId]=t;motionReceived[t.PlayerId]=Time.unscaledTime;
            }
            if(packet.Type==PacketType.PlayerDisconnected && packet.Data.Length==4)
            {int id=BitConverter.ToInt32(packet.Data,0);positions.Remove(id);motions.Remove(id);motionReceived.Remove(id);history.Remove(id);}
            if(isHost && packet.Type==PacketType.ProgressAction)
            {
                var action=ProgressAction.Deserialize(packet.Data);
                if(!history.TryGetValue(action.PlayerId,out DamageRequestHistory seen)) {seen=new DamageRequestHistory();history.Add(action.PlayerId,seen);}
                if(seen.Accept(action.RequestId)) Execute(action);
                _=connection.SendProgressActionAck(action.PlayerId,action.RequestId);
            }
            if(!isHost && packet.Type==PacketType.ProgressActionAck && packet.Data.Length==8 && BitConverter.ToInt32(packet.Data,0)==connection.LocalPlayerId)
                pending.Remove(BitConverter.ToInt32(packet.Data,4));
            if(!isHost && packet.Type==PacketType.ProgressSnapshot && snapshots.TryAdd(packet.Data,out ProgressState state))
            {
                latest=state;needsApply=true;Refresh();
                if(story!=null && player!=null) Apply(state);
            }
        }
        private bool TaskMotion(int id,out Vector2 position,out Vector2 velocity)
        {
            position=velocity=Vector2.zero;
            if(id==connection.LocalPlayerId)
            {
                if(player==null || player.dead || !player.gameObject.activeInHierarchy || player.rb==null)return false;
                position=player.transform.position;velocity=player.rb.velocity;return true;
            }
            if(!motions.TryGetValue(id,out PlayerTransformData motion) || !motion.HasVelocity ||
                !motionReceived.TryGetValue(id,out float received) || Time.unscaledTime-received>3f ||
                CombatReplicator.Instance?.Pilots.IsLiving(id)!=true)return false;
            position=new Vector2(motion.X,motion.Y);velocity=new Vector2(motion.VX,motion.VY);return true;
        }
        private void GatherAt(int actor,TaskTeleportReason reason)
        {
            if(!isHost || !TaskDestination(actor,out Vector2 position,out Vector2 velocity))return;
            PlayerReplicator.Instance?.GatherTeam(actor,position,velocity,reason);
        }
        private bool TaskDestination(int id,out Vector2 position,out Vector2 velocity)
        {
            if(TaskMotion(id,out position,out velocity))return true;
            velocity=Vector2.zero;
            if(id==connection.LocalPlayerId && PlayerReplicator.Instance?.Life.IsSpectating==true)
            {position=PlayerReplicator.Instance.Life.DeathPosition;return true;}
            if(motions.TryGetValue(id,out PlayerTransformData state) && state.HasLifeState && !state.Alive &&
                motionReceived.TryGetValue(id,out float received) && Time.unscaledTime-received<=3f)
            {position=new Vector2(state.X,state.Y);return true;}
            return false;
        }
        private health CurrentBoss=>bosses!=null && bosses.bossHealth!=null?bosses.bossHealth.health:null;
        public void RecordBossDamage(health target,int actor)
        {
            if(!initialized || !isHost || target==null || actor<=0)return;
            var boss=CurrentBoss;if(boss==null || completedBosses.Contains(boss))return;
            var part=target.GetComponent<wormPart>();
            if(target!=boss && !target.transform.IsChildOf(boss.transform) && !(part!=null && part.worm==boss))return;
            bossCredits[boss]=new BossCredit {PlayerId=actor,Received=Time.unscaledTime};
        }
        public void RecordLocalBossDamage(health target)=>RecordBossDamage(target,connection.LocalPlayerId);
        public void BossDied(health target)
        {
            if(!initialized || !isHost || target==null || target!=CurrentBoss || !completedBosses.Add(target))return;
            int actor=0;
            if(bossCredits.TryGetValue(target,out BossCredit credit) && Time.unscaledTime-credit.Received<=1f && TaskDestination(credit.PlayerId,out _,out _))actor=credit.PlayerId;
            if(actor==0)
            {
                // Environmental deaths gather at the nearest living pilot.
                float nearest=float.PositiveInfinity;int bestId=int.MaxValue;
                var candidates=new List<int>(motions.Keys);candidates.Add(connection.LocalPlayerId);
                foreach(int id in candidates)
                    if(TaskMotion(id,out Vector2 position,out _))
                    {
                        float distance=(position-(Vector2)target.transform.position).sqrMagnitude;
                        if(CombatRules.Prefer(distance,id,nearest,bestId)){nearest=distance;bestId=id;actor=id;}
                    }
                if(actor==0)foreach(int id in candidates)
                    if(TaskDestination(id,out Vector2 position,out _))
                    {
                        float distance=(position-(Vector2)target.transform.position).sqrMagnitude;
                        if(CombatRules.Prefer(distance,id,nearest,bestId)){nearest=distance;bestId=id;actor=id;}
                    }
            }
            bossCredits.Remove(target);
            if(actor>0)GatherAt(actor,TaskTeleportReason.Boss);
        }

        private string Prefab(dataBlob blob)
        {
            var sr=blob.GetComponent<SpriteRenderer>();
            foreach(var pair in prefabs)
            {
                var candidate=pair.Value.GetComponent<dataBlob>();
                var visual=pair.Value.GetComponent<SpriteRenderer>();
                if(candidate.isFinal==blob.isFinal && candidate.name==blob.name && (sr==null || (visual!=null && visual.sprite==sr.sprite))) return pair.Key;
            }
            return "";
        }
        private async void SendSnapshot()
        {
            sending=true;
            try {await connection.SendProgressSnapshot(Capture(),nextSnapshot++);}
            catch(Exception ex) {Debug.LogError("[Orbit-Us] Progress snapshot failed: "+ex);}
            finally {sending=false;}
        }
        private ProgressState Capture()
        {
            var w=story.waveManager;var track=story.tracker;
            var state=new ProgressState
            {
                Transmission=story.transmissionIndex,ResearchReward=story.researchReward,BossIndex=story.bossIndex,Data=player.data,DataMax=player.dataMax,
                FragmentHunting=story.fragmentHunting,PlantPlanetSpawned=story.plantPlanetSpawned,WormSpawned=story.wormSpawned,PlantSummoned=bosses.plantSummoned,WormSummoned=bosses.wormSummoned,HasFinalData=player.hasFinalData,
                Wave=w.wave,WaveDuration=w.waveDuration,SecondsUntilNext=w.secondsUntilNext,TargetType=(int)targetType.GetValue(track),TargetRange=track.tooFarRange,
                TerminalActive=story.terminalBox.activeSelf,TransmitActive=story.transmitButton.activeSelf,DataBarActive=story.dataBar.activeSelf,TerminalText=story.terminalText.text,SloMo=story.timeManager.sloMo,
                Warped=warper!=null && warper.warped,Ending=ending,HostX=player.transform.position.x,HostY=player.transform.position.y,
                BadEndingScreen=singularity!=null && singularity.badEndingScreen.activeSelf,FinalQuit=singularity!=null && singularity.finalQuitButton.activeSelf
            };
            if(warper!=null)
            {
                state.HoleScreen=warper.blackHoleScreen.activeSelf;state.Bars=warper.barsAndStuff.activeSelf;state.Compass=warper.compass.activeSelf;state.Parallax=warper.parallax.activeSelf;
                state.CameraSize=warper.cameraMove.wantedSize;state.CameraMaxZoom=warper.cameraMove.maxZoom;
                state.LightIntensity=warper.globalLight.intensity;state.DarknessAlpha=warper.darkness.color.a;
                state.BackgroundR=warper.camera.backgroundColor.r;state.BackgroundG=warper.camera.backgroundColor.g;state.BackgroundB=warper.camera.backgroundColor.b;
            }
            if(singularity!=null)
            {
                state.InGameStuff=singularity.inGameStuff.activeSelf;state.SingularityEnabled=singularity.enabled;
                state.FlowX=singularity.flow.x;state.FlowY=singularity.flow.y;state.FlowSpeed=singularity.flowSpeed;state.StayForce=singularity.playerStayForce;
            }
            state.Effects=endingPresentation.Capture(warper,singularity);
            var missions=(List<bool>)checklist.GetValue(story);
            for(int i=0;i<missions.Count;i++) if(missions[i]) state.Checklist|=1<<i;
            if(track.target!=null)
            {
                state.TargetId=PlanetReplicator.Instance?.GetObjectId(track.target.gameObject)??0;state.TargetKind=1;
                if(state.TargetId==0) {state.TargetId=EnemyReplicator.Instance?.GetObjectId(track.target.gameObject)??0;state.TargetKind=2;}
                var blob=track.target.GetComponent<dataBlob>();
                if(blob!=null) {state.TargetId=EnsureId(blob);state.TargetKind=3;}
            }
            if(bosses.bossHealth.gameObject.activeSelf && bosses.bossHealth.health!=null)
            {
                state.BossId=EnemyReplicator.Instance?.GetObjectId(bosses.bossHealth.health.gameObject)??0;
                state.BossName=bosses.bossHealth.bossName.text;
                state.BossHP=bosses.bossHealth.health.hp;state.BossMaxHP=bosses.bossHealth.health.hpMax;
            }
            foreach(var d in Dialogues()) state.Dialogues.Add(new StoryDialogueState
            {
                Active=d!=null && d.gameObject.activeSelf,Writing=d!=null && d.writing,Text=d!=null && d.textBox!=null?d.textBox.text:"",
                LowerText=d is dialogueTimeless?((dialogueTimeless)d).lowerTextBox.text:""
            });
            foreach(var blob in UnityEngine.Object.FindObjectsOfType<dataBlob>(true))
            {
                int id=EnsureId(blob);if(collected.Contains(id)) continue;
                string prefab=Prefab(blob);
                if(prefab=="") continue;
                var rb=blob.GetComponent<Rigidbody2D>();
                state.Pieces.Add(new DataPieceState {Id=id,PlanetId=blob.planet!=null?PlanetReplicator.Instance?.GetObjectId(blob.planet.gameObject)??0:0,
                    Prefab=prefab,X=blob.transform.position.x,Y=blob.transform.position.y,VX=rb.velocity.x,VY=rb.velocity.y,Rotation=rb.rotation,
                    ScaleX=blob.transform.lossyScale.x,ScaleY=blob.transform.lossyScale.y,Active=blob.gameObject.activeInHierarchy,Final=blob.isFinal,Chasing=blob.chasing});
            }
            foreach(var blob in new List<dataBlob>(ids.Keys)) if(blob==null) {hostPieces.Remove(ids[blob]);ids.Remove(blob);}
            return state;
        }
        private void Apply(ProgressState state)
        {
            // Require every pickup asset before replacing any local pickups.
            foreach(var p in state.Pieces) if(!prefabs.ContainsKey(p.Prefab)) return;
            applying=true;
            needsApply=false;
            try
            {
                int previousData=player.data;
                story.StopAllCoroutines();
                story.transmissionIndex=state.Transmission;story.researchReward=state.ResearchReward;story.bossIndex=state.BossIndex;
                story.fragmentHunting=state.FragmentHunting;story.plantPlanetSpawned=state.PlantPlanetSpawned;story.wormSpawned=state.WormSpawned;
                player.data=state.Data;player.dataMax=state.DataMax;player.hasFinalData=state.HasFinalData;
                SetFinalEffects(state.HasFinalData);
                if(lastReward>0 && state.ResearchReward>lastReward)
                    for(int reward=lastReward;reward<state.ResearchReward;reward+=2) player.scraps+=reward;
                lastReward=state.ResearchReward;
                bosses.plantPlanetSpawned=state.PlantPlanetSpawned;bosses.plantSummoned=state.PlantSummoned;bosses.wormSummoned=state.WormSummoned;
                var missions=(List<bool>)checklist.GetValue(story);
                for(int i=0;i<missions.Count;i++) missions[i]=(state.Checklist&(1<<i))!=0;
                story.waveManager.wave=state.Wave;story.waveManager.waveDuration=state.WaveDuration;story.waveManager.secondsUntilNext=state.SecondsUntilNext;
                story.terminalBox.SetActive(state.TerminalActive);story.transmitButton.SetActive(state.TransmitActive);story.dataBar.SetActive(state.DataBarActive);
                story.terminalText.text=state.TerminalText;story.timeManager.sloMo=state.SloMo;
                var ds=Dialogues();
                for(int i=0;i<ds.Count && i<state.Dialogues.Count;i++)
                {
                    var d=ds[i];if(d==null) continue;var v=state.Dialogues[i];
                    d.StopAllCoroutines();d.gameObject.SetActive(v.Active);d.message=v.Text;d.writing=v.Writing;
                    if(d.textBox!=null) d.textBox.text=v.Text;
                    if(d is dialogueTimeless) ((dialogueTimeless)d).lowerTextBox.text=v.LowerText;
                }
                if(previousData<state.Data) audioManager.Instance?.Play("DataCollection");
                if(state.Warped && state.SingularityEnabled && !wasWarped) {SetupWarp(state);wasWarped=true;}
                if(!state.Warped) wasWarped=false;
                if(state.Warped && warper!=null)
                {
                    warper.blackHoleScreen.SetActive(state.HoleScreen);warper.barsAndStuff.SetActive(state.Bars);
                    warper.compass.SetActive(state.Compass);warper.parallax.SetActive(state.Parallax);
                    warper.cameraMove.wantedSize=state.CameraSize;warper.cameraMove.maxZoom=state.CameraMaxZoom;
                    warper.globalLight.intensity=state.LightIntensity;
                    warper.darkness.color=new Color(1,1,1,state.DarknessAlpha);
                    warper.camera.backgroundColor=new Color(state.BackgroundR,state.BackgroundG,state.BackgroundB);
                    if(singularity!=null)
                    {
                        singularity.inGameStuff.SetActive(state.InGameStuff);singularity.enabled=state.SingularityEnabled;
                        singularity.flow=new Vector2(state.FlowX,state.FlowY);singularity.flowSpeed=state.FlowSpeed;singularity.playerStayForce=state.StayForce;
                    }
                    endingPresentation.Apply(state.Effects,warper,singularity);
                }
                if(singularity!=null) {singularity.badEndingScreen.SetActive(state.BadEndingScreen);singularity.finalQuitButton.SetActive(state.FinalQuit);}
                ApplyPieces(state);ApplyReferences(state);
            }
            finally {applying=false;}
        }
        private void ApplyPieces(ProgressState state)
        {
            var keep=new HashSet<int>();
            foreach(var p in state.Pieces)
            {
                keep.Add(p.Id);
                if(!pieces.TryGetValue(p.Id,out Piece piece) || piece.Blob==null)
                {
                    if(staging==null) {staging=new GameObject("OrbitUs_DataStaging");staging.SetActive(false);}
                    var obj=UnityEngine.Object.Instantiate(prefabs[p.Prefab],staging.transform);obj.SetActive(false);
                    var blob=obj.GetComponent<dataBlob>();blob.rb=obj.GetComponent<Rigidbody2D>();blob.rb.bodyType=RigidbodyType2D.Kinematic;
                    obj.AddComponent<ProgressDataMarker>();
                    piece=new Piece {Blob=blob};pieces[p.Id]=piece;
                    obj.transform.SetParent(null,true);
                }
                piece.State=p;var b=piece.Blob;b.isFinal=p.Final;b.chasing=p.Chasing;
                b.transform.position=new Vector3(p.X,p.Y,0);b.transform.localScale=new Vector3(p.ScaleX,p.ScaleY,1);
                b.rb.rotation=p.Rotation;b.rb.velocity=new Vector2(p.VX,p.VY);
                var parent=PlanetReplicator.Instance?.ResolveObject(p.PlanetId);
                piece.HasParent=parent!=null;
                piece.Offset=parent!=null?new Vector2(p.X,p.Y)-(Vector2)parent.transform.position:Vector2.zero;
                b.gameObject.SetActive(p.Active);
            }
            foreach(int id in new List<int>(pieces.Keys)) if(!keep.Contains(id))
            {var blob=pieces[id].Blob;if(blob!=null) {blob.gameObject.SetActive(false);UnityEngine.Object.Destroy(blob.gameObject);}pieces.Remove(id);}
            foreach(var blob in UnityEngine.Object.FindObjectsOfType<dataBlob>(true))
                if(blob.GetComponent<ProgressDataMarker>()==null) {blob.gameObject.SetActive(false);UnityEngine.Object.Destroy(blob.gameObject);}
        }
        private void ApplyReferences(ProgressState state)
        {
            if(story==null || player==null) return;
            var track=story.tracker;
            // Search objectives (data/radio) remain relative to the local pilot.
            int type=state.HasFinalData?2:state.Data>=state.DataMax?1:state.TargetType;
            if((int)targetType.GetValue(track)!=type) track.ChangeType(type);
            track.tooFarRange=state.TargetRange;
            GameObject target=state.TargetKind==1?PlanetReplicator.Instance?.ResolveObject(state.TargetId):state.TargetKind==2?EnemyReplicator.Instance?.ResolveObject(state.TargetId):null;
            if(type==2 && target!=null && track.target!=target.transform) track.ChangeTarget(target.transform);
            var boss=EnemyReplicator.Instance?.ResolveObject(state.BossId);
            if(bosses!=null && bosses.bossHealth!=null)
            {
                var bar=bosses.bossHealth;
                // Capture the authored full size while the UI is still inactive.
                var presentation=BossHealthBarPresentation.Prepare(bar);
                bar.gameObject.SetActive(state.BossMaxHP>0 && state.BossHP>0);
                bar.health=boss!=null?boss.GetComponent<health>():null;
                if(bar.gameObject.activeSelf)
                {
                    bar.SetName(state.BossName);bar.healthText.text=state.BossHP.ToString("0");
                    presentation.Draw(bar,state.BossHP,state.BossMaxHP);
                }
            }
        }
        private void SetupWarp(ProgressState state)
        {
            if(warper==null) return;
            warper.StopAllCoroutines();
            if(warper.relCamEf!=null){warper.relCamEf.ResetEffects();warper.relCamEf.enabled=false;}
            player.hp=player.hpMax;
            warper.warped=true;warper.blackHole.playerInside=true;warper.blackHoleScreen.SetActive(true);
            warper.barsAndStuff.SetActive(false);warper.compass.SetActive(false);warper.parallax.SetActive(false);
            warper.cameraMove.wantedSize=24;warper.cameraMove.maxZoom=28;
            warper.waveManager.EndWaves();warper.waveManager.enabled=false;
            player.transform.position=new Vector3(state.HostX,state.HostY,0);player.rb.velocity=Vector2.zero;
            if(singularity!=null) {singularity.enabled=true;singularity.spawning=false;}
        }
        private void SetFinalEffects(bool active)
        {
            var particles=AccessTools.Field(typeof(player),"keyParticles").GetValue(player) as GameObject;
            if(particles!=null) particles.SetActive(active);
        }
        public void Destroy()
        {
            connection.OnPacketReceived-=HandlePacket;
            if(IsClient)endingPresentation.Restore(warper);
            foreach(var p in pieces.Values) if(p.Blob!=null) {p.Blob.gameObject.SetActive(false);UnityEngine.Object.Destroy(p.Blob.gameObject);}
            pieces.Clear();pending.Clear();
            motions.Clear();motionReceived.Clear();positions.Clear();uploadCompletion.Clear();bossCredits.Clear();completedBosses.Clear();
            if(staging!=null) UnityEngine.Object.Destroy(staging);
            if(IsClient && story!=null) {story.timeManager.sloMo=false;foreach(var d in Dialogues()) if(d!=null) d.StopYapping();}
            initialized=false;if(Instance==this) Instance=null;
        }
    }
    public sealed class ProgressDataMarker : MonoBehaviour { }
}
