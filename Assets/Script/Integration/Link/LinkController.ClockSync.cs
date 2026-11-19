using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

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
    /// Every inputs message carries the sender's input time when it was sent (<c>clock</c>), so each game
    /// estimates how far its clock is from each directly connected friend's (<see cref="ClockOffsetEstimator"/>).
    /// The host's clock is the reference: joiners ease theirs toward it by running their song slightly fast or
    /// slow (<see cref="SLEW_RATE"/>), which the audio sync follows without a seek. With every joiner in step
    /// with the host, they're in step with each other too.
    /// </para>
    /// <para>
    /// Easing a large difference would take most of a song, so from <see cref="JUMP_THRESHOLD"/> a joiner
    /// seeks into step once instead (<see cref="GameManager.JumpInputTime"/>). The player hears a jump, and
    /// a jump forward misses the notes it skips. That's better than friends' tracks being off for the rest of
    /// the song.
    /// </para>
    /// </remarks>
    public partial class LinkController
    {
        /// <summary>Start correcting once our clock is this far from the host's, in seconds.</summary>
        private const double CLOCK_START_THRESHOLD = 0.010;

        /// <summary>Stop correcting once we're within this of the host's clock, in seconds.</summary>
        private const double CLOCK_STOP_THRESHOLD = 0.003;

        /// <summary>
        /// How fast to correct, in seconds per second: 0.02 runs our song at 98% or 102% speed while correcting,
        /// so 100 ms takes 5 seconds.
        /// </summary>
        private const double SLEW_RATE = 0.02;

        /// <summary>
        /// From this far apart, in seconds, seek into step instead of easing (25 s at <see cref="SLEW_RATE"/>).
        /// </summary>
        private const double JUMP_THRESHOLD = 0.5;

        /// <summary>
        /// The least time between two jumps, in seconds. A jump starts the estimate over, so the next one is
        /// based on new samples. If those still say we're far apart so soon, the estimate is likely wrong, so
        /// ease meanwhile instead of jumping again.
        /// </summary>
        private const double JUMP_COOLDOWN = 10.0;

        /// <summary>How often to log sync diagnostics during a song.</summary>
        private const double SYNC_LOG_INTERVAL = 5.0;

        /// <summary>Clock offset estimates for directly connected members, by member ID.</summary>
        private readonly Dictionary<string, ClockOffsetEstimator> _clocks = new();

        private bool   _clockCorrecting;
        private double _clockCorrected;
        private double _lastClockJump;
        private double _nextSyncLog;

        private void StartClockSync()
        {
            _clocks.Clear();
            _clockCorrecting = false;
            _clockCorrected = 0;
            _lastClockJump = double.NegativeInfinity;
            _nextSyncLog = Now + SYNC_LOG_INTERVAL;
        }

        private void EndClockSync()
        {
            if (_clockCorrected != 0)
            {
                YargLogger.LogInfo($"Link sync: moved our song clock {Ms(_clockCorrected)} in total to stay in step with the host");
            }
            _clocks.Clear();
            _clockCorrecting = false;
            _clockCorrected = 0;
        }

        /// <summary>
        /// Take a clock sample from a friend's inputs message.
        /// </summary>
        private void OnClockSample(LinkMember member, JObject data)
        {
            var gm = _shared?.GameManager;
            if (gm == null || !gm.IsSongStarted || gm.Paused) return;

            // Right after a synced resume, a friend's message can still be from before theirs
            if (Now < _clockHoldUntil) return;

            // Older games don't send a clock; their watermark is close enough (a frame or so early)
            double? remote = (double?) data["clock"] ?? (double?) data["time"];
            if (remote is not { } remoteTime) return;

            // Only direct connections have a round trip to go by. Joiners only reach each other through the
            // host; being in step with the host puts them in step with each other.
            if (member.Peer == null || _session.GetStats(member).Rtt is not { } rtt) return;

            if (!_clocks.TryGetValue(member.Id, out var clock))
            {
                clock = new ClockOffsetEstimator();
                _clocks[member.Id] = clock;
            }
            clock.AddSample(Now, gm.InputTimeAtCall, remoteTime, rtt.TotalSeconds / 2, gm.SongSpeed);
        }

        /// <summary>Call every frame.</summary>
        private void UpdateClockSync()
        {
            var gm = _shared?.GameManager;
            if (gm == null || !gm.IsSongStarted) return;

            if (gm.Paused)
            {
                // Our clock stops while theirs keeps going; start measuring over once we resume
                foreach (var clock in _clocks.Values) clock.Reset();
                _clockCorrecting = false;
                return;
            }

            LogSyncDiagnostics(gm);
            CorrectClock(gm);
        }

        /// <summary>Joiner only: ease our song clock toward the host's.</summary>
        private void CorrectClock(GameManager gm)
        {
            if (_session.Role != LinkRole.Join || gm.Rewinding) return;

            var host = FindHost();
            if (host == null || !_shared.HostInPlay || !_clocks.TryGetValue(host.Id, out var clock) || !clock.HasEstimate) return;

            double offset = clock.Offset;
            double distance = Math.Abs(offset);
            if (distance >= JUMP_THRESHOLD && Now - _lastClockJump >= JUMP_COOLDOWN && gm.JumpInputTime(-offset))
            {
                YargLogger.LogWarning($"Link sync: our song was {Describe(offset)} {host.Name}'s, " +
                    "too far apart to ease; jumped into step");
                _lastClockJump = Now;
                _clockCorrecting = false;
                _clockCorrected -= offset;

                // Our clock jumped, so measure again from here
                clock.Reset();
                return;
            }

            if (!_clockCorrecting)
            {
                if (distance < CLOCK_START_THRESHOLD) return;

                _clockCorrecting = true;
                YargLogger.LogInfo($"Link sync: our song is {Describe(offset)} {host.Name}'s, easing into step " +
                    $"(about {distance / SLEW_RATE:0.0} s)");
            }
            else if (distance < CLOCK_STOP_THRESHOLD)
            {
                _clockCorrecting = false;
                YargLogger.LogInfo($"Link sync: in step with {host.Name} (within {Ms(offset)})");
                return;
            }

            double maxStep = SLEW_RATE * Time.unscaledDeltaTime * gm.SongSpeed;
            double step = Math.Clamp(-offset, -maxStep, maxStep);
            gm.ShiftInputTime(step);
            clock.Shift(step);
            _clockCorrected += step;
        }

        private void LogSyncDiagnostics(GameManager gm)
        {
            if (Now < _nextSyncLog) return;
            _nextSyncLog = Now + SYNC_LOG_INTERVAL;

            var text = new StringBuilder();
            foreach (var setup in _songSetups.Values)
            {
                var member = FindMember(setup.Feeds.Count > 0 ? setup.Feeds[0].Member : null);
                string rtt = member != null && member.Peer != null && _session.GetStats(member).Rtt is { } r
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

        /// <summary>Short sync status for a friend's scoreboard row, or <c>null</c>.</summary>
        private string SyncDetail(RemoteInputFeed feed)
        {
            if (!feed.HasData) return null;

            string detail = $"hits +{feed.Lag * 1000:0} ms";
            if (_clocks.TryGetValue(feed.Member, out var clock) && clock.HasEstimate)
            {
                detail += $" · clock {clock.Offset * 1000:+0;-0;0} ms";
            }
            return detail;
        }

        private LinkMember FindHost()
        {
            foreach (var member in _session.Members)
            {
                if (member.IsHost) return member;
            }
            return null;
        }

        private static string Ms(double seconds) => $"{seconds * 1000:+0;-0;0} ms";

        private static string Describe(double offset) =>
            offset >= 0 ? $"{offset * 1000:0} ms ahead of" : $"{-offset * 1000:0} ms behind";
    }
}
