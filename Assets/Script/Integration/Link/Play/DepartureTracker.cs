using System.Collections.Generic;

namespace YARG.Integration.Link.Play
{
    /// <summary>
    /// Which friends to drop out of a synced song, because they left it or lost their connection. Pure: the
    /// caller says what it sees of each friend, and drops their players.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A friend's mirrored engine only runs as far as their inputs have arrived, so once they're gone it freezes
    /// mid-song (notes scroll by unplayed, multiplier stuck, unisons waiting on them). Their players drop out
    /// instead, like a local player dropping out.
    /// </para>
    /// <para>
    /// The host decides, and tells everyone, so every game drops the same friends. A joiner only decides about
    /// the host itself, once it has left the song: losing the connection to the host is
    /// <see cref="HostLossTracker"/>'s business.
    /// </para>
    /// <para>
    /// The transport can take half a minute to notice a game that crashed or a network that died. Games in a
    /// song send inputs at least 30 times a second, paused or not, so a friend whose inputs stopped for
    /// <see cref="INPUTS_SILENCE"/> has lost the connection, whatever the transport says. Their loss counts from
    /// their last inputs, so the host and the friend (which counts the same way) agree.
    /// </para>
    /// </remarks>
    public sealed class DepartureTracker
    {
        /// <summary>
        /// How long a friend who left the song keeps their highway, in seconds (it may have just ended for them a
        /// moment before us).
        /// </summary>
        public const double LEFT_GRACE = 2.0;

        /// <summary>
        /// How long a lost connection lasts mid-song, in seconds, before it's the end of the song for that player.
        /// Both sides use it: the host drops a friend it lost this long ago, and a joiner who lost the host this
        /// long ago leaves by itself (<see cref="HostLossTracker.LOST_CONNECTION"/>).
        /// </summary>
        public const double LOST_CONNECTION_DROP = 5.0;

        /// <summary>A friend we've heard inputs from but not for this long has lost their connection, in seconds.</summary>
        public const double INPUTS_SILENCE = 3.0;

        public enum Departure
        {
            /// <summary>Still here, or not gone for long enough yet.</summary>
            None,
            /// <summary>Left the song: drop their players.</summary>
            Left,
            /// <summary>Lost the connection: drop their players.</summary>
            Lost,
        }

        /// <summary>What a game sees of a friend with players on a highway, each frame.</summary>
        public readonly struct Friend
        {
            public readonly string Member;
            /// <summary>Not in the session, or we lost the session.</summary>
            public readonly bool   Disconnected;
            /// <summary>Out of gameplay, or onto another play of the song (a restart).</summary>
            public readonly bool   Left;
            public readonly bool   IsHost;

            public Friend(string member, bool disconnected, bool left, bool isHost)
            {
                Member = member;
                Disconnected = disconnected;
                Left = left;
                IsHost = isHost;
            }
        }

        private readonly Dictionary<string, double> _lastInputs = new();
        private readonly Dictionary<string, double> _goneSince = new();
        private readonly HashSet<string> _dropped = new();

        /// <summary>Friends whose players are dropped out of this song.</summary>
        public IReadOnlyCollection<string> Dropped => _dropped;

        public bool IsDropped(string member) => _dropped.Contains(member);

        /// <summary>Their players are dropped out now.</summary>
        public void MarkDropped(string member)
        {
            _dropped.Add(member);
            _goneSince.Remove(member);
        }

        /// <summary>Inputs arrived from <paramref name="member"/>.</summary>
        public void NoteInputs(string member, double now) => _lastInputs[member] = now;

        public bool TryGetLastInputs(string member, out double at) => _lastInputs.TryGetValue(member, out at);

        /// <summary>Since when a friend has been seen gone (they may still come back before the drop).</summary>
        public bool TryGetGoneSince(string member, out double since) => _goneSince.TryGetValue(member, out since);

        /// <summary>
        /// When <paramref name="member"/>'s inputs stopped, if they've been quiet for <see cref="INPUTS_SILENCE"/>.
        /// <c>null</c> if they're still sending, or never have.
        /// </summary>
        public double? SilentSince(string member, double now) =>
            _lastInputs.TryGetValue(member, out double last) && now - last >= INPUTS_SILENCE ? last : null;

        /// <summary>Whether to drop a friend's players now. Call each frame for each friend not yet dropped.</summary>
        /// <param name="weHost">This game is the host, which decides about everyone.</param>
        public Departure Check(in Friend friend, bool weHost, double now)
        {
            string id = friend.Member;
            bool left = !friend.Disconnected && friend.Left;
            double? silentSince = friend.Disconnected || left ? null : SilentSince(id, now);
            bool lost = friend.Disconnected || silentSince.HasValue;

            bool gone = weHost ? lost || left : left && friend.IsHost;
            if (!gone)
            {
                _goneSince.Remove(id);
                return Departure.None;
            }

            // A friend who went quiet has been gone since their last inputs, not since we noticed
            double start = silentSince ?? now;
            if (!_goneSince.TryGetValue(id, out double since) || start < since)
            {
                _goneSince[id] = since = start;
            }

            if (now - since < (left ? LEFT_GRACE : LOST_CONNECTION_DROP)) return Departure.None;

            _goneSince.Remove(id);
            return left ? Departure.Left : Departure.Lost;
        }
    }
}
