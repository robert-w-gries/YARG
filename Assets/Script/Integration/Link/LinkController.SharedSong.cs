using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Our side of a synced song, and the format of the <c>inputs</c> message.
    /// </summary>
    public partial class LinkController
    {
        /// <summary>Bytes per input in the <c>inputs</c> message: time (double), action (int), value (int).</summary>
        private const int INPUT_SIZE = 16;

        /// <summary>
        /// Inputs per player in one <c>inputs</c> message. A backlog (after a hitch, say) goes out over several
        /// messages, so no message gets near the transport's size limit; friends drop messages with more.
        /// </summary>
        private const int MAX_INPUTS_PER_MESSAGE = 4096;

        /// <summary>
        /// Our side of a synced song: which players we share and how many of their inputs we've sent.
        /// </summary>
        private class SharedSong
        {
            public readonly GameManager      GameManager;
            public readonly string           SongHash;

            /// <summary>
            /// This play of the song, in every message about it. Restarting is another play of the same song, so
            /// friends can tell the two apart.
            /// </summary>
            public readonly string PlayId = Guid.NewGuid().ToString("N");

            /// <summary>
            /// Joiner: the host is in this play of the song, so it runs the band's meter and resumes pauses. Not
            /// when we started the song alone (a restart, say) while the host was busy with another.
            /// </summary>
            public bool HostInPlay;
            public readonly List<BasePlayer> Players = new();
            public readonly Dictionary<BasePlayer, int> SentCounts = new();
            public double LastSendTime = double.NegativeInfinity;

            private int _seq;

            public SharedSong(GameManager gameManager, string songHash)
            {
                GameManager = gameManager;
                SongHash = songHash;

                foreach (var player in gameManager.Players)
                {
                    if (player.IsRemote) continue;

                    Players.Add(player);
                    SentCounts[player] = 0;
                }
            }

            public JObject CreateSetupMessage()
            {
                var frames = new List<ReplayFrame>();
                var colorProfiles = new Dictionary<Guid, ColorProfile>();
                var cameraPresets = new Dictionary<Guid, CameraPreset>();
                var rockMeterPresets = new Dictionary<Guid, RockMeterPreset>();

                var players = new JArray();
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

                    players.Add(new JObject
                    {
                        ["name"]       = profile.Name,
                        // Bots don't record inputs (and the replay format doesn't say who's a bot), so
                        // friends simulate a bot of their own
                        ["bot"]        = profile.IsBot,
                        ["instrument"] = profile.CurrentInstrument.ToString(),
                        ["difficulty"] = profile.CurrentDifficulty.ToString(),
                        ["frame"]      = frameIndex,
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

                return new JObject
                {
                    ["song"]    = SongHash,
                    ["play"]    = PlayId,
                    ["players"] = players,
                    ["replay"]  = Convert.ToBase64String(bytes),
                };
            }

            public JObject CreateInputsMessage(GameManager gm)
            {
                var players = new JArray();
                for (int i = 0; i < Players.Count; i++)
                {
                    var player = Players[i];
                    if (player == null) continue;

                    var inputs = player.ReplayInputs;
                    int from = SentCounts[player];
                    int count = Math.Min(inputs.Count - from, MAX_INPUTS_PER_MESSAGE);

                    var bytes = new byte[count * INPUT_SIZE];
                    using (var writer = new BinaryWriter(new MemoryStream(bytes)))
                    {
                        for (int j = from; j < from + count; j++)
                        {
                            writer.Write(inputs[j].Time);
                            writer.Write(inputs[j].Action);
                            writer.Write(inputs[j].Integer);
                        }
                    }
                    // Anything left goes in the next message
                    SentCounts[player] = from + count;

                    players.Add(new JObject
                    {
                        ["i"]          = i,
                        ["from"]       = from,
                        ["inputs"]     = Convert.ToBase64String(bytes),
                        ["score"]      = player.Score,
                        ["bonus"]      = player.BandBonusScore,
                        ["combo"]      = player.Combo,
                        ["stars"]      = player.Stars,
                        ["notesHit"]   = player.NotesHit,
                        ["totalNotes"] = player.BaseStats.TotalNotes,
                    });
                }

                return new JObject
                {
                    ["song"]    = SongHash,
                    ["play"]    = PlayId,
                    ["seq"]     = _seq++,
                    // Every input up to this time has been recorded (before each player's calibration)
                    ["time"]    = gm.InputTime,
                    // Our input time right now, for friends to measure how far apart our clocks are
                    ["clock"]   = gm.InputTimeAtCall,
                    ["players"] = players,
                };
            }
        }
    }
}
