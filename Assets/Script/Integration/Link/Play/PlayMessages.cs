using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link.Play
{
    /// <summary>
    /// The game data that games send each other about songs, inside the session's
    /// <see cref="GameDataMessage"/>. The session does not look inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each kind of game data is one class here, registered with its <see cref="GameDataMessage.Kind"/>. To add
    /// a message: add a class, register it in the static constructor, and check every value that comes from a
    /// friend in <see cref="PlayMessage.Check"/>. <see cref="Decode"/> calls <see cref="PlayMessage.Check"/>, so
    /// the handlers only get messages that passed it.
    /// </para>
    /// <para>
    /// Every message about a play of a song derives from <see cref="SongPlayMessage"/>, so it carries the song
    /// hash and the play ID. A handler must compare both with the play it is in (refer to
    /// <c>SyncedSong.IsFromPlay</c>). Otherwise, messages from a restarted song affect the new song.
    /// </para>
    /// <para>
    /// Like the session messages, there is no protocol version: all members play the same game version.
    /// </para>
    /// </remarks>
    public static class PlayProtocol
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            // Not CamelCasePropertyNamesContractResolver: that one also changes dictionary keys (member IDs)
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = false,
                },
            },
            NullValueHandling = NullValueHandling.Ignore,
        });

        private static readonly Dictionary<string, Type> TypesByKind = new();
        private static readonly Dictionary<Type, string> KindsByType = new();

        static PlayProtocol()
        {
            Register<SetupMessage>("setup");
            Register<InputsMessage>("inputs");
            Register<FailedMessage>("failed");
            Register<DeclinedMessage>("declined");
            Register<LeftMessage>("left");
            Register<PlayerFailedMessage>("player_failed");
            Register<RevivedMessage>("revived");
            Register<MeterMessage>("meter");
            Register<PauseMessage>("pause");
            Register<ResumeMessage>("resume");
            Register<EndSongMessage>("endsong");
            Register<DroppedMessage>("dropped");
        }

        private static void Register<T>(string kind) where T : PlayMessage
        {
            TypesByKind[kind] = typeof(T);
            KindsByType[typeof(T)] = kind;
        }

        public static string KindOf(PlayMessage message) => KindsByType[message.GetType()];

        public static JObject Encode(PlayMessage message) => JObject.FromObject(message, Serializer);

        /// <summary>
        /// Read game data from a friend, and check it (<see cref="PlayMessage.Check"/>).
        /// </summary>
        /// <returns>The message, or <c>null</c> for a kind this version does not know.</returns>
        /// <exception cref="FormatException">The data is not a usable message of this kind.</exception>
        public static PlayMessage Decode(string kind, JObject data)
        {
            if (kind == null || !TypesByKind.TryGetValue(kind, out var type))
            {
                return null;
            }
            if (data == null)
            {
                throw new FormatException($"{kind} has no data");
            }

            PlayMessage message;
            try
            {
                message = (PlayMessage) data.ToObject(type, Serializer);
            }
            catch (JsonException e)
            {
                throw new FormatException($"bad {kind}: {e.Message}", e);
            }
            catch (ArgumentException e)
            {
                throw new FormatException($"bad {kind}: {e.Message}", e);
            }

            if (message == null)
            {
                throw new FormatException($"{kind} has no data");
            }
            message.Check();
            return message;
        }

        /// <summary>Send a message to every other member.</summary>
        public static void Send(this LinkSession session, PlayMessage message) =>
            session.Broadcast(KindOf(message), Encode(message), message.Delivery);
    }

    /// <summary>Limits for the values in <see cref="PlayMessage"/>s, on top of <see cref="LinkLimits"/>.</summary>
    public static class PlayLimits
    {
        /// <summary>
        /// Input and song times further from zero than this, in seconds (a day), are nonsense, whoever sent them.
        /// </summary>
        public const double MAX_SONG_TIME = 24 * 60 * 60;

        public static bool IsSaneTime(double time) =>
            !double.IsNaN(time) && !double.IsInfinity(time) && Math.Abs(time) <= MAX_SONG_TIME;

        /// <summary>A fail meter value (0 to 1), or <c>null</c> for one that is not a number.</summary>
        public static float? SaneHappiness(float? value) =>
            value is { } v && !float.IsNaN(v) && !float.IsInfinity(v) ? Math.Clamp(v, 0f, 1f) : null;

        internal static void CheckId(string id, string what)
        {
            if (string.IsNullOrEmpty(id) || id.Length > LinkLimits.MAX_ID_LENGTH)
            {
                throw new FormatException($"bad {what}");
            }
        }

        internal static void CheckTime(double time, string what)
        {
            if (!IsSaneTime(time))
            {
                throw new FormatException($"bad {what} {time}");
            }
        }
    }

    public abstract class PlayMessage
    {
        /// <summary>How the message travels. Only the latest value of some messages matters.</summary>
        [JsonIgnore]
        public virtual Delivery Delivery => Delivery.Reliable;

        /// <summary>
        /// Check a message from a friend, who may use a modified game: throw <see cref="FormatException"/> if
        /// it cannot be used, and cut text to the <see cref="LinkLimits"/>.
        /// </summary>
        public abstract void Check();
    }

    /// <summary>A message about one play of a song.</summary>
    public abstract class SongPlayMessage : PlayMessage
    {
        /// <summary>The song hash, as a hex string.</summary>
        public string Song;

        /// <summary>
        /// The play of the song. Restarting is another play of the same song, so friends can tell the two apart.
        /// </summary>
        public string Play;

        public override void Check()
        {
            PlayLimits.CheckId(Song, "song hash");
            PlayLimits.CheckId(Play, "play ID");
        }
    }

    /// <summary>
    /// Each game, once for each play, before the start: its players, with their profiles and engine parameters
    /// as replay data.
    /// </summary>
    public sealed class SetupMessage : SongPlayMessage
    {
        public List<SetupPlayer> Players;

        /// <summary>
        /// <c>ReplayData</c> with one frame for each player that can have a highway. YARG.Core's reader trusts
        /// the lengths in it, so the receiver checks it with <c>ReplayBlobCheck</c> first.
        /// </summary>
        public byte[] Replay;

        public override void Check()
        {
            base.Check();
            if (Replay == null)
            {
                throw new FormatException("setup has no replay data");
            }
            if (Players == null)
            {
                throw new FormatException("setup has no players");
            }
            if (Players.Count > LinkLimits.MAX_PLAYERS_PER_MEMBER)
            {
                throw new FormatException($"{Players.Count} players is more than {LinkLimits.MAX_PLAYERS_PER_MEMBER}");
            }
            foreach (var player in Players)
            {
                if (player == null)
                {
                    throw new FormatException("setup has an empty player");
                }
                player.Name = LinkLimits.Clamp(player.Name, LinkLimits.MAX_NAME_LENGTH);
                player.Instrument = LinkLimits.Clamp(player.Instrument, LinkLimits.MAX_SHORT_LENGTH);
                player.Difficulty = LinkLimits.Clamp(player.Difficulty, LinkLimits.MAX_SHORT_LENGTH);
                if (player.StarCutoffs is { } cutoffs &&
                    (cutoffs.Length != SetupPlayer.STAR_CUTOFF_COUNT || Array.Exists(cutoffs, c => c < 0)))
                {
                    player.StarCutoffs = null;
                }
            }
        }
    }

    public sealed class SetupPlayer
    {
        /// <summary>The number of star score cutoffs of a player (one for each star, gold stars included).</summary>
        public const int STAR_CUTOFF_COUNT = 6;

        public string Name;

        /// <summary>
        /// Bots don't record inputs (and the replay format doesn't say who's a bot), so friends simulate a bot of
        /// their own.
        /// </summary>
        public bool Bot;

        /// <summary><c>Instrument</c> enum name.</summary>
        public string Instrument;

        /// <summary><c>Difficulty</c> enum name.</summary>
        public string Difficulty;

        /// <summary>The player's frame in <see cref="SetupMessage.Replay"/>, or -1 for no highway.</summary>
        public int Frame = -1;

        /// <summary>
        /// The player's star score cutoffs. Friends add them to the band's cutoffs, also for a player that has no
        /// highway there. <c>null</c> when the sender didn't send them, or they weren't sane.
        /// </summary>
        [JsonProperty("stars")]
        public int[] StarCutoffs;
    }

    /// <summary>
    /// Each game, 30 to 120 times a second during a song: the inputs of its players since the last message,
    /// with their score. Reliable, since each message only carries the new inputs.
    /// </summary>
    public sealed class InputsMessage : SongPlayMessage
    {
        public int Seq;

        /// <summary>Every input up to this input time has been sent (before each player's calibration).</summary>
        public double Time;

        /// <summary>The sender's input time when it sent this, for friends to measure the clock offset.</summary>
        public double? Clock;

        public List<PlayerInputs> Players;

        public override void Check()
        {
            base.Check();
            PlayLimits.CheckTime(Time, "input time");
            if (Clock is { } clock && !PlayLimits.IsSaneTime(clock))
            {
                Clock = null;
            }
            if (Players == null)
            {
                throw new FormatException("inputs have no players");
            }
            if (Players.Count > LinkLimits.MAX_PLAYERS_PER_MEMBER)
            {
                throw new FormatException($"inputs for {Players.Count} players");
            }
            foreach (var player in Players)
            {
                if (player == null || player.From < 0 || (player.Inputs?.Length ?? 0) / InputCodec.INPUT_SIZE >
                    InputCodec.MAX_INPUTS_PER_MESSAGE)
                {
                    throw new FormatException($"bad inputs for player {player?.Index}");
                }
                player.Inputs ??= Array.Empty<byte>();
            }
        }
    }

    public sealed class PlayerInputs
    {
        /// <summary>The player's index in the sender's <see cref="SetupMessage.Players"/>.</summary>
        [JsonProperty("i")]
        public int Index;

        /// <summary>The index of the first input in <see cref="Inputs"/>, in all of the player's inputs.</summary>
        public int From;

        /// <summary>The inputs, in the format of <see cref="InputCodec"/>.</summary>
        public byte[] Inputs;

        // The player's own numbers, which are the authority for them
        public int   Score;
        [JsonProperty("bonus")]
        public int   BandBonus;
        public int   Combo;
        public float Stars;
        public int   NotesHit;
        public int   TotalNotes;

        /// <summary>The player's star power is active. Friends count it in the band's star power multiplier.</summary>
        [JsonProperty("sp")]
        public bool  StarPower;
    }

    /// <summary>
    /// A game's band meter ran out (on a joiner, only when the host isn't in the play), so friends fail with it.
    /// </summary>
    public sealed class FailedMessage : SongPlayMessage
    {
        public double Time;
    }

    /// <summary>A game left its play of the song: it ended, the players quit, or they restarted.</summary>
    public sealed class LeftMessage : SongPlayMessage
    {
    }

    /// <summary>Host: end the song for everyone ("Leave With Party").</summary>
    public sealed class EndSongMessage : SongPlayMessage
    {
    }

    /// <summary>Host: the band meter failed a player (the member's game, and its index in that game's setup).</summary>
    public sealed class PlayerFailedMessage : SongPlayMessage
    {
        public string Member;
        public int    Index;

        public override void Check()
        {
            base.Check();
            PlayLimits.CheckId(Member, "member");
        }
    }

    /// <summary>Host: star power saved the band.</summary>
    public sealed class RevivedMessage : SongPlayMessage
    {
    }

    /// <summary>
    /// Host, about ten times a second: the band meter, with each player's part of it. Unreliable: only the
    /// latest value matters.
    /// </summary>
    public sealed class MeterMessage : SongPlayMessage
    {
        public float? Happiness;

        /// <summary>
        /// Each player's value, by member ID, then by the index in that member's setup. A <c>null</c> is a player
        /// that the meter doesn't count (failed, or sitting out).
        /// </summary>
        public Dictionary<string, List<float?>> Players;

        [JsonIgnore]
        public override Delivery Delivery => Delivery.Unreliable;

        public override void Check()
        {
            base.Check();
            Happiness = PlayLimits.SaneHappiness(Happiness);
            if (Players == null) return;

            if (Players.Count > LinkLimits.MAX_SESSION_PLAYERS)
            {
                throw new FormatException($"meter for {Players.Count} members");
            }
            foreach (var (member, values) in Players)
            {
                PlayLimits.CheckId(member, "member");
                if (values == null) continue;

                if (values.Count > LinkLimits.MAX_PLAYERS_PER_MEMBER)
                {
                    values.RemoveRange(LinkLimits.MAX_PLAYERS_PER_MEMBER, values.Count - LinkLimits.MAX_PLAYERS_PER_MEMBER);
                }
                for (int i = 0; i < values.Count; i++)
                {
                    values[i] = PlayLimits.SaneHappiness(values[i]);
                }
            }
        }
    }

    /// <summary>
    /// Each game in a synced pause: it paused at this input time, and whether its players are ready to resume.
    /// </summary>
    public sealed class PauseMessage : SongPlayMessage
    {
        /// <summary>Which pause of the play (<c>PauseCoordinator.Epoch</c>).</summary>
        [JsonProperty("n")]
        public int    Epoch;
        public double Time;
        public bool   Ready;

        public override void Check()
        {
            base.Check();
            if (Epoch < 0) throw new FormatException($"bad pause {Epoch}");
            PlayLimits.CheckTime(Time, "pause time");
        }
    }

    /// <summary>Host: everyone is ready; play on from this input time, this long after the host sent it.</summary>
    public sealed class ResumeMessage : SongPlayMessage
    {
        [JsonProperty("n")]
        public int    Epoch;
        public double Time;
        public long   DelayMs;

        public override void Check()
        {
            base.Check();
            if (Epoch < 0) throw new FormatException($"bad pause {Epoch}");
            PlayLimits.CheckTime(Time, "resume time");
        }
    }

    /// <summary>Host: it dropped this member's players out of the song (they left it, or lost the connection).</summary>
    public sealed class DroppedMessage : SongPlayMessage
    {
        public string Member;

        public override void Check()
        {
            base.Check();
            PlayLimits.CheckId(Member, "member");
        }
    }

    /// <summary>A game turned down a friend's song, so everyone can say why.</summary>
    public sealed class DeclinedMessage : PlayMessage
    {
        public const string MISSING = "missing";
        public const string BUSY    = "busy";
        public const string BACKED  = "backed out";

        /// <summary>The song hash.</summary>
        public string Song;
        public string Name;
        /// <summary><see cref="MISSING"/>, <see cref="BUSY"/>, <see cref="BACKED"/>, or something newer.</summary>
        public string Reason;

        public override void Check()
        {
            PlayLimits.CheckId(Song, "song hash");
            Name = LinkLimits.Clamp(Name, LinkLimits.MAX_SONG_TEXT);
            Reason = LinkLimits.Clamp(Reason, LinkLimits.MAX_SHORT_LENGTH);
        }
    }
}
