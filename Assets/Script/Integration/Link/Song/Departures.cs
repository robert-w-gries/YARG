using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Friends who left the synced song or lost their connection drop out of it: their highway lowers and their
    /// engine leaves the band's star power, unisons and meter. Their score so far still counts. Refer to
    /// <see cref="DepartureTracker"/> for who decides and when.
    /// </summary>
    /// <remarks>
    /// A friend who was dropped while their connection was down hears it when they reconnect
    /// (<see cref="ResendDropOuts"/>) and goes back to the library, since their highway is gone for everyone else.
    /// </remarks>
    internal sealed class Departures
    {
        private readonly SyncedSong _song;

        /// <summary>Host: someone joined, so repeat the drop-outs (see <see cref="ResendDropOuts"/>).</summary>
        private bool _resendDropOuts;

        public Departures(SyncedSong song)
        {
            _song = song;
        }

        public DepartureTracker Tracker { get; } = new();

        /// <summary>
        /// Host: someone (re)joined the session mid-song. Tell them who's been dropped, which may be them: a
        /// friend whose connection came back after we gave up on them.
        /// </summary>
        /// <remarks>
        /// Sent from the next <see cref="Update"/>, not now: the session sends the newcomer its roster after this
        /// event, and a joiner ignores game data from a host it doesn't know yet.
        /// </remarks>
        public void ResendDropOuts()
        {
            if (_song.IsHost) _resendDropOuts = true;
        }

        /// <summary>Inputs arrived from <paramref name="member"/>'s play of this song.</summary>
        public void NoteInputs(LinkMember member, double now)
        {
            if (_song.IsFor(_song.GameManager))
            {
                Tracker.NoteInputs(member.Id, now);
            }
        }

        /// <summary>Call every frame.</summary>
        public void Update(double now)
        {
            var gm = _song.GameManager;
            if (!_song.IsFor(gm) || !gm.IsSongStarted) return;

            bool host = _song.IsHost;
            if (host && _resendDropOuts)
            {
                _resendDropOuts = false;
                foreach (string dropped in Tracker.Dropped)
                {
                    SendDropOut(dropped);
                }
            }

            foreach (var player in gm.Players)
            {
                if (player.Player.RemoteFeed is not { } feed || !player.IsActive) continue;

                string id = feed.Member;
                if (Tracker.IsDropped(id)) continue;

                var member = _song.FindMember(id);
                bool disconnected = member == null || !_song.Session.IsConnected;
                // Out of gameplay, or onto another play of the song (a restart) while staying in it
                bool left = !disconnected && (member.Scene != LinkStatus.SCENE_GAMEPLAY ||
                    _song.Friends.TryGetValue(id, out var setup) && setup.Left);

                var friend = new DepartureTracker.Friend(id, disconnected, left, member?.IsHost ?? false);
                var departure = Tracker.Check(friend, host, now);
                if (departure == DepartureTracker.Departure.None) continue;

                DropOut(gm, id, member?.Name,
                    departure == DepartureTracker.Departure.Left ? "left the song" : "lost the connection");
                if (host)
                {
                    SendDropOut(id);
                }
            }
        }

        private void SendDropOut(string member) => _song.Send(new DroppedMessage { Member = member });

        /// <summary>Joiner: the host dropped a friend's players, or ours.</summary>
        /// <returns>Whether the host dropped us: our highway is gone for everyone else.</returns>
        public bool OnDropped(LinkMember member, DroppedMessage message)
        {
            var gm = _song.GameManager;
            if (_song.IsHost || !member.IsHost) return false;
            if (!_song.IsFor(gm) || !_song.IsFromPlay(member, message)) return false;

            string id = message.Member;
            if (id == _song.Session.LocalMemberId) return true;

            if (!Tracker.IsDropped(id))
            {
                DropOut(gm, id, _song.FindMember(id)?.Name, "was dropped by the host");
            }
            return false;
        }

        private void DropOut(GameManager gm, string id, string name, string why)
        {
            Tracker.MarkDropped(id);

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
    }
}
