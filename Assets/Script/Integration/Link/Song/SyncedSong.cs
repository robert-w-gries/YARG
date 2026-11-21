using System.Collections.Generic;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;

namespace YARG.Integration.Link
{
    /// <summary>
    /// One synced song, from its start until we leave it: our players (<see cref="Local"/>), our friends'
    /// players (<see cref="Friends"/>), and the features that keep the games together during the song.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="LinkController"/> makes one when the session starts a song, and drops it when we leave the
    /// song. All the state of a song is in this object and its features, so nothing outlives the song by
    /// accident. The exception is <see cref="HostLoss"/>, whose countdown outlives the session.
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="BandMeter"/>: the host's fail meter is the band's.</item>
    /// <item><see cref="Pause"/>: a pause pauses everyone.</item>
    /// <item><see cref="Clock"/>: the joiners' song clocks follow the host's.</item>
    /// <item><see cref="Departures"/>: friends who left the song or lost the connection drop out.</item>
    /// </list>
    /// </remarks>
    internal sealed class SyncedSong
    {
        public readonly LinkSession Session;
        public readonly InputSender Local;

        /// <summary>
        /// Joiner: the host is in this play of the song, so it runs the band's meter and resumes pauses. Not
        /// when we started the song alone (a restart, say) while the host was busy with another. Always
        /// <c>false</c> on the host.
        /// </summary>
        public readonly bool HostInPlay;

        /// <summary>Friends' setups added to this song, by member ID.</summary>
        public readonly Dictionary<string, RemoteSetup> Friends = new();

        public readonly BandMeterSync BandMeter;
        public readonly PauseSync     Pause;
        public readonly ClockSync     Clock;
        public readonly Departures    Departures;

        public SyncedSong(LinkSession session, InputSender local, bool hostInPlay)
        {
            Session = session;
            Local = local;
            HostInPlay = hostInPlay;

            BandMeter = new BandMeterSync(this);
            Clock = new ClockSync(this);
            Pause = new PauseSync(this, Clock);
            Departures = new Departures(this);
        }

        public GameManager GameManager => Local.GameManager;
        public string SongHash => Local.SongHash;
        public string PlayId => Local.PlayId;
        public bool IsHost => Session.Role == LinkRole.Host;

        /// <summary>We left the song (<see cref="End"/>). It sends nothing more.</summary>
        public bool IsEnded { get; private set; }

        /// <summary>Whether <paramref name="gm"/> is the game manager of this song, and we're still in it.</summary>
        public bool IsFor(GameManager gm) =>
            !IsEnded && gm != null && GameManager == gm && !gm.IsPractice && !gm.IsReplay;

        /// <summary>
        /// Whether <paramref name="message"/> is from <paramref name="member"/>'s play of this song: the one whose
        /// setup we added. Anything else (a play they started again, or one for a song we started alone) isn't
        /// about our song, even with the same song hash.
        /// </summary>
        public bool IsFromPlay(LinkMember member, SongPlayMessage message) =>
            member != null && message.Song == SongHash && Friends.TryGetValue(member.Id, out var setup) &&
            !setup.Left && setup.IsPlay(message);

        /// <summary>Send a message about this play of the song to every other member.</summary>
        public void Send(SongPlayMessage message)
        {
            if (IsEnded) return;

            message.Song = SongHash;
            message.Play = PlayId;
            Session.Send(message);
        }

        public LinkMember FindMember(string id)
        {
            foreach (var member in Session.Members)
            {
                if (member.Id == id) return member;
            }
            return null;
        }

        public LinkMember Host
        {
            get
            {
                foreach (var member in Session.Members)
                {
                    if (member.IsHost) return member;
                }
                return null;
            }
        }

        /// <summary>
        /// Everyone playing the song, us included: the members whose setups we added, while they're still in
        /// gameplay (one who quit to the menu isn't waited for).
        /// </summary>
        public List<string> Participants()
        {
            var members = new List<string> { Session.LocalMemberId };
            foreach (var (member, setup) in Friends)
            {
                if (!setup.Left && FindMember(member) is { Scene: LinkStatus.SCENE_GAMEPLAY })
                {
                    members.Add(member);
                }
            }
            return members;
        }

        /// <summary>
        /// Who a player is across the session: the member whose game they play on, and their index in that
        /// game's setup.
        /// </summary>
        public bool TryGetPlayerKey(BasePlayer player, out string member, out int index)
        {
            if (player.Player.RemoteFeed is { } feed)
            {
                member = feed.Member;
                index = feed.Index;
            }
            else
            {
                member = Session.LocalMemberId;
                index = Local.Players.IndexOf(player);
            }
            return index >= 0;
        }

        /// <summary>Call every frame.</summary>
        public void Update(double now)
        {
            BandMeter.Update(now);
            Pause.Update(now);
            Clock.Update(now);
            Departures.Update(now);
        }

        /// <summary>We left the song.</summary>
        public void End()
        {
            if (IsEnded) return;

            IsEnded = true;
            BandMeter.End();
            Clock.End();
        }
    }
}
