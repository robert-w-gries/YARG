using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using YARG.Core.Engine;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Keys;
using YARG.Core.Engine.Vocals;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Gameplay.Visuals;
using YARG.Input;
using YARG.Integration.Link;
using YARG.Menu;
using YARG.Playback;
using YARG.Player;

namespace YARG.Gameplay
{
    /// <summary>
    /// The <see cref="GameManager"/> code for online play. In online play, friends play one song together
    /// through the online link (<see cref="LinkController"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The online check.</b> <see cref="IsOnlinePlay"/> is true only when the online session schedules a
    /// synced start for this song. The other GameManager code uses <see cref="IsOnlinePlay"/>. It calls
    /// <see cref="LinkController"/> only through <see cref="Link"/>. <see cref="Link"/> is <c>null</c> when
    /// the song is not online play. Code outside the GameManager uses <see cref="IsOnlinePlay"/>.
    /// </para>
    /// <para>
    /// <b>Authority.</b> Each game is the authority for its own players. The host is the authority only for
    /// the fail meter of the band.
    /// </para>
    /// <list type="table">
    /// <listheader><term>Value</term><description>Authority</description></listheader>
    /// <item>
    ///   <term>The score, band bonus and stars of a player</term>
    ///   <description>The game of the player. That game sends these values with its inputs
    ///   (<see cref="RemoteInputFeed.Score"/>). The score screen of each game shows the sent values
    ///   (<see cref="ScoreCardStats"/>). It does not show the values of the local mirror engine.</description>
    /// </item>
    /// <item>
    ///   <term>The score and stars of the band</term>
    ///   <description>No game is the authority. Each game adds the live score of its own players to the sent
    ///   scores of the friends' players (<see cref="UpdateOnlineBandScore"/>). It calculates the stars from the
    ///   cutoffs of all players. All games show the same total. The values of friends arrive after the network
    ///   delay. Only the score box and the header of the score screen show this total. Saved scores and
    ///   <see cref="BandScore"/> contain only the players of this game.</description>
    /// </item>
    /// <item>
    ///   <term>Band combo, star power multiplier and unisons</term>
    ///   <description>Each game, for its own players only. On a joiner, the players of each friend have an
    ///   <see cref="EngineManager"/> of their own (<see cref="EngineManagerFor"/>). On the host, the friends'
    ///   players use the main <see cref="EngineManager"/>. Thus, on the host, friends' players also change the
    ///   band bonus. A YARG.Core change is necessary to remove this difference.</description>
    /// </item>
    /// <item>
    ///   <term>The fail meter of the band, player fails and revives</term>
    ///   <description>The host. On the host, the friends' players use the main <see cref="EngineManager"/>.
    ///   Thus its meter includes all players of the band. Each joiner runs its meter with fail off. It shows
    ///   the values of the host (<see cref="BandHappiness"/>, <see cref="OnlineBandMeterMarkers"/>). It also
    ///   applies the decisions of the host (<see cref="ReviveFromLink"/>, <see cref="FailFromLink"/>).
    ///   </description>
    /// </item>
    /// </list>
    /// </remarks>
    public partial class GameManager
    {
        /// <summary>
        /// True when the online session schedules a synced start for this song with friends.
        /// <see cref="TryStartOnlinePlay"/> sets this value during the load, and the value does not change
        /// after the load. It is always false in practice mode and in a replay.
        /// </summary>
        /// <remarks>
        /// In online play, the pause menu and the fail menu hide the items that leave friends behind: Restart,
        /// Practice, Skip and the no fail items. The no fail setting has no effect (<see cref="NoFailMode"/>).
        /// </remarks>
        public bool IsOnlinePlay { get; private set; }

        /// <summary>
        /// The online controller in online play, else <c>null</c>. Use this property, not
        /// <see cref="LinkController.Instance"/>. Thus all calls to the link have the same condition.
        /// </summary>
        private LinkController Link => IsOnlinePlay ? LinkController.Instance : null;

        // One engine manager for each friend. On a joiner, these managers keep the friends' players out of the
        // band combo, star power, unisons and fail meter of this game. On the host, the friends' players use
        // the main EngineManager. Refer to the remarks on this class.
        private readonly Dictionary<string, EngineManager> _remoteEngineManagers = new();

        // The score and stars of all players in the band. Only the score box and the header of the score
        // screen use these values. The value is null when the song is not online play.
        private EngineManager _onlineBand;

        #region Loading

