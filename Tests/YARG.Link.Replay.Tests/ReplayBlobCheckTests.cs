using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Keys;
using YARG.Core.Engine.Vocals;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.IO;
using YARG.Core.Replays;
using YARG.Integration.Link;

namespace YARG.Link.Replay.Tests
{
    public class ReplayBlobCheckTests
    {
        private static readonly int Version = ReplayIO.REPLAY_VERSIONS.CURRENT;

        private static readonly float[] Stars = { 0.21f, 0.46f, 0.77f, 1.85f, 3.08f, 4.29f };
        private static readonly float[] SoloStars = { 0.21f, 0.46f, 0.77f, 1.85f, 3.08f, 4.29f };

        private static ReplayFrame Frame(GameMode mode, string name, int inputs)
        {
            var preset = EnginePreset.Default;
            var profile = new YargProfile { Name = name, GameMode = mode };
            (BaseEngineParameters, BaseStats) parts = mode switch
            {
                GameMode.FiveFretGuitar => (preset.FiveFretGuitar.Create(Stars, SoloStars, false), new GuitarStats()),
                GameMode.FourLaneDrums  => (preset.Drums.Create(Stars, SoloStars, DrumsEngineParameters.DrumMode.ProFourLane), new DrumsStats()),
                GameMode.Vocals         => (preset.Vocals.Create(Stars, SoloStars, Difficulty.Expert, 60, false), new VocalsStats()),
                GameMode.ProKeys        => (preset.ProKeys.Create(Stars, SoloStars, false), new KeysStats()),
                _                       => throw new ArgumentException(mode.ToString()),
            };
            var input = Enumerable.Range(0, inputs).Select(i => new GameInput(i * 0.1, i % 5, i)).ToArray();
            return new ReplayFrame(profile, parts.Item1, parts.Item2, input);
        }

        private static byte[] Blob(bool presets, params ReplayFrame[] frames)
        {
            var colors = new Dictionary<Guid, ColorProfile>();
            var cameras = new Dictionary<Guid, CameraPreset>();
            var meters = new Dictionary<Guid, RockMeterPreset>();
            if (presets)
            {
                colors[Guid.NewGuid()] = new ColorProfile("Mine");
                cameras[Guid.NewGuid()] = new CameraPreset("Low");
                meters[Guid.NewGuid()] = new RockMeterPreset("Easy");
            }
            return new ReplayData(colors, cameras, meters, false, frames, Array.Empty<double>()).Serialize().ToArray();
        }

        private static ReplayData Parse(byte[] bytes)
        {
            using var array = FixedArray.Read(new MemoryStream(bytes), bytes.Length);
            return new ReplayData(array.ToValueStream(), Version, new ReplayReadOptions());
        }

        private static void AssertUnsafe(byte[] bytes, string expected = null)
        {
            bool safe = ReplayBlobCheck.IsSafe(bytes, Version, out string problem);
            Assert.That(safe, Is.False, "should have been turned away");
            Assert.That(problem, Is.Not.Null.And.Not.Empty);
            if (expected != null) Assert.That(problem, Does.Contain(expected));
        }

        private static byte[] Splice(byte[] bytes, int at, int remove, params byte[] insert) =>
            bytes.Take(at).Concat(insert).Concat(bytes.Skip(at + remove)).ToArray();

        private static int IndexOf(byte[] bytes, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= bytes.Length; i++)
            {
                if (bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
            }
            throw new InvalidOperationException("pattern not found");
        }

        [Test]
        public void KnowsTheCurrentFormat()
        {
            // If this fails, YARG.Core's replay format moved on: check its readers against ReplayBlobCheck
            Assert.That(ReplayBlobCheck.KNOWN_REPLAY_VERSION, Is.EqualTo(Version));
        }

        [Test]
        public void ValidReplayDataPasses([Values] bool presets)
        {
            var frames = new[]
            {
                Frame(GameMode.FiveFretGuitar, "Ann", 3),
                Frame(GameMode.FourLaneDrums, "Bo", 0),
                Frame(GameMode.Vocals, "Cy ♪", 1),
                Frame(GameMode.ProKeys, "Di", 20),
            };
            var bytes = Blob(presets, frames);

            Assert.That(ReplayBlobCheck.IsSafe(bytes, Version, out string problem), Is.True, problem);

            var parsed = Parse(bytes);
            Assert.That(parsed.Frames.Select(f => f.Profile.Name), Is.EqualTo(frames.Select(f => f.Profile.Name)));
            Assert.That(parsed.Frames.Select(f => f.Inputs.Length), Is.EqualTo(new[] { 3, 0, 1, 20 }));
        }

        [Test]
        public void EmptyReplayDataPasses()
        {
            Assert.That(ReplayBlobCheck.IsSafe(Blob(false), Version, out string problem), Is.True, problem);
        }

