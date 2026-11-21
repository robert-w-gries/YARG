using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Keeping the games' song clocks in step during a synced song, and diagnostics on how far behind friends'
    /// tracks are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The synced start schedules every game to start at the same moment, but only as well as half the round
    /// trip estimates the one-way latency, and each game then has its own loading hitches and audio startup.
    /// The games can end up tens or hundreds of milliseconds apart. That shows up as one game's view of its
    /// friends' tracks lagging more than the other's.
    /// </para>
    /// <para>
    /// Every inputs message carries the sender's input time when it was sent (<see cref="InputsMessage.Clock"/>),
    /// so each game estimates how far its clock is from each directly connected friend's
    /// (<see cref="ClockOffsetEstimator"/>). The host's clock is the reference: joiners move theirs toward it
    /// (<see cref="ClockCorrector"/>). With every joiner in step with the host, they're in step with each other
    /// too.
    /// </para>
    /// </remarks>
    internal sealed class ClockSync
    {
        /// <summary>How often to log sync diagnostics during a song, in seconds.</summary>
        private const double SYNC_LOG_INTERVAL = 5.0;

        private readonly SyncedSong _song;
        private readonly Dictionary<string, ClockOffsetEstimator> _clocks = new();
        private readonly ClockCorrector _corrector = new();

        private double _nextSyncLog;
        private double _holdUntil;

        public ClockSync(SyncedSong song)
        {
            _song = song;
            _nextSyncLog = LinkController.Now + SYNC_LOG_INTERVAL;
        }

        /// <summary>Clock offset estimates for directly connected members, by member ID.</summary>
        public IReadOnlyDictionary<string, ClockOffsetEstimator> Estimates => _clocks;

        /// <summary>Joiner: easing toward the host's clock right now.</summary>
        public bool Correcting => _corrector.Correcting;

        /// <summary>Joiner: how far our clock has been moved in total this song, in seconds.</summary>
        public double Corrected => _corrector.Corrected;

        /// <summary>
        /// Ignore friends' clocks until <paramref name="until"/>: right after a synced resume, a friend's messages
        /// can still be from before theirs.
        /// </summary>
        public void HoldUntil(double until) => _holdUntil = until;

        public void End()
        {
            if (_corrector.Corrected != 0)
            {
                YargLogger.LogInfo($"Link sync: moved our song clock {Ms(_corrector.Corrected)} in total to stay in step with the host");
            }
        }

        /// <summary>Take a clock sample from a friend's inputs message.</summary>
        public void OnSample(LinkMember member, InputsMessage message, double now)
        {
            var gm = _song.GameManager;
            if (gm == null || !gm.IsSongStarted || gm.Paused) return;
            if (now < _holdUntil) return;

            // Older games don't send a clock; their watermark is close enough (a frame or so early)
            double remoteTime = message.Clock ?? message.Time;

            // Only direct connections have a round trip to go by. Joiners only reach each other through the
            // host; being in step with the host puts them in step with each other.
            if (member.Peer == null || _song.Session.GetStats(member).Rtt is not { } rtt) return;

            if (!_clocks.TryGetValue(member.Id, out var clock))
            {
                clock = new ClockOffsetEstimator();
                _clocks[member.Id] = clock;
            }
            clock.AddSample(now, gm.InputTimeAtCall, remoteTime, rtt.TotalSeconds / 2, gm.SongSpeed);
        }

        /// <summary>Call every frame.</summary>
        public void Update(double now)
        {
            var gm = _song.GameManager;
            if (gm == null || !gm.IsSongStarted) return;

            if (gm.Paused)
            {
                // Our clock stops while theirs keeps going; start measuring over once we resume
                foreach (var clock in _clocks.Values) clock.Reset();
                _corrector.Pause();
                return;
            }

            LogDiagnostics(gm, now);
            Correct(gm, now);
        }

        /// <summary>Joiner only: move our song clock toward the host's.</summary>
        private void Correct(GameManager gm, double now)
        {
            if (_song.IsHost || gm.Rewinding || !_song.HostInPlay) return;

            var host = _song.Host;
            if (host == null || !_clocks.TryGetValue(host.Id, out var clock) || !clock.HasEstimate) return;

            double offset = clock.Offset;
            if (_corrector.ShouldJump(offset, now) && gm.JumpInputTime(-offset))
            {
                YargLogger.LogWarning($"Link sync: our song was {Describe(offset)} {host.Name}'s, " +
                    "too far apart to ease; jumped into step");
                _corrector.Jumped(-offset, now);

                // Our clock jumped, so measure again from here
                clock.Reset();
                return;
            }

            var result = _corrector.Ease(offset, Time.unscaledDeltaTime, gm.SongSpeed, out double step);
            switch (result)
            {
                case ClockCorrector.EaseResult.Idle:
                    return;
                case ClockCorrector.EaseResult.Stopped:
                    YargLogger.LogInfo($"Link sync: in step with {host.Name} (within {Ms(offset)})");
                    return;
                case ClockCorrector.EaseResult.Started:
                    YargLogger.LogInfo($"Link sync: our song is {Describe(offset)} {host.Name}'s, easing into step " +
                        $"(about {Math.Abs(offset) / ClockCorrector.SLEW_RATE:0.0} s)");
                    break;
            }

            gm.ShiftInputTime(step);
            clock.Shift(step);
        }

        private void LogDiagnostics(GameManager gm, double now)
        {
            if (now < _nextSyncLog) return;
            _nextSyncLog = now + SYNC_LOG_INTERVAL;

            var text = new StringBuilder();
            foreach (var setup in _song.Friends.Values)
            {
                var member = _song.FindMember(setup.Feeds.Count > 0 ? setup.Feeds[0].Member : null);
                string rtt = member != null && member.Peer != null && _song.Session.GetStats(member).Rtt is { } r
                    ? $"{r.TotalMilliseconds:0} ms" : "n/a";
                string clock = member != null && _clocks.TryGetValue(member.Id, out var c) && c.HasEstimate
                    ? $"our clock {Describe(c.Offset)} theirs" : "clock offset unknown";

                foreach (var feed in setup.Feeds)
                {
                    if (!feed.Mirrored) continue;
                    text.Append($"\n  {feed.Name}: hits {feed.Lag * 1000:0} ms late (max {feed.TakeMaxLag() * 1000:0}), " +
                        $"{clock}, round trip {rtt}");
                }
            }

            if (text.Length > 0)
            {
                YargLogger.LogInfo($"Link sync at {gm.InputTime:0.0}s:{text}");
            }
        }

        private static string Ms(double seconds) => $"{seconds * 1000:+0;-0;0} ms";

        private static string Describe(double offset) =>
            offset >= 0 ? $"{offset * 1000:0} ms ahead of" : $"{-offset * 1000:0} ms behind";
    }
}
