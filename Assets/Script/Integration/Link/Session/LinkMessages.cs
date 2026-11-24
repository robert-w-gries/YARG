using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// The session protocol: what games say to each other over an <see cref="Transport.ILinkTransport"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each transport message is one UTF-8 JSON object with a <c>"t"</c> type tag, for example
    /// <c>{"t":"start","delayMs":2000}</c>. The first message on every connection is a <see cref="HelloMessage"/>;
    /// peers whose game version differs are refused with a <see cref="ByeMessage"/>.
    /// </para>
    /// <para>
    /// There's no separate protocol version: game versions must match exactly, and any change to these
    /// messages ships in a new game version. (Local builds with uncommitted changes share their commit's
    /// version, so mixing them can fail in other ways.)
    /// </para>
    /// <para>
    /// The topology is a star: joiners connect to the host only. The host runs the synced-start coordinator
    /// and relays each member's <see cref="GameDataMessage"/> to the other members.
    /// </para>
    /// </remarks>
    public static class LinkProtocol
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        });

        private static readonly Dictionary<string, Type> TypesByTag = new();
        private static readonly Dictionary<Type, string> TagsByType = new();

        static LinkProtocol()
        {
            Register<HelloMessage>("hello");
            Register<ByeMessage>("bye");
            Register<RosterMessage>("roster");
            Register<SceneMessage>("scene");
            Register<StateMessage>("state");
            Register<ReadyMessage>("ready");
            Register<DeclineMessage>("decline");
            Register<LoadSongMessage>("load_song");
            Register<StartMessage>("start");
            Register<CancelMessage>("cancel");
            Register<RestartRequestMessage>("restart_request");
            Register<RestartMessage>("restart");
            Register<RestartWithdrawMessage>("restart_withdraw");
            Register<RestartVoteMessage>("restart_vote");
            Register<GameDataMessage>("game");
        }

        private static void Register<T>(string tag) where T : LinkMessage
        {
            TypesByTag[tag] = typeof(T);
            TagsByType[typeof(T)] = tag;
        }

        public static string TagOf(LinkMessage message) => TagsByType[message.GetType()];

        public static byte[] Encode(LinkMessage message)
        {
            var obj = JObject.FromObject(message, Serializer);
            obj.AddFirst(new JProperty("t", TagOf(message)));
            return Encoding.UTF8.GetBytes(obj.ToString(Formatting.None));
        }

        /// <summary>
        /// Parse a message.
        /// </summary>
        /// <returns>The message, or <c>null</c> for a type this version doesn't know.</returns>
        /// <exception cref="FormatException">The bytes aren't a well-formed message.</exception>
        public static LinkMessage Decode(byte[] bytes)
        {
            try
            {
                var obj = JObject.Parse(Encoding.UTF8.GetString(bytes));
                string tag = (string) obj["t"];
                if (tag == null)
                {
                    throw new FormatException("Message has no type");
                }

                if (!TypesByTag.TryGetValue(tag, out var type))
                {
                    return null;
                }

                obj.Remove("t");
                return (LinkMessage) obj.ToObject(type, Serializer);
            }
            catch (JsonException e)
            {
                throw new FormatException($"Bad message: {e.Message}", e);
            }
            catch (ArgumentException e)
            {
                throw new FormatException($"Bad message: {e.Message}", e);
            }
        }
    }

    /// <summary>A song, as identified across machines.</summary>
    public sealed class SongInfo : IEquatable<SongInfo>
    {
        /// <summary><c>SongEntry.Hash</c> as a hex string.</summary>
        public string Hash;
        public string Name;
        public string Artist;

        public SongInfo()
        {
        }

        public SongInfo(string hash, string name, string artist)
        {
            Hash = hash;
            Name = name;
            Artist = artist;
        }

        public bool Equals(SongInfo other) =>
            other != null && Hash == other.Hash && Name == other.Name && Artist == other.Artist;

        public override bool Equals(object obj) => obj is SongInfo other && Equals(other);
        public override int GetHashCode() => Hash?.GetHashCode() ?? 0;
        public override string ToString() => $"\"{Name}\" by {Artist} ({Hash})";
    }

    public abstract class LinkMessage
    {
    }

    /// <summary>Both ways, first on every connection.</summary>
    public sealed class HelloMessage : LinkMessage
    {
        /// <summary>The game build; peers must match exactly, since remote highways replay inputs.</summary>
        public string Game;
        /// <summary>Stable for the lifetime of a session; identifies the member to everyone.</summary>
        public string Member;
        public string Name;
        /// <summary>Whether the sender is hosting. A joiner only talks to a host.</summary>
        public bool   Host;
    }

    /// <summary>Both ways: the sender refuses the connection and is about to close it.</summary>
    public sealed class ByeMessage : LinkMessage
    {
        /// <summary>For people: why the connection is closing.</summary>
        public string Reason;

        /// <summary>For the game: one of <see cref="ByeCodes"/>, or <c>null</c> from an older version.</summary>
        public string Code;
    }

    /// <summary>Why a connection was closed, so a joiner can tell whether trying again could help.</summary>
    public static class ByeCodes
    {
        /// <summary>The other side left the session on purpose.</summary>
        public const string LEFT = "left";

        /// <summary>Different protocol or game versions. Retrying won't help until someone updates.</summary>
        public const string INCOMPATIBLE = "incompatible";

        /// <summary>The host removed this member from the session.</summary>
        public const string KICKED = "kicked";

        /// <summary>The host already has as many members as it takes.</summary>
        public const string FULL = "full";

        /// <summary>Refused for another reason (both hosting, already in the session, ...).</summary>
        public const string REFUSED = "refused";
    }

    /// <summary>One of a member's players, as shown in the lobby.</summary>
    public sealed class PlayerInfo : IEquatable<PlayerInfo>
    {
        public string Name;
        /// <summary><c>Instrument</c> enum name, for example <c>"FiveFretGuitar"</c>.</summary>
        public string Instrument;
        /// <summary><c>Difficulty</c> enum name, for example <c>"Expert"</c>.</summary>
        public string Difficulty;
        public bool   Bot;

        public bool Equals(PlayerInfo other) => other != null && Name == other.Name &&
            Instrument == other.Instrument && Difficulty == other.Difficulty && Bot == other.Bot;

        public override bool Equals(object obj) => obj is PlayerInfo other && Equals(other);
        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
    }

    public sealed class MemberInfo
    {
        public string Id;
        public string Name;
        /// <summary>See <see cref="SceneMessage.Scene"/>.</summary>
        public string Scene;
        public bool   Host;
        /// <summary>See <see cref="StateMessage.Status"/>.</summary>
        public string Status;
        public List<PlayerInfo> Players;
    }

    /// <summary>Host → joiners, whenever someone joins, leaves or changes scene. Includes the receiver.</summary>
    public sealed class RosterMessage : LinkMessage
    {
        public List<MemberInfo> Members = new();
    }

    /// <summary>Joiner → host, whenever the joiner's scene changes.</summary>
    public sealed class SceneMessage : LinkMessage
    {
        /// <summary><c>"menu"</c>, <c>"gameplay"</c>, <c>"score"</c>, or anything else the game reports.</summary>
        public string Scene;
    }

    /// <summary>
    /// Joiner → host, whenever it changes: what the joiner is doing and who is playing, for the lobby.
    /// The session only passes it on; the game decides what the values mean.
    /// </summary>
    public sealed class StateMessage : LinkMessage
    {
        /// <summary>For example <c>"browsing"</c>, <c>"ready"</c> or <c>"playing"</c>.</summary>
        public string Status;
        public List<PlayerInfo> Players;
    }

    /// <summary>Joiner → host: gameplay for this song is loaded and holding for the start.</summary>
    public sealed class ReadyMessage : LinkMessage
    {
        public SongInfo Song;
    }

    /// <summary>Joiner → host: the joiner can't or won't play the song being started.</summary>
    public sealed class DeclineMessage : LinkMessage
    {
        public string Hash;
        public string Reason;
    }

    /// <summary>Host → joiner: someone picked this song; load it and report ready or decline.</summary>
    public sealed class LoadSongMessage : LinkMessage
    {
        public SongInfo Song;
    }

    /// <summary>
    /// Host → joiner: start playback this long after the host sent it. The joiner subtracts its one-way
    /// latency to the host.
    /// </summary>
    public sealed class StartMessage : LinkMessage
    {
        public long DelayMs;
    }

    /// <summary>Host → joiner: the pending start was abandoned.</summary>
    public sealed class CancelMessage : LinkMessage
    {
        public string Reason;
    }

    /// <summary>
    /// Joiner → host: play this song again from the start, with everyone in our play of it. This opens or joins
    /// a vote: the song restarts once everyone in it asks.
    /// </summary>
    public sealed class RestartRequestMessage : LinkMessage
    {
        public SongInfo Song;

        /// <summary>The play of the song the joiner is in. The session doesn't look inside it.</summary>
        public string Play;
    }

    /// <summary>
    /// Host → joiner in gameplay: a member asked to play the song again. Load it again and report ready if you're
    /// in their play of it, else decline.
    /// </summary>
    public sealed class RestartMessage : LinkMessage
    {
        public SongInfo Song;

        /// <summary>The member who asked.</summary>
        public string Member;

        /// <summary>The play of the song that <see cref="Member"/> is in.</summary>
        public string Play;
    }

    /// <summary>Joiner → host: our song carried on, so the restart vote for it is over.</summary>
    public sealed class RestartWithdrawMessage : LinkMessage
    {
        public string Hash;
    }

    /// <summary>
    /// Host → joiner: the restart vote of the joiner's song changed or ended. See
    /// <see cref="SyncStartCoordinator.RestartVote"/> for the fields.
    /// </summary>
    public sealed class RestartVoteMessage : LinkMessage
    {
        public SongInfo     Song;
        public List<string> Yes;
        public List<string> Waiting;
        public bool         Ended;
        public string       Reason;
    }

    /// <summary>
    /// Gameplay data for every other member (player setups, input streams). The session doesn't look inside.
    /// </summary>
    public sealed class GameDataMessage : LinkMessage
    {
        /// <summary>The member it came from. Filled in by the host; a joiner's own value is ignored.</summary>
        public string  From;
        public string  Kind;
        public JObject Data;
    }
}