        [Test]
        public void EveryTruncationIsTurnedAway()
        {
            var bytes = Blob(true, Frame(GameMode.FiveFretGuitar, "Ann", 2), Frame(GameMode.FourLaneDrums, "Bo", 1));
            for (int length = 0; length < bytes.Length; length++)
            {
                Assert.That(ReplayBlobCheck.IsSafe(bytes.AsSpan(0, length), Version, out _), Is.False, $"length {length}");
            }
        }

        [Test]
        public void TrailingDataIsTurnedAway()
        {
            var bytes = Blob(false, Frame(GameMode.FiveFretGuitar, "Ann", 1));
            AssertUnsafe(bytes.Concat(new byte[] { 0 }).ToArray(), "left over");
        }

        [Test]
        public void StringLongerThanTheDataIsTurnedAway()
        {
            var bytes = Blob(false, Frame(GameMode.FiveFretGuitar, "Ann", 1));
            int at = IndexOf(bytes, new byte[] { 3, (byte) 'A', (byte) 'n', (byte) 'n' });

            // int.MaxValue as a 7-bit encoded length
            AssertUnsafe(Splice(bytes, at, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x07), "bad name length");

            // One byte more than is left after the (two-byte) length
            int left = bytes.Length - at - 1;
            int length = left + 1;
            AssertUnsafe(Splice(bytes, at, 1, (byte) (length & 0x7F | 0x80), (byte) (length >> 7)),
                $"bad name length {length} with {left} bytes left");
        }

        [Test]
        public void NegativeStringLengthIsTurnedAway()
        {
            var bytes = Blob(false, Frame(GameMode.FiveFretGuitar, "Ann", 1));
            int at = IndexOf(bytes, new byte[] { 3, (byte) 'A', (byte) 'n', (byte) 'n' });
            AssertUnsafe(Splice(bytes, at, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F), "bad name length -1");
        }

        [Test]
        public void OversizeCountsAreTurnedAway()
        {
            var bytes = Blob(false, Frame(GameMode.FiveFretGuitar, "Ann", 2));
            byte[] Patched(int at, int value)
            {
                var copy = (byte[]) bytes.Clone();
                BitConverter.GetBytes(value).CopyTo(copy, at);
                return copy;
            }

            // Presets version, then three empty dictionaries and the no-fail flag: frames start at 17
            AssertUnsafe(Patched(4, int.MaxValue), "presets");
            AssertUnsafe(Patched(4, -1), "presets");
            AssertUnsafe(Patched(17, int.MaxValue), "frames");
            AssertUnsafe(Patched(17, -5), "frames");

            // At the end: the frame's input count, its two inputs, then the frame time count
            int inputs = bytes.Length - 4 - 2 * 16 - 4;
            AssertUnsafe(Patched(inputs, 1 << 26), "inputs");
            AssertUnsafe(Patched(bytes.Length - 4, int.MaxValue), "frame times");
        }

        [Test]
        public void TooManyFramesAreTurnedAway()
        {
            var frames = Enumerable.Range(0, ReplayBlobCheck.MAX_FRAMES + 1)
                .Select(i => Frame(GameMode.FiveFretGuitar, $"P{i}", 0)).ToArray();
            AssertUnsafe(Blob(false, frames), "players");
        }

        [Test]
        public void OtherVersionsAreTurnedAway()
        {
            var bytes = Blob(false, Frame(GameMode.FiveFretGuitar, "Ann", 1));
            Assert.That(ReplayBlobCheck.IsSafe(bytes, Version - 1, out _), Is.False);

            // The profile's own version is the first thing in the frame after its tag
            int at = IndexOf(bytes, new byte[] { (byte) 'R', (byte) 'P', (byte) 'F', (byte) 'M' }) + 4;
            var copy = (byte[]) bytes.Clone();
            BitConverter.GetBytes(ReplayBlobCheck.KNOWN_PROFILE_VERSION + 1).CopyTo(copy, at);
            AssertUnsafe(copy, "profile version");
        }

        [Test]
        public void CorruptedDataEitherFailsTheCheckOrParses()
        {
            // Whatever passes the check, YARG.Core must be able to read without going out of bounds:
            // so it either parses or throws a managed exception, and never claims more than it was given
            var bytes = Blob(true, Frame(GameMode.FiveFretGuitar, "Ann", 4), Frame(GameMode.Vocals, "Bo", 2));
            var random = new Random(1234);
            int passed = 0;
            for (int i = 0; i < 5000; i++)
            {
                var copy = (byte[]) bytes.Clone();
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    copy[random.Next(copy.Length)] = (byte) random.Next(256);
                }

                if (!ReplayBlobCheck.IsSafe(copy, Version, out _)) continue;
                passed++;
                try
                {
                    var parsed = Parse(copy);
                    Assert.That(parsed.Frames.Sum(f => f.Inputs.Length) * 16, Is.LessThan(copy.Length));
                }
                catch (Exception e) when (e is not AssertionException)
                {
                    // Bad JSON in a preset, an unknown enum value, ...: managed and caught by the caller
                }
            }
            TestContext.Out.WriteLine($"{passed} of 5000 corrupted copies passed the check");
        }
    }
}
