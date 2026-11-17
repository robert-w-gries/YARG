using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Pausing together during a synced song.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When anyone pauses (the pause menu, losing focus, a controller disconnecting), they send a <c>pause</c>
    /// with their input time, and every other game pauses too and sends its own. Pressing resume doesn't
    /// resume: it marks that player ready (<c>pause</c> again, with <c>ready</c>) and closes their menu.
    /// Pausing again, or pressing resume again, reopens the menu and takes it back.
    /// </para>
    /// <para>
    /// The host resumes everyone once the host and every friend in the song are ready. It picks one input time
    /// a little before the latest pause (<see cref="PauseCoordinator"/>) and sends <c>resume</c> with that time
    /// and a lead (<see cref="RESUME_LEAD"/>). Each game, joiners allowing for half the round trip, rewinds to
    /// that time and plays on at the same moment, the way a synced start does. Clock correction takes care of
    /// the last few milliseconds.
    /// </para>
    /// <para>
    /// A failed song's pause menu isn't synced: failing is (<c>failed</c>), and the band's meter decides it.
    /// </para>
    /// </remarks>
    public partial class LinkController
    {
        private const string KIND_PAUSE  = "pause";
        private const string KIND_RESUME = "resume";

        public const string STATUS_PAUSED = "paused";

        /// <summary>
        /// How far ahead the host schedules a resume, which has to cover the trip to every joiner and the
        /// rewind animation.
        /// </summary>
        private const double RESUME_LEAD = 1.5;

        /// <summary>The longest lead we accept from a host, so a bad value can't hold us paused for long.</summary>
        private const double MAX_RESUME_LEAD = 10.0;

        /// <summary>
        /// How long the resume's rewind animation runs before the song plays (<c>SongRunner.RewindAndResume</c>),
        /// so it can start this early and finish on time.
        /// </summary>
        private const double REWIND_ANIMATION = 0.5;

        /// <summary>
        /// Ignore friends' clocks for this long after a synced resume: their first inputs messages can still
        /// carry the paused time.
        /// </summary>
        private const double CLOCK_HOLD_AFTER_RESUME = 1.0;

        private PauseCoordinator _pause;

        /// <summary>When to resume (on <see cref="Now"/>), and from what input time, once scheduled.</summary>
        private double? _resumeAt;
        private double  _resumeInputTime;

        /// <summary>We're pausing our game because a friend paused, not because our player did.</summary>
        private bool _pausingFromLink;

        /// <summary>We're resuming our game for a synced resume, so <see cref="HoldResume"/> lets it through.</summary>
        private bool _resumingFromLink;

        private double _clockHoldUntil;

        private GUIStyle _pauseStyle;

        private void StartPauseSync()
        {
            _pause = new PauseCoordinator(_session.LocalMemberId, GameManager.PAUSE_REWIND_LENGTH);
            _resumeAt = null;
            _clockHoldUntil = 0;
        }

        private void EndPauseSync()
        {
            _pause = null;
            _resumeAt = null;
        }

        /// <summary>The game manager of the synced song we're in, if <paramref name="gm"/> is it.</summary>
        private bool IsSyncedSong(GameManager gm) =>
            gm != null && _session != null && _pause != null && _shared != null && _shared.GameManager == gm &&
            !gm.IsPractice && !gm.IsReplay;

        /// <summary>Whether a synced pause is holding the song (for the lobby, and the HUD).</summary>
        public bool IsSyncedPauseActive => _pause is { Active: true } || _resumeAt.HasValue;

        /// <summary>
        /// Called by <see cref="GameManager"/> when it opens the pause menu, whatever opened it.
        /// </summary>
        public void OnLocalPause(GameManager gm)
        {
            if (!IsSyncedSong(gm) || !gm.IsSongStarted || gm.PlayerHasFailed) return;

            // Pausing during a resume's countdown takes it back; the others pause again when they hear
            _resumeAt = null;

            string pausedBy = _pausingFromLink ? _pause.PausedBy : null;
            bool started = _pause.OnLocalPause(gm.InputTime, pausedBy);
            if (started)
            {
                YargLogger.LogInfo($"Link pause: we paused at {gm.InputTime:0.000}s, pausing friends");
            }

            BroadcastPause();
        }

        /// <summary>
        /// Called by <see cref="GameManager.Resume"/>. In a synced pause, resuming marks us ready (or, if we
        /// were ready, reopens the pause menu) instead of resuming, and the host resumes everyone.
        /// </summary>
        /// <returns>Whether we took over, so the game shouldn't resume.</returns>
        public bool HoldResume(GameManager gm)
        {
            if (_resumingFromLink || !IsSyncedSong(gm) || gm.PlayerHasFailed) return false;

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
                gm.HidePauseMenu();
                YargLogger.LogInfo("Link pause: we're ready to resume");
            }
            else
            {
                gm.ShowPauseMenu();
                YargLogger.LogInfo("Link pause: we're not ready after all");
            }

            BroadcastPause();
            TryResumeAsHost();
            return true;
        }

        private void BroadcastPause()
        {
            if (_pause.LocalTime is not { } time) return;

            _session.Broadcast(KIND_PAUSE, new JObject
            {
                ["song"]  = _shared.SongHash,
                ["play"]  = _shared.PlayId,
                ["n"]     = _pause.Epoch,
                ["time"]  = time,
                ["ready"] = _pause.LocalReady,
            });
        }

        /// <summary>Call every frame.</summary>
        private void UpdatePauseSync()
        {
            var gm = _shared?.GameManager;
            if (!IsSyncedSong(gm)) return;

            if (_resumeAt is { } at && Now >= at - REWIND_ANIMATION)
            {
                ExecuteResume(gm);
                return;
            }

            if (!_pause.Active) return;

            // A friend paused, but we couldn't follow at the time (still loading, or mid-resume)
            if (_pause.LocalTime == null && gm.IsSongStarted && !gm.PlayerHasFailed)
            {
                PauseFromLink(gm);
            }

            // Someone leaving can leave everyone else ready
            TryResumeAsHost();
        }

        /// <summary>Pause our game because a friend paused.</summary>
        private void PauseFromLink(GameManager gm)
        {
            if (gm.Paused && !gm.Rewinding)
            {
                // Already paused (we paused at the same moment, or are waiting on a resume): just report it,
                // and show the menu if we'd closed it as ready
                _pausingFromLink = true;
                try
                {
                    OnLocalPause(gm);
                }
                finally
                {
                    _pausingFromLink = false;
                }
                gm.ShowPauseMenu();
                return;
            }

            _pausingFromLink = true;
            try
            {
                // Pausing mid-rewind cancels the rewind, as it does for a player pausing then
                gm.Pause();
            }
            finally
            {
                _pausingFromLink = false;
            }
        }

        /// <summary>
        /// Whoever runs this play of the song resumes it: the host, or a joiner playing it without the host
        /// (one that restarted alone, say), who would otherwise wait for a resume that never comes.
        /// </summary>
        private bool LeadsSong => _session != null && _shared != null && !_shared.HostInPlay;

        /// <summary>Host (or <see cref="LeadsSong"/>) only: resume everyone if everyone is ready.</summary>
        private void TryResumeAsHost()
        {
            if (!LeadsSong || _pause is not { Active: true }) return;

            var plan = _pause.TryResume(SongParticipants());
            if (plan is not { } p) return;

            YargLogger.LogInfo($"Link pause: everyone's ready, resuming from {p.InputTime:0.000}s in {RESUME_LEAD:0.0}s");
            _session.Broadcast(KIND_RESUME, new JObject
            {
                ["song"]    = _shared.SongHash,
                ["play"]    = _shared.PlayId,
                ["n"]       = p.Epoch,
                ["time"]    = p.InputTime,
                ["delayMs"] = (long) Math.Round(RESUME_LEAD * 1000),
            });
            ScheduleResume(Now + RESUME_LEAD, p.InputTime);
        }

        private void ScheduleResume(double at, double inputTime)
        {
            _resumeAt = at;
            _resumeInputTime = inputTime;
        }

        private void ExecuteResume(GameManager gm)
        {
            _resumeAt = null;
            if (!gm.Paused || gm.PlayerHasFailed) return;

            double rewind = gm.InputTime - _resumeInputTime;
            YargLogger.LogInfo($"Link pause: resuming with friends from {_resumeInputTime:0.000}s " +
                $"(rewinding {rewind:0.000}s from our pause)");

            _resumingFromLink = true;
            try
            {
                gm.Resume(rewind);
            }
            finally
            {
                _resumingFromLink = false;
            }

            _clockHoldUntil = Now + REWIND_ANIMATION + CLOCK_HOLD_AFTER_RESUME;
        }

        /// <summary>
        /// Everyone playing the synced song, us included: the members whose setups we added, while they're
        /// still in gameplay (one who quit to the menu isn't waited for).
        /// </summary>
        private List<string> SongParticipants()
        {
            var members = new List<string> { _session.LocalMemberId };
            foreach (var (member, setup) in _songSetups)
            {
                if (!setup.Left && FindMember(member) is { Scene: SCENE_GAMEPLAY })
                {
                    members.Add(member);
                }
            }
            return members;
        }

        private void OnPauseData(LinkMember member, string kind, JObject data)
        {
            var gm = _shared?.GameManager;
            if (!IsSyncedSong(gm) || !IsFromSongPlay(member, data)) return;

            int epoch = (int?) data["n"] ?? -1;
            double time = (double?) data["time"] ?? double.NaN;
            if (epoch < 0 || !RemoteSetup.IsSaneTime(time)) return;

            switch (kind)
            {
                case KIND_PAUSE:
                {
                    bool ready = (bool?) data["ready"] ?? false;
                    bool playing = gm.IsSongStarted && !gm.PlayerHasFailed && (!gm.Paused || gm.Rewinding);
                    bool wasActive = _pause.Active;

                    var result = _pause.OnRemotePause(member.Id, epoch, time, ready, playing);
                    if (result == PauseCoordinator.RemotePauseResult.Ignored) return;

                    if (!wasActive)
                    {
                        YargLogger.LogInfo($"Link pause: {member.Name} paused at {time:0.000}s");
                        ToastManager.ToastInformation($"{member.Name} paused");
                    }
                    if (result == PauseCoordinator.RemotePauseResult.PauseUs)
                    {
                        _resumeAt = null;
                        PauseFromLink(gm);
                    }

                    TryResumeAsHost();
                    break;
                }

                case KIND_RESUME:
                {
                    // Only the host resumes everyone
                    if (_session.Role != LinkRole.Join || !member.IsHost || !_shared.HostInPlay) return;
                    if (!_pause.OnRemoteResume(epoch)) return;

                    double delay = Math.Clamp(((double?) data["delayMs"] ?? 0) / 1000.0, 0, MAX_RESUME_LEAD);
                    double oneWay = member.Peer != null && _session.GetStats(member).Rtt is { } rtt
                        ? rtt.TotalSeconds / 2 : 0;
                    YargLogger.LogInfo($"Link pause: {member.Name} resumes everyone from {time:0.000}s " +
                        $"in {(delay - oneWay) * 1000:0} ms");
                    ScheduleResume(Now + delay - oneWay, time);
                    break;
                }
            }
        }

        /// <summary>What the synced pause is waiting on, over the pause menu.</summary>
        private void DrawPauseStatus()
        {
            var gm = _shared?.GameManager;
            if (!IsSyncedSong(gm) || !IsSyncedPauseActive) return;

            var text = new StringBuilder();
            if (_resumeAt.HasValue)
            {
                text.Append("<b>Resuming together...</b>");
            }
            else
            {
                string by = _pause.PausedBy == _session.LocalMemberId ? "You"
                    : FindMember(_pause.PausedBy)?.Name ?? "A friend";
                text.Append($"<b>{Escape(by)} paused</b>");

                var waiting = _pause.WaitingOn(SongParticipants());
                var names = new List<string>();
                bool waitingOnHost = false;
                foreach (string id in waiting)
                {
                    if (id == _session.LocalMemberId) continue;
                    var member = FindMember(id);
                    if (member == null) continue;
                    if (member.IsHost) waitingOnHost = true;
                    else names.Add(Escape(member.Name));
                }

                text.Append('\n');
                if (!_pause.LocalReady)
                {
                    text.Append("Resume when you're ready. The song carries on once everyone is.");
                }
                else if (names.Count > 0)
                {
                    text.Append("You're ready. Pause again if you're not.");
                }
                else if (waitingOnHost)
                {
                    text.Append("Everyone's ready. Waiting for the host to resume.");
                }
                else
                {
                    text.Append("Everyone's ready.");
                }

                if (names.Count > 0)
                {
                    text.Append($"\n<color=#c0c0c0>Waiting for {string.Join(", ", names)}</color>");
                }
            }

            float scale = Screen.height / 1080f;
            _pauseStyle ??= new GUIStyle(GUI.skin.label)
            {
                richText = true,
                wordWrap = false,
                alignment = TextAnchor.MiddleCenter,
            };
            _pauseStyle.fontSize = Mathf.RoundToInt(24 * scale);

            var content = new GUIContent(text.ToString());
            var size = _pauseStyle.CalcSize(content);
            float pad = 14 * scale;
            var box = new Rect((Screen.width - size.x) / 2 - pad, 40 * scale, size.x + pad * 2, size.y + pad * 2);

            var oldColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = oldColor;
            GUI.Label(new Rect(box.x + pad, box.y + pad, size.x, size.y), content, _pauseStyle);
        }
    }
}
