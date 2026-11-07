using System;
using System.Collections.Generic;
using System.IO;

namespace Orbit_Us
{
    public enum ProgressActionKind { Collect, OpenComms, CloseComms, Transmit, PlantBoss, EnterBlackHole, GoodEnding, BadEnding }

    public sealed class ProgressAction
    {
        public int PlayerId, RequestId, Argument, ExpectedTransmission;
        public ProgressActionKind Kind;
        public byte[] Serialize()
        {
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            { w.Write(PlayerId); w.Write(RequestId); w.Write((int)Kind); w.Write(Argument); w.Write(ExpectedTransmission); return s.ToArray(); }
        }
        public static ProgressAction Deserialize(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 20) throw new InvalidDataException("Invalid progression action.");
            using (var r = new BinaryReader(new MemoryStream(bytes)))
            {
                var a = new ProgressAction { PlayerId = r.ReadInt32(), RequestId = r.ReadInt32(), Kind = (ProgressActionKind)r.ReadInt32(), Argument = r.ReadInt32(), ExpectedTransmission = r.ReadInt32() };
                if (a.PlayerId < 0 || a.RequestId <= 0 || !Enum.IsDefined(typeof(ProgressActionKind), a.Kind) || a.ExpectedTransmission < 0 || a.ExpectedTransmission > 7)
                    throw new InvalidDataException("Invalid progression action values.");
                return a;
            }
        }
    }

    public sealed class DataPieceState
    {
        public int Id, PlanetId;
        public string Prefab = "";
        public float X, Y, VX, VY, Rotation, ScaleX = 1, ScaleY = 1;
        public bool Active, Final, Chasing;
    }
    public sealed class StoryDialogueState
    {
        public bool Active, Writing;
        public string Text = "", LowerText = "";
    }
    public sealed class ProgressState
    {
        public int Transmission, ResearchReward, BossIndex, Checklist, Data, DataMax;
        public int Wave, WaveDuration, SecondsUntilNext, TargetKind, TargetId, TargetType, BossId, Ending;
        public float TargetRange, BossHP, BossMaxHP, HostX, HostY;
        public bool FragmentHunting, PlantPlanetSpawned, WormSpawned, PlantSummoned, WormSummoned, HasFinalData;
        public bool TerminalActive, TransmitActive, DataBarActive, SloMo, Warped, BadEndingScreen, FinalQuit;
        public bool HoleScreen, Bars, Compass, Parallax, InGameStuff, SingularityEnabled;
        public float CameraSize=24, CameraMaxZoom=40, LightIntensity=1, DarknessAlpha, FlowX, FlowY, FlowSpeed, StayForce, BackgroundR, BackgroundG, BackgroundB;
        // Song is retained in the wire format for compatibility; music stays local.
        public string TerminalText = "", Song = "", BossName = "";
        public EndingEffectsState Effects=new EndingEffectsState();
        public List<StoryDialogueState> Dialogues = new List<StoryDialogueState>();
        public List<DataPieceState> Pieces = new List<DataPieceState>();
    }
    public sealed class DarknessPose
    {
        public bool Active;
        public float X=1,Y=1,Z=1;
    }
    public sealed class EndingEffectsState
    {
        public int Phase,AmplitudeSteps,FrequencySteps,ParticleCount;
        public bool Spawning,Exotics,Controls=true,RelativisticCamera=true,CameraAtBlackHole,BlackHoleActive=true;
        public float BloomIntensity,BloomThreshold,LensDistortion;
        public List<DarknessPose> Darkness=new List<DarknessPose>();
    }

}
