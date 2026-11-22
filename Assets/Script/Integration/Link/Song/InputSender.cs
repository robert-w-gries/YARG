using System;
using System.Collections.Generic;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Our side of a synced song: which of our players we share, their setup, and the inputs we've sent.
    /// </summary>
    internal sealed class InputSender
    {
        /// <summary>Send new inputs at most this often, in seconds (120 times a second).</summary>
        private const double INTERVAL_MIN = 1.0 / 120.0;

        /// <summary>
        /// Send at least this often, in seconds (30 times a second), so friends' tracks keep moving when nothing is
        /// pressed.
        /// </summary>
        private const double INTERVAL_MAX = 1.0 / 30.0;

        public readonly GameManager GameManager;
        public readonly string      SongHash;

        /// <summary>
        /// This play of the song, in every message about it. Restarting is another play of the same song, so
        /// friends can tell the two apart.
        /// </summary>
        public readonly string PlayId = Guid.NewGuid().ToString("N");

        /// <summary>Our players, by their index in our setup.</summary>
        public readonly List<BasePlayer> Players = new();

        private readonly Dictionary<BasePlayer, int> _sentCounts = new();
        private double _lastSendTime = double.NegativeInfinity;
        private int _seq;

        public InputSender(GameManager gameManager, string songHash)
        {
            GameManager = gameManager;
            SongHash = songHash;

            foreach (var player in gameManager.Players)
            {
                if (player.IsRemote) continue;

                Players.Add(player);
                _sentCounts[player] = 0;
            }
        }

        /// <summary>
        /// Game modes whose players can be shown as a highway on friends' screens.
        /// </summary>
        public static bool CanMirror(GameMode mode)
        {
            // Vocals share one track and pro guitar has no replay support
            return mode is not (GameMode.Vocals or GameMode.ProGuitar);
        }

        private static int[] StarCutoffs(BasePlayer player)
        {
            var cutoffs = player.BaseEngine?.StarScoreThresholds;
            return cutoffs is { Length: SetupPlayer.STAR_CUTOFF_COUNT } ? (int[]) cutoffs.Clone() : null;
        }

        public SetupMessage CreateSetupMessage()
        {
            var frames = new List<ReplayFrame>();
            var colorProfiles = new Dictionary<Guid, ColorProfile>();
            var cameraPresets = new Dictionary<Guid, CameraPreset>();
            var rockMeterPresets = new Dictionary<Guid, RockMeterPreset>();

            var players = new List<SetupPlayer>();
            foreach (var player in Players)
            {
                var yargPlayer = player.Player;
                var profile = yargPlayer.Profile;

                int frameIndex = -1;
                if (CanMirror(profile.GameMode))
                {
                    try
                    {
                        var (frame, _) = player.ConstructReplayData();
                        frameIndex = frames.Count;
                        frames.Add(frame);

                        if (!yargPlayer.ColorProfile.DefaultPreset)
                            colorProfiles.TryAdd(yargPlayer.ColorProfile.Id, yargPlayer.ColorProfile);
                        if (!yargPlayer.CameraPreset.DefaultPreset)
                            cameraPresets.TryAdd(yargPlayer.CameraPreset.Id, yargPlayer.CameraPreset);
                        if (!yargPlayer.RockMeterPreset.DefaultPreset)
                            rockMeterPresets.TryAdd(yargPlayer.RockMeterPreset.Id, yargPlayer.RockMeterPreset);
                    }
                    catch (Exception e)
                    {
                        YargLogger.LogException(e, $"Link: couldn't share {profile.Name}'s setup");
                        frameIndex = -1;
                    }
                }

                players.Add(new SetupPlayer
                {
                    Name = profile.Name,
                    Bot = profile.IsBot,
                    Instrument = profile.CurrentInstrument.ToString(),
                    Difficulty = profile.CurrentDifficulty.ToString(),
                    Frame = frameIndex,
                    StarCutoffs = StarCutoffs(player),
                });
            }

            bool noFail = GameManager.NoFailMode != NoFailMode.Off;
            var data = new ReplayData(colorProfiles, cameraPresets, rockMeterPresets, noFail,
                frames.ToArray(), Array.Empty<double>());
            var bytes = data.Serialize().ToArray();

            // Friends check this before parsing it; if ours fails, the check is behind YARG.Core's format
            if (!ReplayBlobCheck.IsSafe(bytes, ReplayIO.REPLAY_VERSIONS.CURRENT, out string problem))
            {
                YargLogger.LogFormatError("Link: friends will turn down our players' replay data ({0}). " +
                    "ReplayBlobCheck needs updating for YARG.Core's replay format.", problem);
            }

            return new SetupMessage
            {
                Song = SongHash,
                Play = PlayId,
                Players = players,
                Replay = bytes,
            };
        }

        /// <summary>
        /// Send our players' new inputs, if it's time to. Call once a frame, after the GameManager's update, so
        /// this frame's inputs are recorded.
        /// </summary>
        public void Update(LinkSession session, double now)
        {
            var gm = GameManager;
            if (gm == null || !gm.IsSongStarted) return;

            double sinceLast = now - _lastSendTime;
            if (sinceLast < INTERVAL_MIN) return;

            bool anyNew = false;
            foreach (var player in Players)
            {
                if (player != null && player.ReplayInputs.Count > _sentCounts[player])
                {
                    anyNew = true;
                    break;
                }
            }

            if (!anyNew && sinceLast < INTERVAL_MAX) return;

            _lastSendTime = now;
            session.Send(CreateInputsMessage(gm));
        }

        private InputsMessage CreateInputsMessage(GameManager gm)
        {
            var players = new List<PlayerInputs>();
            for (int i = 0; i < Players.Count; i++)
            {
                var player = Players[i];
                if (player == null) continue;

                int from = _sentCounts[player];
                var bytes = InputCodec.Encode(player.ReplayInputs, from,
                    input => new LinkInput(input.Time, input.Action, input.Integer), out int count);

                // Anything left goes in the next message
                _sentCounts[player] = from + count;

                players.Add(new PlayerInputs
                {
                    Index = i,
                    From = from,
                    Inputs = bytes,
                    Score = player.Score,
                    BandBonus = player.BandBonusScore,
                    Combo = player.Combo,
                    Stars = player.Stars,
                    NotesHit = player.NotesHit,
                    TotalNotes = player.BaseStats.TotalNotes,
                    StarPower = player.BaseStats.IsStarPowerActive,
                });
            }

            return new InputsMessage
            {
                Song = SongHash,
                Play = PlayId,
                Seq = _seq++,
                Time = gm.InputTime,
                Clock = gm.InputTimeAtCall,
                Players = players,
            };
        }
    }
}
