using System.Collections.Generic;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Pausing together during a synced song.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When anyone pauses (the pause menu, losing focus, a controller disconnecting), they send a
    /// <see cref="PauseMessage"/> with their input time, and every other game pauses too and sends its own.
    /// Pressing resume doesn't resume: it marks that player ready (another <see cref="PauseMessage"/>, with
    /// <see cref="PauseMessage.Ready"/>) and closes their menu. Pausing again, or pressing resume again, reopens
    /// the menu and takes it back.
    /// </para>
    /// <para>
    /// The host resumes everyone once the host and every friend in the song are ready. It picks one input time
    /// a little before the latest pause (<see cref="PauseCoordinator"/>) and sends a <see cref="ResumeMessage"/>
    /// with that time and a lead. Each game rewinds to that time and plays on at the same moment, the way a
    /// synced start does (<see cref="ResumeTiming"/>). Clock correction takes care of the rest.
    /// </para>
    /// <para>
    /// A failed song's pause menu isn't synced: failing is (<see cref="FailedMessage"/>), and the band's meter
    /// decides it.
    /// </para>
    /// </remarks>
    internal sealed class PauseSync
    {
        private readonly SyncedSong _song;
        private readonly ClockSync  _clock;
        private readonly PauseCoordinator _pause;

        /// <summary>When to resume (on <see cref="LinkController.Now"/>), and from what input time, once scheduled.</summary>
        private double? _resumeAt;
        private double  _resumeInputTime;

        /// <summary>We're pausing our game because a friend paused, not because our player did.</summary>
        private bool _pausingFromLink;

        /// <summary>We're resuming our game for a synced resume, so <see cref="HoldResume"/> lets it through.</summary>
        private bool _resumingFromLink;

        public PauseSync(SyncedSong song, ClockSync clock)
        {
            _song = song;
            _clock = clock;
            _pause = new PauseCoordinator(song.Session.LocalMemberId, GameManager.PAUSE_REWIND_LENGTH);
        }

        /// <summary>Whether a synced pause is holding the song (for the lobby, and the HUD).</summary>
        public bool IsActive => _pause.Active || _resumeAt.HasValue;

        /// <summary>When the song plays again, if a resume is scheduled.</summary>
        public double? ResumeAt => _resumeAt;

        /// <summary>Who started the current pause, if one is active.</summary>
        public string PausedBy => _pause.Active ? _pause.PausedBy : null;

        /// <summary>
        /// Whoever runs this play of the song resumes it: the host, or a joiner playing it without the host
        /// (one that restarted alone, say), who would otherwise wait for a resume that never comes.
        /// </summary>
        private bool LeadsSong => !_song.HostInPlay;

        /// <summary>Called by <see cref="GameManager"/> when it opens the pause menu, whatever opened it.</summary>
        public void OnLocalPause(GameManager gm)
        {
            if (!_song.IsFor(gm) || !gm.IsSongStarted || gm.PlayerHasFailed) return;

            // Pausing during a resume's countdown takes it back; the others pause again when they hear
            _resumeAt = null;

            string pausedBy = _pausingFromLink ? _pause.PausedBy : null;
            if (_pause.OnLocalPause(gm.InputTime, pausedBy))
            {
                YargLogger.LogInfo($"Link pause: we paused at {gm.InputTime:0.000}s, pausing friends");
            }

            SendPause();
        }

        /// <summary>
        /// Called by <see cref="GameManager.Resume"/>. In a synced pause, resuming marks us ready (or, if we
        /// were ready, reopens the pause menu) instead of resuming, and the host resumes everyone.
        /// </summary>
        /// <returns>Whether we took over, so the game shouldn't resume.</returns>
        public bool HoldResume(GameManager gm, double now)
        {
            if (_resumingFromLink || !_song.IsFor(gm) || gm.PlayerHasFailed) return false;

            if (!_pause.Active)
            {
                // Told to resume already, and waiting for the moment
                return _resumeAt.HasValue;
            }

            if (_pause.LocalTime == null)
            {
                // We're paused but never reported it; do that, and let the press count as ready below
                _pause.OnLocalPause(gm.InputTime, _pause.PausedBy);
            }

            bool ready = !_pause.LocalReady;
            _pause.SetLocalReady(ready);
            if (ready)
            {
                gm.HidePauseMenuWhileWaitingForBand();
                YargLogger.LogInfo("Link pause: we're ready to resume");
            }
            else
            {
                gm.ShowPauseMenuWhileWaitingForBand();
                YargLogger.LogInfo("Link pause: we're not ready after all");
            }

            SendPause();
            TryResumeAsLeader(now);
            return true;
        }

        private void SendPause()
        {
            if (_pause.LocalTime is not { } time) return;

            _song.Send(new PauseMessage
            {
                Epoch = _pause.Epoch,
                Time = time,
                Ready = _pause.LocalReady,
            });
        }

        /// <summary>Call every frame.</summary>
        public void Update(double now)
        {
            var gm = _song.GameManager;
            if (!_song.IsFor(gm)) return;

            if (_resumeAt is { } at && ResumeTiming.ShouldStart(at, now))
            {
                ExecuteResume(gm, at, now);
                return;
            }

            if (!_pause.Active) return;

            // A friend paused, but we couldn't follow at the time (still loading, or mid-resume)
            if (_pause.LocalTime == null && gm.IsSongStarted && !gm.PlayerHasFailed)
            {
                PauseFromLink(gm);
            }

            // Someone leaving can leave everyone else ready
            TryResumeAsLeader(now);
        }

        /// <summary>Pause our game because a friend paused.</summary>
        private void PauseFromLink(GameManager gm)
        {
            _pausingFromLink = true;
            try
            {
                if (gm.Paused && !gm.Rewinding)
                {
                    // Already paused (we paused at the same moment, or are waiting on a resume): just report it,
                    // and show the menu if we'd closed it as ready
                    OnLocalPause(gm);
                }
                else
                {
                    // Pausing mid-rewind cancels the rewind, as it does for a player pausing then
                    gm.Pause();
                    return;
                }
            }
            finally
            {
                _pausingFromLink = false;
            }

            gm.ShowPauseMenuWhileWaitingForBand();
        }

        /// <summary>The leader (<see cref="LeadsSong"/>) only: resume everyone if everyone is ready.</summary>
        private void TryResumeAsLeader(double now)
        {
            if (!LeadsSong || !_pause.Active) return;

            if (_pause.TryResume(_song.Participants()) is not { } plan) return;

            YargLogger.LogInfo($"Link pause: everyone's ready, resuming from {plan.InputTime:0.000}s in {ResumeTiming.LEAD:0.0}s");
            _song.Send(new ResumeMessage
            {
                Epoch = plan.Epoch,
                Time = plan.InputTime,
                DelayMs = ResumeTiming.LeadMs,
            });
            Schedule(now + ResumeTiming.LEAD, plan.InputTime);
        }

        private void Schedule(double at, double inputTime)
        {
            _resumeAt = at;
            _resumeInputTime = inputTime;
        }

        /// <param name="at">When the song was scheduled to play (on <see cref="LinkController.Now"/>).</param>
        private void ExecuteResume(GameManager gm, double at, double now)
        {
            _resumeAt = null;
            if (!gm.Paused || gm.PlayerHasFailed) return;

            var plan = ResumeTiming.PlanRewind(gm.InputTime, _resumeInputTime, at, now, gm.SongSpeed);
            if (plan.Behind > 0)
            {
                YargLogger.LogWarning($"Link pause: resume came {plan.Late * 1000:0} ms late; we'll start " +
                    $"{plan.Behind * 1000:0} ms behind the others");
            }

            YargLogger.LogInfo($"Link pause: resuming with friends from {gm.InputTime - plan.Rewind:0.000}s " +
                $"(rewinding {plan.Rewind:0.000}s from our pause, {plan.Late * 1000:0} ms late)");

            _resumingFromLink = true;
            try
            {
                gm.Resume(plan.Rewind);
            }
            finally
            {
                _resumingFromLink = false;
            }

            _clock.HoldUntil(now + ResumeTiming.REWIND_ANIMATION + ResumeTiming.CLOCK_HOLD);
        }

        public void OnPause(LinkMember member, PauseMessage message, double now)
        {
            var gm = _song.GameManager;
            if (!_song.IsFor(gm) || !_song.IsFromPlay(member, message)) return;

            bool playing = gm.IsSongStarted && !gm.PlayerHasFailed && (!gm.Paused || gm.Rewinding);
            bool wasActive = _pause.Active;

            var result = _pause.OnRemotePause(member.Id, message.Epoch, message.Time, message.Ready, playing);
            if (result == PauseCoordinator.RemotePauseResult.Ignored) return;

            if (!wasActive)
            {
                YargLogger.LogInfo($"Link pause: {member.Name} paused at {message.Time:0.000}s");
                ToastManager.ToastInformation($"{member.Name} paused");
            }
            if (result == PauseCoordinator.RemotePauseResult.PauseUs)
            {
                _resumeAt = null;
                PauseFromLink(gm);
            }

            TryResumeAsLeader(now);
        }

        public void OnResume(LinkMember member, ResumeMessage message, double now)
        {
            var gm = _song.GameManager;
            if (!_song.IsFor(gm) || !_song.IsFromPlay(member, message)) return;

            // Only the host resumes everyone
            if (_song.IsHost || !member.IsHost || !_song.HostInPlay) return;
            if (!_pause.OnRemoteResume(message.Epoch)) return;

            double? oneWay = member.Peer != null && _song.Session.GetStats(member).Rtt is { } rtt
                ? rtt.TotalSeconds / 2 : null;
            double at = ResumeTiming.ScheduleAt(now, message.DelayMs, oneWay);
            YargLogger.LogInfo($"Link pause: {member.Name} resumes everyone from {message.Time:0.000}s " +
                $"in {(at - now) * 1000:0} ms");
            Schedule(at, message.Time);
        }

        /// <summary>The members, other than us, whose players aren't ready to resume yet.</summary>
        public List<string> WaitingOn() => _pause.WaitingOn(_song.Participants());

        /// <summary>Whether our players are ready to resume.</summary>
        public bool LocalReady => _pause.LocalReady;
    }
}
