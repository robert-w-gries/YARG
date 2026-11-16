using System;
using System.Buffers.Binary;
using YARG.Core;
using YARG.Core.Replays;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Checks replay data from another member before YARG.Core parses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// YARG.Core's replay reader trusts the data it reads. <c>FixedArrayStream.ReadString</c> doesn't check a
    /// string's length against the buffer, so a crafted length reads past the end of it (a crash, or other
    /// memory shown as a player name), and array counts are allocated as given. A friend's <c>setup</c>
    /// carries replay data, so until the reader is fixed in YARG.Core, everything from the link goes through
    /// this first.
    /// </para>
    /// <para>
    /// It walks the same layout as the YARG.Core readers (<c>ReplayData</c>, <c>ReplayFrame</c>,
    /// <c>YargProfile</c>, the <c>*EngineParameters</c> and <c>*Stats</c> classes) and checks every length
    /// and count against the bytes that are left. It only knows one replay version and one profile version,
    /// the ones current when it was written, and turns anything else away: if YARG.Core's format moves on,
    /// friends' highways stop appearing (and <see cref="LinkController"/> logs an error when our own setup
    /// fails this check) rather than unchecked data reaching the reader. When that happens, compare the
    /// readers with the layout below, update it and bump the versions. Tests/YARG.Link.Replay.Tests checks it
    /// against YARG.Core's own serializer.
    /// </para>
    /// </remarks>
    public static class ReplayBlobCheck
    {
        /// <summary>The replay format version this check knows (<c>ReplayIO.REPLAY_VERSIONS.CURRENT</c>).</summary>
        public const int KNOWN_REPLAY_VERSION = 19;

        /// <summary>The profile format version this check knows (<c>YargProfile.PROFILE_VERSION</c>).</summary>
        public const int KNOWN_PROFILE_VERSION = 9;

        /// <summary>Players one replay may carry.</summary>
        public const int MAX_FRAMES = 16;

        /// <summary>The longest preset (as JSON) and profile name, in bytes.</summary>
        public const int MAX_PRESET_BYTES = 256 * 1024;
        public const int MAX_NAME_BYTES   = 1024;

        // No frame is smaller than this (tag, profile, engine parameters and stats add up to well over it),
        // so a frame count can't claim more frames than the bytes could hold
        private const int MIN_FRAME_BYTES = 256;

        private const int GUID_BYTES  = 16;
        private const int INPUT_BYTES = 16; // double time, int action, int value

        /// <summary>
        /// Whether YARG.Core can safely parse <paramref name="data"/> as <c>ReplayData</c> of
        /// <paramref name="version"/>.
        /// </summary>
        public static bool IsSafe(ReadOnlySpan<byte> data, int version, out string problem)
        {
            if (version != KNOWN_REPLAY_VERSION)
            {
                problem = $"replay version {version} isn't the one this check knows ({KNOWN_REPLAY_VERSION})";
                return false;
            }

            if (ReplayIO.REPLAY_VERSIONS.CURRENT != KNOWN_REPLAY_VERSION)
            {
                problem = $"YARG.Core writes replay version {ReplayIO.REPLAY_VERSIONS.CURRENT}, but this check only " +
                    $"knows version {KNOWN_REPLAY_VERSION}";
                return false;
            }

            try
            {
                var reader = new Reader(data);
                WalkReplayData(ref reader);
                problem = null;
                return true;
            }
            catch (FormatException e)
            {
                problem = e.Message;
                return false;
            }
        }

        private static void WalkReplayData(ref Reader reader)
        {
            reader.Skip(4); // presets version

            // Color profiles, camera presets, rock meter presets: GUID and JSON each
            for (int dict = 0; dict < 3; dict++)
            {
                int presets = reader.Count(GUID_BYTES + 1, "presets");
                for (int i = 0; i < presets; i++)
                {
                    reader.Skip(GUID_BYTES);
                    reader.String(MAX_PRESET_BYTES, "preset");
                }
            }

            reader.Skip(1); // no fail

            int frames = reader.Count(MIN_FRAME_BYTES, "frames");
            if (frames > MAX_FRAMES)
            {
                throw new FormatException($"{frames} players is more than {MAX_FRAMES}");
            }
            for (int i = 0; i < frames; i++)
            {
                WalkFrame(ref reader);
            }

            int frameTimes = reader.Count(8, "frame times");
            reader.Skip(frameTimes * 8);

            if (reader.Remaining != 0)
            {
                throw new FormatException($"{reader.Remaining} bytes left over");
            }
        }

        private static void WalkFrame(ref Reader reader)
        {
            // FourCC "RPFM", little-endian
            if (reader.Byte() != 'R' || reader.Byte() != 'P' || reader.Byte() != 'F' || reader.Byte() != 'M')
            {
                throw new FormatException("no frame tag");
            }

            var mode = WalkProfile(ref reader);
            WalkBaseEngineParameters(ref reader);

            switch (mode)
            {
                case GameMode.FiveFretGuitar:
                case GameMode.SixFretGuitar:
                    reader.Skip(8 * 3 + 1 * 4); // hopo/strum leniencies; infinite front end, anti-ghosting, solo taps, no SP overlap
                    WalkBaseStats(ref reader);
                    reader.Skip(4 * 3);         // overstrums, hopos strummed, ghost inputs
                    break;
                case GameMode.FourLaneDrums:
                case GameMode.FiveLaneDrums:
                case GameMode.EliteDrums:
                    reader.Skip(1 + 4 * 2 + 1); // mode, velocity threshold, situational velocity window, no SP overlap
                    WalkBaseStats(ref reader);
                    reader.Skip(4 * 6);         // overhits, ghosts, accents, dynamics bonus
                    break;
                case GameMode.Vocals:
                    reader.Skip(4 * 2 + 8 * 2 + 1 + 4); // pitch windows, phrase hit percent, fps, sing to activate, points per phrase
                    WalkBaseStats(ref reader);
                    reader.Skip(4 * 2 + 1);             // ticks hit/missed, carry note
                    break;
                case GameMode.ProKeys:
                    reader.Skip(8 * 2 + 1 + 1); // chord stagger, fat finger; no SP overlap, release keys hit notes
                    WalkBaseStats(ref reader);
                    reader.Skip(4 * 2);         // overhits, fat fingers ignored
                    break;
                default:
                    throw new FormatException($"unsupported game mode {(int) mode}");
            }

            int inputs = reader.Count(INPUT_BYTES, "inputs");
            reader.Skip(inputs * INPUT_BYTES);
        }

        private static GameMode WalkProfile(ref Reader reader)
        {
            int version = reader.Int32();
            if (version != KNOWN_PROFILE_VERSION)
            {
                throw new FormatException($"profile version {version} isn't the one this check knows ({KNOWN_PROFILE_VERSION})");
            }

            reader.String(MAX_NAME_BYTES, "name");
            reader.Skip(GUID_BYTES * 5); // engine, theme, color, camera, highway
            reader.Skip(1 + 1 + 8 + 1);  // instrument, difficulty, modifiers, harmony index
            reader.Skip(4 + 4 + 1 + 1);  // note speed, highway length, lefty flip, range
            reader.Skip(4);              // cymbal models and the old drum layout options
            reader.Skip(1);              // star power activation
            var mode = (GameMode) reader.Byte();
            reader.Skip(1);              // open lane display

            // Four-lane, pro and five-lane drum highway orderings, each a byte count and that many bytes
            for (int i = 0; i < 3; i++)
            {
                reader.Skip(reader.Byte());
            }

            reader.Skip(GUID_BYTES);     // rock meter preset
            return mode;
        }

        private static void WalkBaseEngineParameters(ref Reader reader)
        {
            // Hit window: max, min, front-to-back, dynamic, slope, scale, gamma, lane autohit, proximity protection
            reader.Skip(8 * 3 + 1 + 8 * 3 + 8 + 8);
            reader.Skip(4 + 8 + 8); // max multiplier, SP whammy buffer, sustain drop leniency

            int stars = reader.Count(4, "star thresholds");
            reader.Skip(stars * 4);
            int soloStars = reader.Count(4, "solo star thresholds");
            reader.Skip(soloStars * 4);

            reader.Skip(8 + 1); // song speed, lanes
        }

        private static void WalkBaseStats(ref Reader reader)
        {
            reader.Skip(4 * 6);         // committed, pending, note, sustain, multiplier, band bonus scores
            reader.Skip(4 * 4);         // combo, max combo, score multiplier, band multiplier
            reader.Skip(4 * 2);         // notes hit, total notes
            reader.Skip(4 * 2 + 8 + 4); // SP tick amount, total SP ticks, time in SP, SP whammy ticks
            reader.Skip(4 + 1);         // SP activations, SP active
            reader.Skip(4 * 2 + 4);     // SP phrases hit/total, revives
            reader.Skip(4 * 2 + 4);     // solo bonuses, SP score, average multiplier
        }

        /// <summary>Reads what the walk needs to know and skips the rest, never past the end.</summary>
        private ref struct Reader
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _position;

            public Reader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _position = 0;
            }

            public int Remaining => _data.Length - _position;

            public void Skip(int count)
            {
                if (count < 0 || count > Remaining)
                {
                    throw new FormatException($"truncated at byte {_position}");
                }
                _position += count;
            }

            public byte Byte()
            {
                Skip(1);
                return _data[_position - 1];
            }

            public int Int32()
            {
                Skip(4);
                return BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position - 4));
            }

            /// <summary>An element count, each element taking at least <paramref name="minElementBytes"/>.</summary>
            public int Count(int minElementBytes, string what)
            {
                int count = Int32();
                if (count < 0 || count > Remaining / minElementBytes)
                {
                    throw new FormatException($"{count} {what} can't fit in the {Remaining} bytes left");
                }
                return count;
            }

            /// <summary>A string as <c>FixedArrayStream.ReadString</c> reads it: a 7-bit encoded length, then UTF-8.</summary>
            public void String(int maxBytes, string what)
            {
                int length = SevenBitInt();
                if (length < 0 || length > Remaining || length > maxBytes)
                {
                    throw new FormatException($"bad {what} length {length} with {Remaining} bytes left");
                }
                Skip(length);
            }

            // The same as FixedArrayStream.Read7BitEncodedInt
            private int SevenBitInt()
            {
                uint result = 0;
                for (int shift = 0; shift < 4 * 7; shift += 7)
                {
                    byte b = Byte();
                    result |= (b & 0x7Fu) << shift;
                    if (b <= 0x7F) return (int) result;
                }

                byte last = Byte();
                if (last > 0b1111)
                {
                    throw new FormatException("bad 7-bit encoded length");
                }
                result |= (uint) last << (4 * 7);
                return (int) result;
            }
        }
    }
}
