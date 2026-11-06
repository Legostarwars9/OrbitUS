using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace Orbit_Us
{
    public static class ProgressSnapshotCodec
    {
        private const int Version = 1, HeaderBytes = 20, ChunkBytes = 1080;
        public const int MaxSnapshotBytes = 2 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        public static List<byte[]> Encode(ProgressState state, int snapshotId)
        {
            if (snapshotId <= 0 || state == null) throw new InvalidDataException("Invalid progress snapshot.");
            byte[] payload;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteState(writer, state);
                if (stream.Length > MaxSnapshotBytes) throw new InvalidDataException("Progress snapshot too large.");
                payload = stream.ToArray();
            }
            int total = (payload.Length + ChunkBytes - 1) / ChunkBytes;
            var result = new List<byte[]>(total);
            for (int index = 0; index < total; index++)
            {
                int length = Math.Min(ChunkBytes, payload.Length - index * ChunkBytes);
                using (var stream = new MemoryStream(HeaderBytes + length))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Version);
                    writer.Write(snapshotId);
                    writer.Write(index);
                    writer.Write(total);
                    writer.Write(payload.Length);
                    writer.Write(payload, index * ChunkBytes, length);
                    result.Add(stream.ToArray());
                }
            }
            return result;
        }

        public static bool TryReadChunkHeader(byte[] data, out int snapshotId, out int chunkIndex, out int chunkCount)
        {
            snapshotId = chunkIndex = chunkCount = 0;
            if (data == null || data.Length <= HeaderBytes || data.Length > HeaderBytes + ChunkBytes) return false;
            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                if (reader.ReadInt32() != Version) return false;
                int id = reader.ReadInt32(), index = reader.ReadInt32(), total = reader.ReadInt32(), length = reader.ReadInt32();
                if (id <= 0 || length < 4 || length > MaxSnapshotBytes ||
                    total != (length + ChunkBytes - 1) / ChunkBytes || index < 0 || index >= total ||
                    data.Length != HeaderBytes + Math.Min(ChunkBytes, length - index * ChunkBytes)) return false;
                snapshotId = id; chunkIndex = index; chunkCount = total;
                return true;
            }
        }


        private static void String(BinaryWriter w, string text)
        {
            byte[] bytes = Utf8.GetBytes(text ?? "");
            if (bytes.Length > 8192) throw new InvalidDataException("Progress text too long.");
            w.Write(bytes.Length); w.Write(bytes);
        }
        private static string String(BinaryReader r)
        {
            int count = Count(r, 8192); byte[] bytes = r.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return Utf8.GetString(bytes);
        }
        private static int Count(BinaryReader r, int max)
        { int n = r.ReadInt32(); if (n < 0 || n > max) throw new InvalidDataException("Invalid progress count."); return n; }
        private static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
        private static void Validate(ProgressState s)
        {
            if (s.Transmission < 0 || s.Transmission > 7 || s.Data < 0 || s.DataMax < 1 || s.DataMax > 100000 || s.Data > s.DataMax ||
                s.ResearchReward < 0 || s.ResearchReward > 100000 || s.Ending < 0 || s.Ending > 2 || s.TargetKind < 0 || s.TargetKind > 3 ||
                s.TargetType < 0 || s.TargetType > 2 || s.Dialogues.Count > 40 || s.Pieces.Count > 10000 ||
                !Finite(s.TargetRange) || !Finite(s.BossHP) || !Finite(s.BossMaxHP) || !Finite(s.HostX) || !Finite(s.HostY))
                throw new InvalidDataException("Invalid story state.");
            foreach(float v in new[]{s.CameraSize,s.CameraMaxZoom,s.LightIntensity,s.DarknessAlpha,s.FlowX,s.FlowY,s.FlowSpeed,s.StayForce,s.BackgroundR,s.BackgroundG,s.BackgroundB})
                if(!Finite(v)) throw new InvalidDataException("Invalid story presentation.");
            var ids = new HashSet<int>();
            foreach (var p in s.Pieces)
                if (p.Id <= 0 || !ids.Add(p.Id) || p.PlanetId < 0 || p.Prefab.Length > 256 || !Finite(p.X) || !Finite(p.Y) ||
                    !Finite(p.VX) || !Finite(p.VY) || !Finite(p.ScaleX) || !Finite(p.ScaleY) || !Finite(p.Rotation))
                    throw new InvalidDataException("Invalid data piece.");
        }
        private static void WriteState(BinaryWriter w, ProgressState s)
        {
            Validate(s);
            foreach (int v in new[] { s.Transmission,s.ResearchReward,s.BossIndex,s.Checklist,s.Data,s.DataMax,s.Wave,s.WaveDuration,s.SecondsUntilNext,s.TargetKind,s.TargetId,s.TargetType,s.BossId,s.Ending }) w.Write(v);
            foreach (float v in new[] { s.TargetRange,s.BossHP,s.BossMaxHP,s.HostX,s.HostY }) w.Write(v);
            foreach (bool v in new[] {s.FragmentHunting,s.PlantPlanetSpawned,s.WormSpawned,s.PlantSummoned,s.WormSummoned,s.HasFinalData,s.TerminalActive,s.TransmitActive,s.DataBarActive,s.SloMo,s.Warped,s.BadEndingScreen,s.FinalQuit}) w.Write(v);
            foreach(bool v in new[]{s.HoleScreen,s.Bars,s.Compass,s.Parallax,s.InGameStuff,s.SingularityEnabled}) w.Write(v);
            foreach(float v in new[]{s.CameraSize,s.CameraMaxZoom,s.LightIntensity,s.DarknessAlpha,s.FlowX,s.FlowY,s.FlowSpeed,s.StayForce,s.BackgroundR,s.BackgroundG,s.BackgroundB}) w.Write(v);
            String(w,s.TerminalText); String(w,s.Song); String(w,s.BossName);
            w.Write(s.Dialogues.Count);
            foreach(var d in s.Dialogues) { w.Write(d.Active); w.Write(d.Writing); String(w,d.Text); String(w,d.LowerText); }
            w.Write(s.Pieces.Count);
            foreach(var p in s.Pieces)
            {
                w.Write(p.Id); w.Write(p.PlanetId); String(w,p.Prefab);
                w.Write(p.X); w.Write(p.Y); w.Write(p.VX); w.Write(p.VY); w.Write(p.Rotation); w.Write(p.ScaleX); w.Write(p.ScaleY);
                w.Write(p.Active); w.Write(p.Final); w.Write(p.Chasing);
            }
        }
        private static ProgressState ReadState(BinaryReader r)
        {
            var s = new ProgressState
            {
                Transmission=r.ReadInt32(),ResearchReward=r.ReadInt32(),BossIndex=r.ReadInt32(),Checklist=r.ReadInt32(),Data=r.ReadInt32(),DataMax=r.ReadInt32(),
                Wave=r.ReadInt32(),WaveDuration=r.ReadInt32(),SecondsUntilNext=r.ReadInt32(),TargetKind=r.ReadInt32(),TargetId=r.ReadInt32(),TargetType=r.ReadInt32(),BossId=r.ReadInt32(),Ending=r.ReadInt32(),
                TargetRange=r.ReadSingle(),BossHP=r.ReadSingle(),BossMaxHP=r.ReadSingle(),HostX=r.ReadSingle(),HostY=r.ReadSingle(),
                FragmentHunting=r.ReadBoolean(),PlantPlanetSpawned=r.ReadBoolean(),WormSpawned=r.ReadBoolean(),PlantSummoned=r.ReadBoolean(),WormSummoned=r.ReadBoolean(),HasFinalData=r.ReadBoolean(),
                TerminalActive=r.ReadBoolean(),TransmitActive=r.ReadBoolean(),DataBarActive=r.ReadBoolean(),SloMo=r.ReadBoolean(),Warped=r.ReadBoolean(),BadEndingScreen=r.ReadBoolean(),FinalQuit=r.ReadBoolean(),
                HoleScreen=r.ReadBoolean(),Bars=r.ReadBoolean(),Compass=r.ReadBoolean(),Parallax=r.ReadBoolean(),InGameStuff=r.ReadBoolean(),SingularityEnabled=r.ReadBoolean(),
                CameraSize=r.ReadSingle(),CameraMaxZoom=r.ReadSingle(),LightIntensity=r.ReadSingle(),DarknessAlpha=r.ReadSingle(),FlowX=r.ReadSingle(),FlowY=r.ReadSingle(),FlowSpeed=r.ReadSingle(),StayForce=r.ReadSingle(),BackgroundR=r.ReadSingle(),BackgroundG=r.ReadSingle(),BackgroundB=r.ReadSingle(),
                TerminalText=String(r),Song=String(r),BossName=String(r)
            };
            int dialogues=Count(r,40);
            for(int i=0;i<dialogues;i++) s.Dialogues.Add(new StoryDialogueState {Active=r.ReadBoolean(),Writing=r.ReadBoolean(),Text=String(r),LowerText=String(r)});
            int pieces=Count(r,10000);
            for(int i=0;i<pieces;i++) s.Pieces.Add(new DataPieceState
            {
                Id=r.ReadInt32(),PlanetId=r.ReadInt32(),Prefab=String(r),X=r.ReadSingle(),Y=r.ReadSingle(),VX=r.ReadSingle(),VY=r.ReadSingle(),Rotation=r.ReadSingle(),ScaleX=r.ReadSingle(),ScaleY=r.ReadSingle(),Active=r.ReadBoolean(),Final=r.ReadBoolean(),Chasing=r.ReadBoolean()
            });
            Validate(s); return s;
        }

        public sealed class Assembler
        {
            private sealed class Pending
            {
                public int Length, Received, MissingCursor;
                public byte[][] Chunks;
            }

            private readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
            public int LastSnapshotId { get; private set; }
            public byte[][] CompletedChunks { get; private set; }

            public int[] GetMissingChunks(int maxCount, out int snapshotId)
            {
                snapshotId = 0;
                if (maxCount <= 0) return new int[0];
                foreach (int key in pending.Keys) snapshotId = Math.Max(snapshotId, key);
                if (snapshotId == 0) return new int[0];
                Pending snapshot = pending[snapshotId];
                var missing = new List<int>();
                for (int checkedCount = 0; checkedCount < snapshot.Chunks.Length && missing.Count < maxCount; checkedCount++)
                {
                    int index = snapshot.MissingCursor;
                    snapshot.MissingCursor = (index + 1) % snapshot.Chunks.Length;
                    if (snapshot.Chunks[index] == null) missing.Add(index);
                }
                return missing.ToArray();
            }

            public bool TryAdd(byte[] data, out ProgressState state)
            {
                state = null;
                if (data == null || data.Length <= HeaderBytes || data.Length > HeaderBytes + ChunkBytes) return false;
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    if (reader.ReadInt32() != Version) return false;
                    int id = reader.ReadInt32(), index = reader.ReadInt32(), total = reader.ReadInt32(), length = reader.ReadInt32();
                    if (id <= LastSnapshotId || length < 4 || length > MaxSnapshotBytes ||
                        total != (length + ChunkBytes - 1) / ChunkBytes || index < 0 || index >= total ||
                        data.Length != HeaderBytes + Math.Min(ChunkBytes, length - index * ChunkBytes)) return false;

                    Pending snapshot;
                    if (!pending.TryGetValue(id, out snapshot))
                    {
                        if (pending.Count >= 3)
                        {
                            int oldest = int.MaxValue;
                            foreach (int key in pending.Keys) oldest = Math.Min(oldest, key);
                            if (id < oldest) return false;
                            pending.Remove(oldest);
                        }
                        snapshot = new Pending { Length = length, Chunks = new byte[total][] };
                        pending.Add(id, snapshot);
                    }
                    if (snapshot.Length != length || snapshot.Chunks.Length != total || snapshot.Chunks[index] != null) return false;
                    snapshot.Chunks[index] = data;
                    if (++snapshot.Received != total) return false;
                    pending.Remove(id);
                    byte[] payload = new byte[length];
                    for (int i = 0; i < total; i++)
                        Buffer.BlockCopy(snapshot.Chunks[i], HeaderBytes, payload, i * ChunkBytes, snapshot.Chunks[i].Length - HeaderBytes);
                    try
                    {
                        ProgressState result;
                        using (var input = new MemoryStream(payload))
                        using (var bodyReader = new BinaryReader(input))
                        {
                            result = ReadState(bodyReader);
                            if (input.Position != input.Length) throw new InvalidDataException("Trailing progress data.");
                        }
                        LastSnapshotId = id;
                        CompletedChunks = snapshot.Chunks;
                        foreach (int key in new List<int>(pending.Keys)) if (key <= id) pending.Remove(key);
                        state = result;
                        return true;
                    }
                    catch (InvalidDataException) { return false; }
                    catch (IOException) { return false; }
                    catch (ArgumentException) { return false; }
                }
            }
        }
    }
}