        /// <summary>
        /// When friends are in the online session, this method does these steps:
        /// <list type="number">
        /// <item>It tells the friends that this game is loaded.</item>
        /// <item>It waits for the synced start.</item>
        /// <item>It adds a highway for each player of each friend.</item>
        /// </list>
        /// It sets <see cref="IsOnlinePlay"/>.
        /// </summary>
        /// <returns>False if the online session cancelled the start. The game then goes to the song library.</returns>
        private async UniTask<bool> TryStartOnlinePlay(LoadingContext context)
        {
            var link = LinkController.Instance;
            if (link is not { ShouldHoldForStart: true } || IsPractice || ReplayInfo != null)
            {
                return true;
            }

            var result = await link.WaitForStartSignal(this, context);
            switch (result)
            {
                case LinkController.StartResult.Synced:
                    IsOnlinePlay = true;
                    CreateRemotePlayers(link.TakeRemotePlayers(Song));
                    return true;

                case LinkController.StartResult.Alone:
                    // The online session is gone. Thus this song is not online play, and the local rules apply.
                    YargLogger.LogInfo("Link: no synced start, playing this song without online play");
                    return true;

                case LinkController.StartResult.Cancelled:
                    MenuManager.OpenOnNextLoad(MenuManager.Menu.MusicLibrary);
                    GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
                    return false;

                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown start result");
            }
        }

        /// <summary>Wait for the synced start time. Thus all games start the song at the same time.</summary>
        private UniTask WaitForOnlineStartTime() => Link?.WaitForStartTime() ?? UniTask.CompletedTask;

        /// <summary>
        /// Add a highway for each player of a friend. Each highway plays the inputs that come from the game of
        /// the friend, as a replay does.
        /// </summary>
        /// <remarks>
        /// <see cref="LinkController.TakeRemotePlayers"/> gives only the players that can have a highway
        /// (<see cref="RemoteInputFeed.Mirrored"/>). Vocals and pro guitar players of friends get no highway.
        /// They have an icon on the fail meter of the band.
        /// </remarks>
        private void CreateRemotePlayers(List<(ReplayFrame Frame, ReplayData Data, RemoteInputFeed Feed)> remotePlayers)
        {
            foreach (var (frame, data, feed) in remotePlayers)
            {
                var player = new YargPlayer(frame, data, feed);
                var prefab = GetTrackPrefab(player.Profile);
                if (prefab == null)
                {
                    YargLogger.LogFormatWarning("Link: no highway for {0}'s {1} ({2})", feed.Name,
                        player.Profile.CurrentInstrument, player.Profile.GameMode);
                    continue;
                }

                // The highway renderer has a fixed number of slots. The host does not let in a joiner when the
                // session is full (LinkLimits.MAX_SESSION_PLAYERS), but a friend can add players after that.
                if (_highwayIndex + 1 >= HighwayCameraRendering.MAX_MATRICES)
                {
                    YargLogger.LogFormatWarning("Link: no highway for {0}'s {1}: all {2} highways are in use",
                        feed.Name, player.Profile.CurrentInstrument, HighwayCameraRendering.MAX_MATRICES);
                    continue;
                }

                _highwayIndex++;
                try
                {
                    CreateTrackPlayer(prefab, player, lastHighScore: null);
                    YargLogger.LogFormatInfo("Link: added {0}'s {1} track", player.Profile.Name,
                        player.Profile.CurrentInstrument);
                }
                catch (Exception ex)
                {
                    YargLogger.LogException(ex, $"Link: failed to add {feed.Name}'s track");
                }
            }
        }

        /// <summary>
        /// Start the fail meters. Only the meter of the host can fail the song. On a joiner, the main meter
        /// runs with fail off and shows the values of the host. The engine manager of a friend never fails the
        /// song, because the game of the friend runs the meter for those players.
        /// </summary>
        /// <remarks>
        /// This method uses the no fail flag of <see cref="EngineManager.InitializeHappiness"/> to stop fails.
        /// A YARG.Core change can add a mode for this to the engine manager.
        /// </remarks>
        private void InitializeOnlineHappiness()
        {
            bool hostDecides = Link?.IsBandMeterRemote(this) ?? false;
            EngineManager.InitializeHappiness(noFail: hostDecides);

            foreach (var remote in _remoteEngineManagers.Values)
            {
                remote.InitializeHappiness(noFail: true);
            }
        }

        #endregion

        #region Engines and the band meter

