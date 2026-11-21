using System;
using Newtonsoft.Json.Linq;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Integration.Link.Session;

namespace YARG.Integration.Link
{
    /// <summary>
    /// The game data that games send each other during a synced song: setups, inputs, failing and leaving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each kind of game data is handled in the file of its feature: the band meter (BandMeter), pauses
    /// (PauseSync), the end of the song for everyone and drop-outs (HostLoss). <see cref="HandleGameData"/>
    /// sends each kind to its handler.
    /// </para>
    /// </remarks>
    public partial class LinkController
    {
        // Kinds of game data (GameDataMessage.Kind) that this file handles
        private const string KIND_SETUP    = "setup";
        private const string KIND_INPUTS   = "inputs";
        private const string KIND_FAILED   = "failed";
        private const string KIND_DECLINED = "declined";
        private const string KIND_LEFT     = "left";

        /// <summary>Send new inputs at most this often, in seconds (120 times a second).</summary>
        private const double INPUTS_INTERVAL_MIN = 1.0 / 120.0;

        /// <summary>
        /// Send at least this often, in seconds (30 times a second), so friends' tracks keep moving when nothing is
        /// pressed.
        /// </summary>
        private const double INPUTS_INTERVAL_MAX = 1.0 / 30.0;

        private void HandleGameData(LinkMember member, string kind, JObject data)
        {
            if (data == null) return;

            try
            {
                switch (kind)
                {
                    case KIND_SETUP:
                        var setup = RemoteSetup.Parse(member.Id, data);
                        _remoteSetups[member.Id] = setup;
                        YargLogger.LogInfo($"Link: got {setup.Feeds.Count} player(s) from {member.Name} for {setup.SongHash}");

                        // Starting again (a restart) leaves the play of theirs that's on our highways
                        if (_songSetups.TryGetValue(member.Id, out var current) && !current.Left)
                        {
                            current.Left = true;
                            YargLogger.LogInfo($"Link: {member.Name} started another song or play without us");
                        }
                        break;

                    case KIND_INPUTS:
                        if (IsFromSongPlay(member, data))
                        {
                            _songSetups[member.Id].Receive(data, Now);
                            OnClockSample(member, data);
                            NoteInputsFrom(member);
                        }
                        else if (_remoteSetups.TryGetValue(member.Id, out var pending) && pending.IsPlay(data))
                        {
                            pending.Receive(data, Now);
                        }
                        break;

                    case KIND_LEFT:
                        OnFriendLeftSong(member, data);
                        break;

                    case KIND_FAILED:
                        OnFriendFailed(member, data);
                        break;

                    case KIND_DECLINED:
                        OnFriendDeclined(member, data);
                        break;

                    case KIND_PLAYER_FAILED:
                    case KIND_REVIVED:
                    case KIND_METER:
                        OnBandMeterData(member, kind, data);
                        break;

                    case KIND_PAUSE:
                    case KIND_RESUME:
                        OnPauseData(member, kind, data);
                        break;

                    case KIND_END_SONG:
                        OnEndSongData(member, data);
                        break;

                    case KIND_DROPPED:
                        OnDroppedData(member, data);
                        break;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Link: bad {kind} data from {member.Name}");
            }
        }

        /// <summary>
        /// Our band meter ran out. Tell friends so they fail with us: once we pause, our inputs stop
        /// and their mirror of our track would just freeze.
        /// </summary>
        public void ReportSongFailed(GameManager gameManager)
        {
            if (_session == null || _shared == null || _shared.GameManager != gameManager) return;

            YargLogger.LogInfo("Link: we failed the song, telling friends");
            _session.Broadcast(KIND_FAILED, new JObject
            {
                ["song"] = _shared.SongHash,
                ["play"] = _shared.PlayId,
                ["time"] = gameManager.SongTime,
            });
        }

        private void OnFriendFailed(LinkMember member, JObject data)
        {
            var gm = _shared?.GameManager;
            if (gm == null || !IsFromSongPlay(member, data)) return;
            if (!gm.IsSongStarted || gm.PlayerHasFailed) return;

            YargLogger.LogInfo($"Link: {member.Name} failed the song at {(double?) data["time"] ?? 0:0.00}s, failing with them");
            gm.FailFromLink();
        }

        /// <summary>
        /// Called by <see cref="GameManager"/> as it's destroyed, which is how every song ends: finished, quit,
        /// restarted, or switched to practice. Restarting reloads gameplay without leaving it, so the scene alone
        /// can't tell friends we've left this play of the song.
        /// </summary>
        public void OnSongExited(GameManager gm)
        {
            if (_session == null || _shared == null || _shared.GameManager != gm) return;

            YargLogger.LogInfo("Link: we left the synced song");
            _session.Broadcast(KIND_LEFT, new JObject
            {
                ["song"] = _shared.SongHash,
                ["play"] = _shared.PlayId,
            });
            EndSharedSong();
        }

        /// <summary>A friend left their play of a song: the one on our highways, or one we haven't joined.</summary>
        private void OnFriendLeftSong(LinkMember member, JObject data)
        {
            if (IsFromSongPlay(member, data))
            {
                _songSetups[member.Id].Left = true;
            }

            // Nobody can join a play that's over
            if (_remoteSetups.TryGetValue(member.Id, out var pending) && pending.IsPlay(data))
            {
                _remoteSetups.Remove(member.Id);
            }
        }

        /// <summary>
        /// Whether <paramref name="data"/> is from <paramref name="member"/>'s play of the synced song we're in:
        /// the one whose setup we added. Anything else (a play they started again, or one for a song we started
        /// alone) isn't about our song, even with the same song hash.
        /// </summary>
        private bool IsFromSongPlay(LinkMember member, JObject data) =>
            _shared != null && member != null && (string) data["song"] == _shared.SongHash &&
            _songSetups.TryGetValue(member.Id, out var setup) && !setup.Left && setup.IsPlay(data);

        /// <summary>
        /// Drop per-song state once we've left gameplay. <see cref="OnSongExited"/> usually has already.
        /// </summary>
        private void CheckSongEnded()
        {
            if (GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay) return;
            if (_shared == null) return;

            EndSharedSong();
        }

        /// <summary>
        /// Forget the synced song we were in. Setups that were used are done with; ones still waiting for us to
        /// join are kept.
        /// </summary>
        private void EndSharedSong()
        {
            _shared = null;
            _songSetups.Clear();
            _friendGoneSince.Clear();
            _droppedFriends.Clear();
            _lastInputsAt.Clear();
            EndBandMeter();
            EndClockSync();
            EndPauseSync();
        }

        private void SendInputs()
        {
            var gm = _shared.GameManager;
            if (gm == null || !gm.IsSongStarted || _waiting)
            {
                return;
            }

            double sinceLast = Now - _shared.LastSendTime;
            if (sinceLast < INPUTS_INTERVAL_MIN)
            {
                return;
            }

            bool anyNew = false;
            foreach (var player in _shared.Players)
            {
                if (player != null && player.ReplayInputs.Count > _shared.SentCounts[player])
                {
                    anyNew = true;
                    break;
                }
            }

            if (!anyNew && sinceLast < INPUTS_INTERVAL_MAX)
            {
                return;
            }

            _shared.LastSendTime = Now;

            // Reliable: each message only carries the inputs since the last one
            _session.Broadcast(KIND_INPUTS, _shared.CreateInputsMessage(gm));
        }

        /// <summary>
        /// Game modes whose players can be shown as a highway on friends' screens.
        /// </summary>
        private static bool CanMirror(GameMode mode)
        {
            // Vocals share one track and pro guitar has no replay support
            return mode is not (GameMode.Vocals or GameMode.ProGuitar);
        }
    }
}
