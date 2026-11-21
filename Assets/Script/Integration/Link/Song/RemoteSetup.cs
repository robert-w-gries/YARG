using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core.Input;
using YARG.Core.IO;
using YARG.Core.Replays;
using YARG.Integration.Link.Play;

namespace YARG.Integration.Link
{
    /// <summary>
    /// A friend's players for one play of a song, from their <see cref="SetupMessage"/>, and the inputs that
    /// they send for it.
    /// </summary>
    internal sealed class RemoteSetup
    {
        public readonly string SongHash;

        /// <summary>Which play of the song (see <see cref="InputSender.PlayId"/>).</summary>
        public readonly string Play;

        public readonly ReplayData Data;
        public readonly List<RemoteInputFeed> Feeds = new();

        /// <summary>Player index → frame index in <see cref="Data"/>, or -1.</summary>
        public readonly List<int> FrameIndices = new();

        /// <summary>The friend has left this play of the song: it ended, they quit, or they restarted.</summary>
        public bool Left;

        private RemoteSetup(string songHash, string play, ReplayData data)
        {
            SongHash = songHash;
            Play = play;
            Data = data;
        }

        /// <summary>Whether a message is about this play of the song.</summary>
        public bool IsPlay(SongPlayMessage message) => message.Song == SongHash && message.Play == Play;

        /// <exception cref="FormatException">The replay data is not usable; the caller logs it.</exception>
        public static RemoteSetup From(string member, SetupMessage message)
        {
            // YARG.Core's reader trusts lengths in the data, so check it first
            var bytes = message.Replay;
            if (!ReplayBlobCheck.IsSafe(bytes, ReplayIO.REPLAY_VERSIONS.CURRENT, out string problem))
            {
                throw new FormatException($"unusable replay data: {problem}");
            }

            ReplayData data;
            using (var array = FixedArray.Read(new MemoryStream(bytes), bytes.Length))
            {
                data = new ReplayData(array.ToValueStream(), ReplayIO.REPLAY_VERSIONS.CURRENT, new ReplayReadOptions());
            }

            var setup = new RemoteSetup(message.Song, message.Play, data);
            for (int i = 0; i < message.Players.Count; i++)
            {
                var player = message.Players[i];
                bool mirrored = player.Frame >= 0 && player.Frame < data.Frames.Length;

                setup.FrameIndices.Add(mirrored ? player.Frame : -1);
                setup.Feeds.Add(new RemoteInputFeed(member, i, player.Name ?? "Friend", player.Instrument ?? "",
                    player.Difficulty ?? "", mirrored, player.Bot));
            }
            return setup;
        }

        /// <exception cref="FormatException">The inputs are not usable; the caller logs it.</exception>
        public void Receive(InputsMessage message, double now)
        {
            if (!IsPlay(message)) return;

            if (message.Players.Count > Feeds.Count)
            {
                throw new FormatException($"inputs for {message.Players.Count} players, but the setup has {Feeds.Count}");
            }

            var inputs = new List<GameInput>();
            foreach (var player in message.Players)
            {
                if (player.Index < 0 || player.Index >= Feeds.Count) continue;

                var decoded = InputCodec.Decode(player.Inputs);

                var feed = Feeds[player.Index];
                feed.SetStats(player.Score, player.BandBonus, player.Combo, player.Stars, player.NotesHit,
                    player.TotalNotes);

                inputs.Clear();
                foreach (var input in decoded)
                {
                    inputs.Add(new GameInput(input.Time, input.Action, input.Value));
                }
                feed.Receive(player.From, inputs, message.Time, now);
            }
        }
    }
}