        /// <summary>
        /// The engine manager for the engine of a player. The players of this game use
        /// <see cref="EngineManager"/>. In online play, on a joiner, the players of each friend use a different
        /// manager. On the host, they use <see cref="EngineManager"/>. Refer to the remarks on this class.
        /// </summary>
        public EngineManager EngineManagerFor(YargPlayer player)
        {
            var feed = player.RemoteFeed;
            if (feed == null || feed.InHostBand) return EngineManager;

            if (!_remoteEngineManagers.TryGetValue(feed.Member, out var manager))
            {
                manager = new EngineManager();
                _remoteEngineManagers.Add(feed.Member, manager);
            }
            return manager;
        }

        /// <summary>
        /// The value of the fail meter of the band. On a joiner in online play, it is the value of the host.
        /// Else, it is the value of <see cref="EngineManager"/>.
        /// </summary>
        public float BandHappiness => Link?.HostBandHappiness(this) ?? EngineManager.Happiness;

        /// <summary>
        /// The player icons of the fail meter on a joiner in online play. They show the values of the host for
        /// all players in the band. The value is <c>null</c> when the meter shows the engines of this game.
        /// </summary>
        public List<FailMeter.Marker> OnlineBandMeterMarkers() => Link?.BandMeterMarkers(this);

        /// <summary>
        /// The band meter of the host revived the band. The star power of a band member saved a failed player.
        /// </summary>
        public void ReviveFromLink()
        {
            EngineManager.RevivePlayer();
            foreach (var manager in _remoteEngineManagers.Values)
            {
                manager.RevivePlayer();
            }
        }

        /// <summary>
        /// Fail the song, because the game of a friend failed it. The band fails together. If not, this game
        /// continues next to the stopped highway of the friend. The link calls this method only in online play.
        /// Online play is never practice mode.
        /// </summary>
        public void FailFromLink()
        {
            FailSong(fromLink: true);
        }

        #endregion

        #region Band score

        /// <summary>
        /// The band score that the score box shows. It is <see cref="BandScore"/>. In online play, it is the
        /// score of all players in the band. Refer to the remarks on this class.
        /// </summary>
        public int ScoreBoxScore => _onlineBand?.Score ?? BandScore;

        /// <summary>
        /// The band stars that the score box shows. They are <see cref="BandStars"/>. In online play, they come
        /// from the star cutoffs of all players in the band.
        /// </summary>
        public float ScoreBoxStars => _onlineBand?.Stars ?? BandStars;

        /// <summary>
        /// Calculate the star cutoffs of the band from all players that have a highway. The score and the
        /// cutoffs do not include friends' players that have no highway (vocals, pro guitar).
        /// </summary>
        private void UpdateOnlineBandCutoffs()
        {
            _onlineBand ??= new EngineManager();
            _onlineBand.StarScoreThresholds = EngineManager.GetStarScoreCutoffs(
                _players.Select(p => p.BaseEngine.StarScoreThresholds).ToList());
            _onlineBand.ResetStars();

            YargLogger.LogFormatDebug("Online band star score thresholds: {0}",
                string.Join(", ", _onlineBand.StarScoreThresholds));
        }

        /// <summary>
        /// Calculate the score of the band. The score is <paramref name="localScore"/> (the players of this game)
        /// plus the score and band bonus that the game of each friend sends for its players. All games do this
        /// calculation. The host is not the authority for it.
        /// </summary>
        private void UpdateOnlineBandScore(int localScore)
        {
            int friendsScore = 0;
            foreach (var player in _players)
            {
                if (player.Player.RemoteFeed is { } feed)
                {
                    friendsScore += feed.Score + feed.BandBonus;
                }
            }

            _onlineBand.Score = localScore + friendsScore;
            _onlineBand.UpdateStars();
        }

        /// <summary>
        /// The stats that the score card of a player shows. For the player of a friend, these are the stats of
        /// the mirror, with the score, band bonus and stars that the game of the friend sent. Thus all games
        /// show the same values for that player.
        /// </summary>
        private static BaseStats ScoreCardStats(BasePlayer player)
        {
            var stats = player.BaseStats;
            var feed = player.Player.RemoteFeed;
            if (feed is not { HasData: true }) return stats;

            BaseStats copy = stats switch
            {
                GuitarStats guitar => new GuitarStats(guitar),
                DrumsStats drums   => new DrumsStats(drums),
                KeysStats keys     => new KeysStats(keys),
                VocalsStats vocals => new VocalsStats(vocals),
                _                  => null,
            };
            if (copy == null) return stats;

            // TotalScore comes from other fields. Add the difference to the committed score. Then the total is
            // the score of the friend.
            copy.CommittedScore += feed.Score - copy.TotalScore;
            copy.BandBonusScore = feed.BandBonus;
            copy.Stars = feed.Stars;
            return copy;
        }

