using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;
using YARG.Menu;
using YARG.Menu.Data;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Leaving a synced song, and what a joiner does when the host is gone from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host runs the song for everyone, and there's no host migration yet. So when the host backs out to the
    /// library with friends still playing, the pause menu asks whether to take everyone along
    /// (<see cref="EndSongMessage"/>, which sends every joiner back to the library too) or to leave alone.
    /// </para>
    /// <para>
    /// A joiner whose host is gone from the song sees a countdown and goes back to the library when it runs out
    /// (<see cref="HostLossTracker"/>).
    /// </para>
    /// <para>
    /// Unlike the rest of a song's state (<see cref="SyncedSong"/>), the countdown outlives the session: when the
    /// session ends during a song, it must go on until it runs out or we leave the song.
    /// </para>
    /// </remarks>
    internal sealed class HostLoss
    {
        private readonly HostLossTracker _tracker = new();

        /// <summary>The song the countdown is for.</summary>
        private GameManager _song;

        /// <summary>The countdown, while it's shown: why the host is gone, and when we go back to the library.</summary>
        public HostLossTracker Countdown => _tracker.Watching && _song != null ? _tracker : null;

        /// <summary>The countdown, shown or not, for the debug overlay.</summary>
        public HostLossTracker Tracker => _tracker;

        public void Clear()
        {
            _tracker.Clear();
            _song = null;
        }

        /// <summary>
        /// Call every frame, also when there is no session or song: the countdown of a song whose session
        /// ended goes on.
        /// </summary>
        public void Update(LinkSession session, SyncedSong song, double now)
        {
            // The song ended, or we left it (a destroyed GameManager compares equal to null)
            if (_tracker.Watching && (_song == null || GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay))
            {
                Clear();
            }

            // A song we're playing without the host (we restarted alone, say) carries on whatever it does
            var gm = song?.GameManager;
            if (session is { Role: LinkRole.Join } && song != null && song.IsFor(gm) && gm.IsSongStarted &&
                song.HostInPlay)
            {
                var host = song.Host;
                if (!session.IsConnected || host == null)
                {
                    Watch(gm, "Lost the connection to the host", HostLossTracker.LOST_CONNECTION, now);
                }
                else if (host.Scene != LinkStatus.SCENE_GAMEPLAY ||
                    song.Friends.TryGetValue(host.Id, out var hostSetup) && hostSetup.Left)
                {
                    Watch(gm, $"{host.Name} left the song", HostLossTracker.HOST_LEFT, now);
                }
                else if (song.Departures.Tracker.SilentSince(host.Id, now) is { } silent)
                {
                    // The transport hasn't noticed yet, but the host's inputs stopped
                    Watch(gm, "Lost the connection to the host", HostLossTracker.LOST_CONNECTION, now, silent);
                }
                else if (_tracker.Watching)
                {
                    if (_tracker.Clear())
                    {
                        YargLogger.LogInfo("Link: the host is back in the song");
                        ToastManager.ToastInformation($"{host.Name} is back");
                    }
                    _song = null;
                }
            }

            if (!_tracker.Watching) return;

            if (_tracker.TryAnnounce(now))
            {
                YargLogger.LogInfo($"Link: {_tracker.Reason}, returning to the library in " +
                    $"{_tracker.Deadline - now:0}s unless the host is back");
                ToastManager.ToastWarning(_tracker.Reason);
            }

            string reason = _tracker.Reason;
            if (_tracker.TryExpire(now))
            {
                var expired = _song;
                Clear();

                YargLogger.LogInfo($"Link: {reason}, returning to the library");
                ToastManager.ToastWarning($"{reason}, so the song can't go on. Back to the library.");
                ReturnToLibrary(expired);
            }
        }

        /// <summary>
        /// The session closed for good while we were a joiner in a synced song. Call before the song is dropped.
        /// </summary>
        public void OnSessionEnded(LinkSession session, SyncedSong song, string reason, double now)
        {
            var gm = song?.GameManager;
            if (session is not { Role: LinkRole.Join } || song == null || !song.IsFor(gm) || !gm.IsSongStarted) return;
            if (!song.HostInPlay) return;

            // Nobody can come back to this one
            Watch(gm, reason, HostLossTracker.HOST_LEFT, now);
        }

        private void Watch(GameManager gm, string reason, double timeout, double now, double? since = null)
        {
            if (_song != gm)
            {
                _tracker.Clear();
                _song = gm;
            }

            if (_tracker.Watch(reason, timeout, now, since))
            {
                ToastManager.ToastWarning(reason);
            }
        }

        /// <summary>
        /// Called by the pause menu's "Back To Library". When we host a synced song friends are still playing,
        /// ask whether to take them along.
        /// </summary>
        /// <returns>Whether we took over (the dialog is open), so the pause menu shouldn't quit.</returns>
        public static bool OfferLeaveWithParty(SyncedSong song, GameManager gm)
        {
            if (song == null || !song.IsHost || !song.IsFor(gm) || !gm.IsSongStarted) return false;
            if (song.Participants().Count <= 1) return false;

            var dialogs = DialogManager.Instance;
            if (dialogs == null || dialogs.IsDialogShowing) return false;

            var dialog = dialogs.ShowMessage("Leave the song?",
                "You're the host, and your friends can't carry on the song without you.\n\n" +
                "Leave with the party to take everyone back to the library, or leave alone and your friends " +
                "will go back on their own shortly.");

            dialog.ClearButtons();
            dialog.AddDialogButton("Menu.Common.Cancel", MenuData.Colors.BrightButton, dialogs.ClearDialog);
            dialog.AddDialogButton("Menu.Pause.Online.LeaveAlone", MenuData.Colors.CancelButton, () =>
            {
                dialogs.ClearDialog();
                YargLogger.LogInfo("Link: leaving the song alone");
                gm.ForceQuitSong();
            });
            dialog.AddDialogButton("Menu.Pause.Online.LeaveWithParty", MenuData.Colors.ConfirmButton, () =>
            {
                dialogs.ClearDialog();

                // The song may have ended while the dialog was open
                if (song.IsFor(gm))
                {
                    YargLogger.LogInfo("Link: leaving the song with the party");
                    song.Send(new EndSongMessage());
                }
                gm.ForceQuitSong();
            });

            return true;
        }

        /// <summary>Joiner: the host ended the song for everyone.</summary>
        public void OnEndSong(SyncedSong song, LinkMember member, EndSongMessage message)
        {
            var gm = song.GameManager;
            if (song.IsHost || !member.IsHost) return;
            if (!song.IsFor(gm) || !song.IsFromPlay(member, message)) return;

            YargLogger.LogInfo($"Link: {member.Name} ended the song for everyone");
            ToastManager.ToastInformation($"{member.Name} ended the song");
            Clear();
            ReturnToLibrary(gm);
        }

        /// <summary>Joiner: the host dropped us from the song, so our highway is gone for everyone else.</summary>
        public void OnDroppedUs(SyncedSong song, LinkMember host)
        {
            // Once rejoining a song is supported, the library is where to do it from
            YargLogger.LogInfo($"Link: {host.Name} dropped us from the song, returning to the library");
            ToastManager.ToastWarning("You lost the connection during the song and were dropped from it");
            Clear();
            ReturnToLibrary(song.GameManager);
        }

        /// <summary>
        /// Leave the song for the song library (not wherever the menus were before the song: a joiner may have
        /// been sent into it from the lobby).
        /// </summary>
        private static void ReturnToLibrary(GameManager gm)
        {
            MenuManager.OpenOnNextLoad(MenuManager.Menu.MusicLibrary);
            gm.ForceQuitSong();
        }
    }
}
