using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using YARG.Core.Input;
using YARG.Core.IO;
using YARG.Core.Replays;
using YARG.Integration.Link.Session;

namespace YARG.Integration.Link
{
    /// <summary>
    /// A friend's side of a synced song: their players, and the inputs that they send.
    /// </summary>
    public partial class LinkController
    {
        /// <summary>
        /// Input and song times further from zero than this, in seconds (a day), are nonsense, whoever sent them.
        /// </summary>
        private const double MAX_INPUT_TIME = 24 * 60 * 60;

        /// <summary>
        /// A friend's players for one song.
        /// </summary>
        private class RemoteSetup
        {
            public string SongHash;
            /// <summary>Which play of the song (see <see cref="SharedSong.PlayId"/>).</summary>
            public string Play;
            public ReplayData Data;
            public readonly List<RemoteInputFeed> Feeds = new();
            /// <summary>Player index → frame index in <see cref="Data"/>, or -1.</summary>
            public readonly List<int> FrameIndices = new();
            /// <summary>The friend has left this play of the song: it ended, they quit, or they restarted.</summary>
            public bool Left;

            /// <summary>Whether a message is about this play of the song.</summary>
            public bool IsPlay(JObject msg) =>
                Play != null && (string) msg["song"] == SongHash && (string) msg["play"] == Play;

            /// <exception cref="Exception">The setup is malformed; the caller logs it.</exception>
            public static RemoteSetup Parse(string member, JObject msg)
            {
                var setup = new RemoteSetup { SongHash = (string) msg["song"] };
                if (setup.SongHash == null || setup.SongHash.Length > LinkLimits.MAX_ID_LENGTH)
                {
                    throw new FormatException("bad song hash");
                }

                setup.Play = (string) msg["play"];
                if (string.IsNullOrEmpty(setup.Play) || setup.Play.Length > LinkLimits.MAX_ID_LENGTH)
                {
                    throw new FormatException("bad play ID");
                }

                var bytes = Convert.FromBase64String((string) msg["replay"]);

                // YARG.Core's reader trusts lengths in the data, so check it first
                if (!ReplayBlobCheck.IsSafe(bytes, ReplayIO.REPLAY_VERSIONS.CURRENT, out string problem))
                {
                    throw new FormatException($"unusable replay data: {problem}");
                }
                using (var array = FixedArray.Read(new MemoryStream(bytes), bytes.Length))
                {
                    setup.Data = new ReplayData(array.ToValueStream(), ReplayIO.REPLAY_VERSIONS.CURRENT,
                        new ReplayReadOptions());
                }

                var players = (JArray) msg["players"];
                if (players.Count > LinkLimits.MAX_PLAYERS_PER_MEMBER)
                {
                    throw new FormatException($"{players.Count} players is more than {LinkLimits.MAX_PLAYERS_PER_MEMBER}");
                }
                for (int i = 0; i < players.Count; i++)
                {
                    var p = (JObject) players[i];
                    int frame = (int?) p["frame"] ?? -1;
                    bool mirrored = frame >= 0 && frame < setup.Data.Frames.Length;

                    setup.FrameIndices.Add(mirrored ? frame : -1);
                    setup.Feeds.Add(new RemoteInputFeed(member, i,
                        LinkLimits.Clamp((string) p["name"], LinkLimits.MAX_NAME_LENGTH) ?? "Friend",
                        LinkLimits.Clamp((string) p["instrument"], LinkLimits.MAX_SHORT_LENGTH) ?? "",
                        LinkLimits.Clamp((string) p["difficulty"], LinkLimits.MAX_SHORT_LENGTH) ?? "",
                        mirrored,
                        (bool?) p["bot"] ?? false));
                }

                return setup;
            }

            public static bool IsSaneTime(double time) =>
                !double.IsNaN(time) && !double.IsInfinity(time) && Math.Abs(time) <= MAX_INPUT_TIME;

            public void Receive(JObject msg, double now)
            {
                if (!IsPlay(msg)) return;

                double time = (double) msg["time"];
                if (!IsSaneTime(time))
                {
                    throw new FormatException($"bad input time {time}");
                }

                var inputs = new List<GameInput>();
                var players = (JArray) msg["players"];
                if (players.Count > Feeds.Count)
                {
                    throw new FormatException($"inputs for {players.Count} players, but the setup has {Feeds.Count}");
                }
                foreach (var token in players)
                {
                    var p = (JObject) token;
                    int i = (int) p["i"];
                    if (i < 0 || i >= Feeds.Count) continue;

                    var bytes = Convert.FromBase64String((string) p["inputs"] ?? "");
                    int from = (int?) p["from"] ?? 0;
                    if (bytes.Length / INPUT_SIZE > MAX_INPUTS_PER_MESSAGE || from < 0)
                    {
                        throw new FormatException($"bad inputs for player {i}");
                    }

                    var feed = Feeds[i];
                    feed.SetStats(
                        (int?) p["score"] ?? 0,
                        (int?) p["bonus"] ?? 0,
                        (int?) p["combo"] ?? 0,
                        (float?) p["stars"] ?? 0f,
                        (int?) p["notesHit"] ?? 0,
                        (int?) p["totalNotes"] ?? 0);

                    inputs.Clear();
                    using (var reader = new BinaryReader(new MemoryStream(bytes)))
                    {
                        for (int n = 0; n < bytes.Length / INPUT_SIZE; n++)
                        {
                            double t = reader.ReadDouble();
                            int action = reader.ReadInt32();
                            int value = reader.ReadInt32();
                            if (!IsSaneTime(t))
                            {
                                throw new FormatException($"bad input time {t}");
                            }
                            inputs.Add(new GameInput(t, action, value));
                        }
                    }

                    feed.Receive(from, inputs, time, now);
                }
            }
        }
    }
}
