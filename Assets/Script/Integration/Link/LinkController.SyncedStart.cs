using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Core.Song;
using YARG.Gameplay;
using YARG.Integration.Link.Session;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// The synced start of a song: gameplay holds at the loading screen until the session schedules the start,
    /// and friends' players are added to the song.
    /// </summary>
    public partial class LinkController
    {
        /// <summary>Give up on a synced start after this long, in seconds, and start alone.</summary>
        private const double MAX_WAIT_SECONDS = 120.0;

        // Synced-start state, touched only on the main thread
        private bool    _waiting;
        private double? _startAt;
        private string  _cancelReason;

        public enum StartResult
        {
            /// <summary>A synced start is scheduled; call <see cref="WaitForStartTime"/>.</summary>
            Synced,
            /// <summary>The session is gone or took too long; start right away.</summary>
            Alone,
            /// <summary>The start was cancelled; return to the menu.</summary>
            Cancelled,
        }

        /// <summary>
        /// Report that gameplay is loaded, then wait until the session schedules the start.
        /// Called by <see cref="GameManager"/> once its own players exist.
        /// </summary>
        /// <remarks>
        /// The start is scheduled a couple of seconds ahead, which leaves time to add friends' players
        /// (<see cref="TakeRemotePlayers"/>) before <see cref="WaitForStartTime"/>.
        /// </remarks>
        public async UniTask<StartResult> WaitForStartSignal(GameManager gameManager, LoadingContext context)
        {
            var song = gameManager.Song;

            // A song that's still ours (gameplay reloaded without its GameManager telling us) is over now
            EndSharedSong();

            _waiting = true;
            _startAt = null;
            _cancelReason = null;
            _remoteRequestHash = null;

            // Forget setups for any other song
            string hash = song.Hash.ToString();
            var stale = new List<string>();
            foreach (var (member, setup) in _remoteSetups)
            {
                if (setup.SongHash != hash) stale.Add(member);
            }
            foreach (var member in stale)
            {
                _remoteSetups.Remove(member);
            }

            // Tell friends about our players first, so they have it by the time the start is scheduled
            var shared = new SharedSong(gameManager, hash);
            _session.Broadcast(KIND_SETUP, shared.CreateSetupMessage());
            _session.ReportReady(ToSongInfo(song));

            context.SetLoadingText("Waiting for other players...", song.Name.Original);
            YargLogger.LogInfo("Link: waiting for synced start");

            double deadline = Now + MAX_WAIT_SECONDS;
            while (true)
            {
                if (_cancelReason != null)
                {
                    ToastManager.ToastWarning($"Online start cancelled: {_cancelReason}");
                    _waiting = false;
                    return StartResult.Cancelled;
                }

                if (_startAt.HasValue)
                {
                    // A host taking part sent its setup before it reported ready, so before the start reached us.
                    // Without one (it's in another song, or another play of this one), we're on our own.
                    shared.HostInPlay = _session.Role == LinkRole.Join && HostMember() is { } host &&
                        _remoteSetups.TryGetValue(host.Id, out var hostSetup) && hostSetup.SongHash == hash;
                    _shared = shared;
                    StartBandMeter(gameManager);
                    StartClockSync();
                    StartPauseSync();
                    return StartResult.Synced;
                }

                if (_session is not { IsConnected: true } || Now >= deadline)
                {
                    ToastManager.ToastWarning("Lost the online session, starting without sync");
                    _waiting = false;
                    return StartResult.Alone;
                }

                await UniTask.Yield();
            }
        }

        /// <summary>
        /// Wait for the moment scheduled by <see cref="WaitForStartSignal"/>.
        /// </summary>
        public async UniTask WaitForStartTime()
        {
            try
            {
                while (_startAt.HasValue && Now < _startAt.Value && _session != null)
                {
                    await UniTask.Yield();
                }
                YargLogger.LogInfo("Link: synced start");
            }
            finally
            {
                _waiting = false;
                _startAt = null;
            }
        }

        /// <summary>
        /// Friends' players to add to the current song: their replay frames, plus the feeds their inputs arrive on.
        /// </summary>
        public List<(ReplayFrame Frame, ReplayData Data, RemoteInputFeed Feed)> TakeRemotePlayers(SongEntry song)
        {
            var players = new List<(ReplayFrame, ReplayData, RemoteInputFeed)>();
            string hash = song.Hash.ToString();
            var taken = new List<string>();
            foreach (var (member, setup) in _remoteSetups)
            {
                // WaitForStartSignal removed the setups for other songs. If a setup for a different song arrived
                // after that, the friend gets no highway in this song.
                if (setup.SongHash != hash)
                {
                    YargLogger.LogFormatInfo("Link: {0} sent a setup for a different song, so they have no " +
                        "highway in this one", setup.Feeds.Count > 0 ? setup.Feeds[0].Name : member);
                    continue;
                }

                taken.Add(member);
                _songSetups[member] = setup;

                foreach (var feed in setup.Feeds)
                {
                    // The host's fail meter is the band's, so friends' players count toward it there
                    feed.InHostBand = _session?.Role == LinkRole.Host;

                    if (feed.Mirrored)
                    {
                        var frame = setup.Data.Frames[setup.FrameIndices[feed.Index]];
                        frame.Profile.IsBot = feed.IsBot;
                        players.Add((frame, setup.Data, feed));
                    }
                }
            }

            foreach (var member in taken)
            {
                _remoteSetups.Remove(member);
            }

            YargLogger.LogInfo($"Link: {_songSetups.Count} friend setup(s) for this song, {players.Count} highway(s) to add");
            return players;
        }

        private void OnStartScheduled(double delaySeconds)
        {
            if (_waiting)
            {
                _startAt = Now + delaySeconds;
            }
        }

        private void OnStartCancelled(string reason)
        {
            if (_waiting)
            {
                _cancelReason = reason;
            }
            else if (_remoteRequestHash != null)
            {
                _remoteRequestHash = null;
                ToastManager.ToastWarning($"Online start cancelled: {reason}");
            }
        }

        private static SongInfo ToSongInfo(SongEntry song) =>
            new(song.Hash.ToString(), song.Name.Original, song.Artist.Original);
    }
}
