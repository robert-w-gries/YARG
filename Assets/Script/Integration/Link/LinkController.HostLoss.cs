using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.Logging;
using System.Collections.Generic;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YARG.Integration.Link.Session;
using YARG.Menu;
using YARG.Menu.Data;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Leaving a synced song, and what joiners do when the host is gone from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host runs the song for everyone (resuming a synced pause, the band's meter), and there's no host
    /// migration yet. So when the host backs out to the library with friends still playing, the pause menu
    /// asks whether to take everyone along (<c>endsong</c>, which sends every joiner back to the library too)
    /// or to leave alone.
    /// </para>
    /// <para>
    /// A joiner whose host is gone from the song (it left alone, the connection dropped, or the session ended)
    /// can't carry on for long: a pause would never resume. After a short grace (the host finishing the song a
    /// moment before us isn't leaving it), the joiner sees a countdown and goes back to the library when it
    /// runs out. A lost connection ends the song after <see cref="LOST_CONNECTION_DROP"/>, the same time the
    /// host takes to drop the joiner, so the joiner doesn't wait to reconnect only to be dropped.
    /// </para>
    /// </remarks>
    public partial class LinkController
    {
        private const string KIND_END_SONG = "endsong";
        private const string KIND_DROPPED  = "dropped";

        /// <summary>Don't count the host as gone for this long: it may have just finished the song.</summary>
        private const double HOST_GONE_GRACE = 2.0;

        /// <summary>The host left the song (or ended the session): it isn't coming back to it.</summary>
        private const double HOST_LEFT_TIMEOUT = 10.0;

        /// <summary>
        /// How long a lost connection lasts mid-song before it's the end of the song for that player. Both sides
        /// use it: the host drops a friend it lost this long ago, and a joiner who lost the host this long ago
        /// leaves by itself, rather than reconnecting (which can take a while) only to hear it was dropped.
        /// Each side counts from the other's last inputs (see <see cref="INPUTS_SILENCE"/>), or from when the
        /// transport reported the loss if that came first, so whichever gives up first, both end up agreeing.
        /// </summary>
        private const double LOST_CONNECTION_DROP = 5.0;

        private sealed class HostGone
        {
            public GameManager GameManager;
            public string      Reason;
            public double      Since;
            public double      Deadline;
            public bool        Announced;
        }

        /// <summary>Joiner: the synced song we're in has lost its host. Outlives the session, if that ended.</summary>
        private HostGone _hostGone;

        private GUIStyle _hostGoneStyle;

        /// <summary>A friend who left the song (it may have just ended for them a moment before us).</summary>
        private const double FRIEND_LEFT_GRACE = 2.0;

        /// <summary>When each friend with players on a highway was first seen gone from the song (member ID).</summary>
        private readonly Dictionary<string, double> _friendGoneSince = new();

        /// <summary>Friends whose players we've dropped out of this song.</summary>
        private readonly HashSet<string> _droppedFriends = new();

        /// <summary>Host: someone joined, so repeat the drop-outs (see <see cref="ResendDropOuts"/>).</summary>
        private bool _resendDropOuts;

        /// <summary>
        /// A friend we've heard inputs from but not for this long has lost their connection, whatever the
        /// transport says. Games in a song send inputs at least 30 times a second, paused or not (and keep
        /// running when tabbed out), while the transport can take half a minute to notice a game that crashed
        /// or a network that died.
        /// </summary>
        private const double INPUTS_SILENCE = RemoteInputFeed.STALL_SECONDS;

        /// <summary>When inputs last arrived from each member during this song.</summary>
        private readonly Dictionary<string, double> _lastInputsAt = new();

        /// <summary>
        /// Called by the pause menu's "Back To Library". When we host a synced song friends are still playing,
        /// ask whether to take them along.
        /// </summary>
        /// <returns>Whether we took over (the dialog is open), so the pause menu shouldn't quit.</returns>
        public bool OfferLeaveWithParty(GameManager gm)
        {
            if (_session is not { Role: LinkRole.Host } || !IsSyncedSong(gm) || !gm.IsSongStarted) return false;
            if (SongParticipants().Count <= 1) return false;

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
                LeaveSongWithParty(gm);
            });

            return true;
        }

        /// <summary>Host only: end the song for everyone, and go back to the library.</summary>
        private void LeaveSongWithParty(GameManager gm)
        {
            if (_session is { Role: LinkRole.Host } && _shared != null && _shared.GameManager == gm)
            {
                YargLogger.LogInfo("Link: leaving the song with the party");
                _session.Broadcast(KIND_END_SONG, new JObject
                {
                    ["song"] = _shared.SongHash,
                    ["play"] = _shared.PlayId,
                });
            }

            gm.ForceQuitSong();
        }

        private void OnEndSongData(LinkMember member, JObject data)
        {
            var gm = _shared?.GameManager;
            if (_session is not { Role: LinkRole.Join } || !member.IsHost) return;
            if (!IsSyncedSong(gm) || !IsFromSongPlay(member, data)) return;

            YargLogger.LogInfo($"Link: {member.Name} ended the song for everyone");
            ToastManager.ToastInformation($"{member.Name} ended the song");
            _hostGone = null;
            ReturnToLibrary(gm);
        }

        /// <summary>
        /// The session closed for good while we were a joiner in a synced song. Call before <see cref="Close"/>,
        /// which forgets the song.
        /// </summary>
        private void OnSessionEndedInSong(string reason)
        {
            var gm = _shared?.GameManager;
            if (_session is not { Role: LinkRole.Join } || !IsSyncedSong(gm) || !gm.IsSongStarted) return;
            if (!_shared.HostInPlay) return;

            // Nobody can come back to this one
            WatchHostGone(gm, reason, HOST_GONE_GRACE + HOST_LEFT_TIMEOUT);
        }

        /// <summary>
        /// Call every frame. A friend's mirrored engine only runs as far as their inputs have arrived, so once
        /// they're gone it freezes mid-song (notes scroll by unplayed, multiplier stuck, unisons waiting on
        /// them). Drop their players out instead, like a local player dropping out: the highway lowers and
        /// their engine leaves the band's star power, unisons and meter. Their score so far still counts.
        /// </summary>
        /// <remarks>
        /// The host decides, and tells everyone (<c>dropped</c>), so every game drops the same friends. A friend
        /// who was dropped while their connection was down hears it when they reconnect (see
        /// <see cref="ResendDropOuts"/>) and goes back to the library, since their highway is gone for everyone
        /// else. A joiner only decides about the host itself, once it has left the song: losing the connection
        /// to the host is the host-gone countdown's business.
        /// </remarks>
        private void UpdateDepartedFriends()
        {
            var gm = _shared?.GameManager;
            if (!IsSyncedSong(gm) || !gm.IsSongStarted)
            {
                _friendGoneSince.Clear();
                _droppedFriends.Clear();
                _lastInputsAt.Clear();
                return;
            }

            bool host = _session.Role == LinkRole.Host;
            if (host && _resendDropOuts)
            {
                _resendDropOuts = false;
                foreach (string dropped in _droppedFriends)
                {
                    BroadcastDropOut(dropped);
                }
            }

            foreach (var player in gm.Players)
            {
                if (player.Player.RemoteFeed is not { } feed || !player.IsActive) continue;

                string id = feed.Member;
                if (_droppedFriends.Contains(id)) continue;

                var member = FindMember(id);
                bool disconnected = member == null || !_session.IsConnected;
                // Out of gameplay, or onto another play of the song (a restart) while staying in it
                bool left = !disconnected &&
                    (member.Scene != SCENE_GAMEPLAY || _songSetups.TryGetValue(id, out var setup) && setup.Left);
                double? silentSince = disconnected || left ? null : SilentSince(id);
                bool lost = disconnected || silentSince.HasValue;

                bool gone = host ? lost || left : left && member.IsHost;
                if (!gone)
                {
                    _friendGoneSince.Remove(id);
                    continue;
                }

                // A friend who went quiet has been gone since their last inputs, not since we noticed
                double start = silentSince ?? Now;
                if (!_friendGoneSince.TryGetValue(id, out double since) || start < since)
                {
                    _friendGoneSince[id] = since = start;
                }

                if (Now - since < (left ? FRIEND_LEFT_GRACE : LOST_CONNECTION_DROP)) continue;

                _friendGoneSince.Remove(id);
                DropOutFriend(gm, id, member?.Name, left ? "left the song" : "lost the connection");
                if (host)
                {
                    BroadcastDropOut(id);
                }
            }
        }

        /// <summary>Inputs arrived from <paramref name="member"/>.</summary>
        private void NoteInputsFrom(LinkMember member)
        {
            if (IsSyncedSong(_shared?.GameManager))
            {
                _lastInputsAt[member.Id] = Now;
            }
        }

        /// <summary>
        /// When <paramref name="member"/>'s inputs stopped, if they've been quiet for <see cref="INPUTS_SILENCE"/>.
        /// <c>null</c> if they're still sending, or never have.
        /// </summary>
        private double? SilentSince(string member)
        {
            return _lastInputsAt.TryGetValue(member, out double last) && Now - last >= INPUTS_SILENCE
                ? last : null;
        }

        private void BroadcastDropOut(string member)
        {
            _session.Broadcast(KIND_DROPPED, new JObject
            {
                ["song"]   = _shared.SongHash,
                ["play"]   = _shared.PlayId,
                ["member"] = member,
            });
        }

        /// <summary>
        /// Host: someone (re)joined the session mid-song. Tell them who's been dropped, which may be them: a
        /// friend whose connection came back after we gave up on them.
        /// </summary>
        /// <remarks>
        /// Sent from the next <see cref="UpdateDepartedFriends"/>, not now: the session sends the newcomer its
        /// roster after this event, and a joiner ignores game data from a host it doesn't know yet.
        /// </remarks>
        private void ResendDropOuts()
        {
            if (_session is { Role: LinkRole.Host }) _resendDropOuts = true;
        }

        /// <summary>Joiner: the host dropped a friend's players, or ours.</summary>
        private void OnDroppedData(LinkMember member, JObject data)
        {
            var gm = _shared?.GameManager;
            if (_session is not { Role: LinkRole.Join } || !member.IsHost) return;
            if (!IsSyncedSong(gm) || !IsFromSongPlay(member, data)) return;

            string id = (string) data["member"];
            if (string.IsNullOrEmpty(id)) return;

            if (id == _session.LocalMemberId)
            {
                // Our highway is gone for everyone else, so there's no carrying on. Once rejoining a song is
                // supported, the library is where to do it from.
                YargLogger.LogInfo($"Link: {member.Name} dropped us from the song, returning to the library");
                ToastManager.ToastWarning("You lost the connection during the song and were dropped from it");
                _hostGone = null;
                ReturnToLibrary(gm);
                return;
            }

            if (_droppedFriends.Contains(id)) return;
            DropOutFriend(gm, id, FindMember(id)?.Name, "was dropped by the host");
        }

        private void DropOutFriend(GameManager gm, string id, string name, string why)
        {
            _droppedFriends.Add(id);
            _friendGoneSince.Remove(id);

            bool any = false;
            foreach (var player in gm.Players)
            {
                if (player.Player.RemoteFeed is not { } feed || feed.Member != id) continue;

                name ??= feed.Name;
                if (player is TrackPlayer { IsActive: true } track)
                {
                    track.DropOutRemote();
                    any = true;
                }
            }

            if (!any) return;
            YargLogger.LogInfo($"Link: {name} {why}, dropping out their players");
            ToastManager.ToastInformation($"{name} dropped out of the song");
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

        /// <summary>Call every frame, even without a session.</summary>
        private void UpdateHostLoss()
        {
            // The song ended, or we left it (a destroyed GameManager compares equal to null)
            if (_hostGone != null &&
                (_hostGone.GameManager == null || GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay))
            {
                _hostGone = null;
            }

            // A song we're playing without the host (we restarted alone, say) carries on whatever it does
            var gm = _shared?.GameManager;
            if (_session is { Role: LinkRole.Join } && IsSyncedSong(gm) && gm.IsSongStarted && _shared.HostInPlay)
            {
                var host = HostMember();
                if (!_session.IsConnected || host == null)
                {
                    WatchHostGone(gm, "Lost the connection to the host", LOST_CONNECTION_DROP);
                }
                else if (host.Scene != SCENE_GAMEPLAY ||
                    _songSetups.TryGetValue(host.Id, out var hostSetup) && hostSetup.Left)
                {
                    WatchHostGone(gm, $"{host.Name} left the song", HOST_GONE_GRACE + HOST_LEFT_TIMEOUT);
                }
                else if (SilentSince(host.Id) is { } silent)
                {
                    // The transport hasn't noticed yet, but the host's inputs stopped
                    WatchHostGone(gm, "Lost the connection to the host", LOST_CONNECTION_DROP, silent);
                }
                else if (_hostGone != null)
                {
                    if (_hostGone.Announced)
                    {
                        YargLogger.LogInfo("Link: the host is back in the song");
                        ToastManager.ToastInformation($"{host.Name} is back");
                    }
                    _hostGone = null;
                }
            }

            if (_hostGone == null) return;

            if (!_hostGone.Announced && Now >= _hostGone.Since + HOST_GONE_GRACE)
            {
                _hostGone.Announced = true;
                YargLogger.LogInfo($"Link: {_hostGone.Reason}, returning to the library in " +
                    $"{_hostGone.Deadline - Now:0}s unless the host is back");
                ToastManager.ToastWarning(_hostGone.Reason);
            }

            if (Now >= _hostGone.Deadline)
            {
                var song = _hostGone.GameManager;
                string reason = _hostGone.Reason;
                _hostGone = null;

                YargLogger.LogInfo($"Link: {reason}, returning to the library");
                ToastManager.ToastWarning($"{reason}, so the song can't go on. Back to the library.");
                ReturnToLibrary(song);
            }
        }

        /// <summary>
        /// Start (or update) the countdown for a song whose host is gone, ending <paramref name="timeout"/> after
        /// <paramref name="since"/> (by default, now).
        /// </summary>
        private void WatchHostGone(GameManager gm, string reason, double timeout, double? since = null)
        {
            double start = since ?? Now;
            double deadline = start + timeout;

            if (_hostGone == null || _hostGone.GameManager != gm)
            {
                _hostGone = new HostGone
                {
                    GameManager = gm,
                    Reason      = reason,
                    Since       = start,
                    Deadline    = deadline,
                };
                return;
            }

            if (_hostGone.Reason == reason) return;

            // Say so if the reason changes (the connection came back, but the host had left the song), and
            // never push the deadline later
            _hostGone.Reason = reason;
            _hostGone.Deadline = Math.Min(_hostGone.Deadline, deadline);
            if (_hostGone.Announced)
            {
                ToastManager.ToastWarning(reason);
            }
        }

        private LinkMember HostMember()
        {
            if (_session == null) return null;
            foreach (var member in _session.Members)
            {
                if (member.IsHost) return member;
            }
            return null;
        }

        /// <summary>The countdown over the song, once the host has been gone a while.</summary>
        /// <returns>Whether it's showing (so the pause status isn't drawn over it).</returns>
        private bool DrawHostGone()
        {
            if (_hostGone is not { Announced: true } || _hostGone.GameManager == null) return false;

            int seconds = Mathf.Max(0, Mathf.CeilToInt((float) (_hostGone.Deadline - Now)));
            string text = $"<b>{Escape(_hostGone.Reason)}</b>\n" +
                $"Returning to the library in {seconds}s. You can leave now from the pause menu.";

            float scale = Screen.height / 1080f;
            _hostGoneStyle ??= new GUIStyle(GUI.skin.label)
            {
                richText = true,
                wordWrap = false,
                alignment = TextAnchor.MiddleCenter,
            };
            _hostGoneStyle.fontSize = Mathf.RoundToInt(24 * scale);

            var content = new GUIContent(text);
            var size = _hostGoneStyle.CalcSize(content);
            float pad = 14 * scale;
            var box = new Rect((Screen.width - size.x) / 2 - pad, 40 * scale, size.x + pad * 2, size.y + pad * 2);

            var oldColor = GUI.color;
            GUI.color = new Color(0.25f, 0f, 0f, 0.8f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = oldColor;
            GUI.Label(new Rect(box.x + pad, box.y + pad, size.x, size.y), content, _hostGoneStyle);
            return true;
        }
    }
}
