using System;
using System.Collections.Generic;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// Bounds on what other members can send us. Every member, the host included, may be running a modified
    /// game, so anything that arrives is cut down to these before the rest of the game sees it.
    /// </summary>
    public static class LinkLimits
    {
        /// <summary>The largest message the transports carry (<c>YL_MAX_RELIABLE</c>).</summary>
        public const int MAX_MESSAGE_BYTES = 1024 * 1024 - 1;

        /// <summary>The largest message from a peer that hasn't said hello yet. A hello is far smaller.</summary>
        public const int MAX_UNADMITTED_MESSAGE_BYTES = 4 * 1024;

        public const int MAX_ID_LENGTH     = 64;
        public const int MAX_NAME_LENGTH   = 64;
        public const int MAX_SHORT_LENGTH  = 32;
        public const int MAX_SONG_TEXT     = 256;
        public const int MAX_REASON_LENGTH = 256;

        /// <summary>Players one member may report. <see cref="MAX_SESSION_PLAYERS"/> also limits the total.</summary>
        public const int MAX_PLAYERS_PER_MEMBER = 16;

        /// <summary>
        /// The most players in one song, for all members together. This is the limit of YARG itself: the
        /// highway renderer has 32 slots (<c>HighwayCameraRendering.MAX_MATRICES</c>), and the profile menu
        /// stops at 32 players. A host refuses a new joiner when the session has this many players.
        /// </summary>
        public const int MAX_SESSION_PLAYERS = 32;

        /// <summary>Cut <paramref name="text"/> to at most <paramref name="max"/> characters.</summary>
        public static string Clamp(string text, int max)
        {
            if (text == null || text.Length <= max) return text;

            // Don't leave half a surrogate pair at the end
            int length = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
            return text.Substring(0, length);
        }

        /// <summary>A copy of <paramref name="players"/> with at most <see cref="MAX_PLAYERS_PER_MEMBER"/> entries, each clamped.</summary>
        public static List<PlayerInfo> Clamp(IReadOnlyList<PlayerInfo> players)
        {
            var clamped = new List<PlayerInfo>();
            if (players == null) return clamped;

            foreach (var player in players)
            {
                if (clamped.Count >= MAX_PLAYERS_PER_MEMBER) break;
                if (player == null) continue;

                clamped.Add(new PlayerInfo
                {
                    Name = Clamp(player.Name, MAX_NAME_LENGTH),
                    Instrument = Clamp(player.Instrument, MAX_SHORT_LENGTH),
                    Difficulty = Clamp(player.Difficulty, MAX_SHORT_LENGTH),
                    Bot = player.Bot,
                });
            }
            return clamped;
        }

        /// <summary>A clamped copy of <paramref name="song"/>, or <c>null</c> if it has no usable hash.</summary>
        public static SongInfo Clamp(SongInfo song)
        {
            if (song?.Hash == null || song.Hash.Length > MAX_ID_LENGTH) return null;
            return new SongInfo(song.Hash, Clamp(song.Name, MAX_SONG_TEXT), Clamp(song.Artist, MAX_SONG_TEXT));
        }
    }

    /// <summary>
    /// A token bucket: allows <c>rate</c> per second on average, with bursts of up to <c>burst</c>.
    /// </summary>
    internal sealed class TokenBucket
    {
        private readonly double _rate;
        private readonly double _burst;
        private double _tokens;
        private double _last;

        public TokenBucket(double rate, double burst, double now)
        {
            _rate = Math.Max(0, rate);
            _burst = Math.Max(0, burst);
            _tokens = _burst;
            _last = now;
        }

        /// <summary>Take <paramref name="amount"/> tokens if there are that many.</summary>
        public bool TryTake(double amount, double now)
        {
            if (now > _last)
            {
                _tokens = Math.Min(_burst, _tokens + (now - _last) * _rate);
                _last = now;
            }

            if (_tokens < amount) return false;
            _tokens -= amount;
            return true;
        }
    }
}