        #endregion

        #region Pause, leave and song time

        /// <summary>
        /// Online play, in a synced pause: close the pause menu, and stay paused. The player selected Resume.
        /// The player now waits until the band is ready.
        /// </summary>
        public void HidePauseMenuWhileWaitingForBand()
        {
            _pauseMenu.PopAllMenus();
            CloseAllPlayerMenus();
        }

        /// <summary>
        /// Online play, in a synced pause: open the pause menu again, and stay paused. This occurs when the
        /// player who waits selects Resume again (not ready). It also occurs when a friend pauses again.
        /// </summary>
        public void ShowPauseMenuWhileWaitingForBand()
        {
            if (!Paused || _pauseMenu.IsOpen) return;
            PushPauseMenu();
        }

        /// <summary>
        /// "Back to Library" in the pause menu, in online play. When this game is the host and friends still
        /// play, a dialog asks if the friends also leave the song.
        /// </summary>
        /// <returns>True if the dialog opened. Then the pause menu must not quit yet.</returns>
        public bool OfferLeaveWithParty() => Link?.OfferLeaveWithParty(this) ?? false;

        /// <summary>
        /// The input time at the moment of the call. <see cref="InputTime"/> is the time at the start of the
        /// frame. The clock sync uses this value, because an error of one frame is too large for the sync.
        /// During a pause, the value is the pause time.
        /// </summary>
        public double InputTimeAtCall => Paused ? InputTime : GetInputTime(InputManager.CurrentInputTime);

        /// <inheritdoc cref="SongRunner.ShiftInputTime"/>
        public void ShiftInputTime(double seconds) => _songRunner.ShiftInputTime(seconds);

        /// <summary>
        /// The song time that the engines reached before <see cref="JumpInputTime"/> moved the song back. The
        /// engines and the inputs of this game wait (<see cref="Rewinding"/>) until the song passes this time
        /// again. The value is <c>null</c> when no jump back is in progress.
        /// </summary>
        private double? _catchUpSongTime;

        /// <summary>
        /// Online play: move the song by <paramref name="seconds"/> of input time at once, with a seek. The
        /// clock sync uses this method when the clock of this game is too far from the host's clock for
        /// <see cref="ShiftInputTime"/>. The player hears a jump in the audio.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A jump forward skips part of the song. The engines of this game miss the notes in that part.
        /// </para>
        /// <para>
        /// A jump back plays part of the song again. The engines must not go back in time. Thus, as in the
        /// rewind of a resume, the engines and the inputs of this game wait until the song passes the time
        /// that they reached (<see cref="UpdateOnlineCatchUp"/>). The highways show the notes of that part
        /// again. A pause during that part is a usual pause. The resume after it also waits for that time.
        /// </para>
        /// </remarks>
        /// <returns>
        /// False if the jump is not possible now: not online play, not playing, already in a rewind, or past the
        /// end of the song.
        /// </returns>
        public bool JumpInputTime(double seconds)
        {
            if (!IsOnlinePlay || !IsSongStarted || Paused || Rewinding || _resumeInProgress || PlayerHasFailed ||
                seconds == 0 || double.IsNaN(seconds))
            {
                return false;
            }

            double target = InputTime + seconds;
            if (target + (SongTime - InputTime) >= SongLength)
            {
                return false;
            }

            if (seconds < 0)
            {
                _catchUpSongTime = SongTime;
                Rewinding = true;
                foreach (var player in _players)
                {
                    player.Rewind(VisualTime + seconds);
                }
            }

            SetSongTime(target, 0);
            return true;
        }

        /// <summary>
        /// Call each frame before the players update. Ends a jump back of <see cref="JumpInputTime"/> when the
        /// song passes the time that the engines reached.
        /// </summary>
        private void UpdateOnlineCatchUp()
        {
            // A resume in progress waits for this time itself (RewindAndResume)
            if (_catchUpSongTime is not { } catchUp || _resumeInProgress || SongTime <= catchUp)
            {
                return;
            }

            _catchUpSongTime = null;
            Rewinding = false;
            foreach (var player in _players)
            {
                player.PostRewind(VisualTime);
                player.SendInputsOnResume();
            }
        }

        #endregion
    }
}
