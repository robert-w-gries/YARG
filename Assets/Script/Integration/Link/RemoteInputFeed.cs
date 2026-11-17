using System;
using System.Collections.Generic;
using YARG.Core.Input;
using YARG.Core.Logging;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Inputs and live stats for one of a friend's players, received over the online link.
    /// </summary>
    /// <remarks>
    /// The friend's game sends its recorded inputs (the same ones it saves in replays) together with its
    /// current input time. Feeding those inputs to a local engine reproduces their performance exactly, like a
    /// replay. The catch is that inputs arrive late. The friend's highway is still drawn in time with the song;
    /// only their engine runs behind, as far as the inputs received so far allow, so their hits, misses and
    /// combo show up a network delay late while the notes scroll on time.
    /// </remarks>
    public class RemoteInputFeed
    {
        /// <summary>The feed counts as stalled after this long without data.</summary>
        public const double STALL_SECONDS = 3.0;

        /// <summary>
        /// Received inputs waiting to be played. Gameplay drains them every frame; a friend who sends inputs
        /// while we aren't playing their song can't grow this past here (later ones are dropped).
        /// </summary>
        public const int MAX_PENDING_INPUTS = 64 * 1024;

        /// <summary>The member ID of the friend whose player this is.</summary>
        public string Member { get; }
        public int    Index { get; }
        public string Name  { get; }

        /// <summary>The friend's game mode can be mirrored with a highway here.</summary>
        public bool Mirrored { get; }

        /// <summary>
        /// The friend's player is a bot. Bots record no inputs, so the mirrored player runs its own bot, which
        /// plays the chart exactly the same way.
        /// </summary>
        public bool IsBot { get; }

        /// <summary>
        /// The player counts toward this game's band fail meter. Set on the host, whose meter is the band's;
        /// elsewhere friends' players get a separate meter that can't fail.
        /// </summary>
        public bool InHostBand { get; set; }

        public string Instrument { get; }
        public string Difficulty { get; }

        /// <summary>The friend's own numbers, as last reported. These are authoritative.</summary>
        public int   Score    { get; private set; }
        /// <summary>The friend's band bonus: points from their bandmates' star power.</summary>
        public int   BandBonus { get; private set; }
        public int   Combo    { get; private set; }
        public float Stars    { get; private set; }
        public int   NotesHit { get; private set; }
        public int   TotalNotes { get; private set; }

        public bool HasData => !double.IsNegativeInfinity(_watermark);

        /// <summary>When data last arrived, in <see cref="LinkController"/> clock seconds.</summary>
        public double LastReceived { get; private set; } = double.NegativeInfinity;

        /// <summary>
        /// How far behind the song the friend's engine was simulated on the last frame, in seconds: how late
        /// their hits show up. Their newest data's lateness (network latency, send batching, and any difference
        /// between our clocks), or 0 when it's ahead of us.
        /// </summary>
        public double Lag { get; private set; }

        /// <summary>
        /// How far behind our song the friend's newest data was on the last frame, in seconds. Negative when
        /// their clock is ahead of ours.
        /// </summary>
        public double Lateness { get; private set; }

        /// <summary>The most <see cref="Lag"/> since the last <see cref="TakeMaxLag"/>.</summary>
        private double _maxLag;

        private readonly List<GameInput> _pending = new();

        // Index of the next input we expect from the friend's input list
        private int _nextInputIndex;
        private bool _gapLogged;
        private bool _overflowLogged;

        // The friend's input time (before their calibration) at the moment they sent their latest data.
        // Every input up to this time has been received.
        private double _watermark = double.NegativeInfinity;

        private double _lastPlaybackTime = double.NegativeInfinity;

        public RemoteInputFeed(string member, int index, string name, string instrument, string difficulty,
            bool mirrored, bool isBot = false)
        {
            IsBot = isBot;
            Member = member;
            Index = index;
            Name = name;
            Instrument = instrument;
            Difficulty = difficulty;
            Mirrored = mirrored;
        }

        /// <summary>
        /// Add data from the friend's game.
        /// </summary>
        /// <param name="from">Index of the first input in <paramref name="inputs"/> within their full input list.</param>
        /// <param name="inputs">New inputs, in order.</param>
        /// <param name="inputTime">Their input time when they sent this.</param>
        public void Receive(int from, IReadOnlyList<GameInput> inputs, double inputTime, double now)
        {
            LastReceived = now;

            // Skip anything we already have (shouldn't happen, but be safe)
            int skip = Math.Max(0, _nextInputIndex - from);
            if (from > _nextInputIndex && !_gapLogged)
            {
                // Inputs travel reliably, so this means a bug or a resent setup. The mirror will drift from here on.
                YargLogger.LogWarning($"Link: lost {from - _nextInputIndex} input(s) from {Name}, their track may be inaccurate");
                _gapLogged = true;
            }

            int room = MAX_PENDING_INPUTS - _pending.Count;
            if (inputs.Count - skip > room && !_overflowLogged)
            {
                YargLogger.LogWarning($"Link: too many inputs waiting from {Name}, dropping some; their track may be inaccurate");
                _overflowLogged = true;
            }
            for (int i = skip; i < inputs.Count && i - skip < room; i++)
            {
                _pending.Add(inputs[i]);
            }
            _nextInputIndex = (int) Math.Min(int.MaxValue, Math.Max(_nextInputIndex, (long) from + inputs.Count));

            _watermark = Math.Max(_watermark, inputTime);
        }

        public void SetStats(int score, int bandBonus, int combo, float stars, int notesHit, int totalNotes)
        {
            Score = score;
            BandBonus = bandBonus;
            Combo = combo;
            Stars = stars;
            NotesHit = notesHit;
            TotalNotes = totalNotes;
        }

        /// <summary>
        /// Move received inputs into the player's input list.
        /// </summary>
        public void DrainInto(List<GameInput> inputs)
        {
            if (_pending.Count == 0) return;

            inputs.AddRange(_pending);
            _pending.Clear();
        }

        /// <summary>
        /// Work out the input time the mirrored player should be simulated at this frame.
        /// </summary>
        /// <param name="localInputTime">Our own <c>GameManager.InputTime</c>.</param>
        /// <param name="playbackTime">
        /// Input time to update the mirrored engine to: as close to ours as the received inputs allow, so it
        /// never misses a note whose input is still in flight. Never decreases.
        /// </param>
        /// <returns><c>false</c> if nothing has been received yet.</returns>
        public bool TryGetPlaybackTime(double localInputTime, out double playbackTime)
        {
            if (!HasData)
            {
                playbackTime = double.NaN;
                return false;
            }

            Lateness = localInputTime - _watermark;

            // Never simulate past what we've received (or past now), and never go backwards
            playbackTime = Math.Min(localInputTime, _watermark);
            playbackTime = Math.Max(playbackTime, _lastPlaybackTime);
            _lastPlaybackTime = playbackTime;
            Lag = localInputTime - playbackTime;
            _maxLag = Math.Max(_maxLag, Lag);
            return true;
        }

        /// <summary>The most <see cref="Lag"/> since this was last called, for diagnostics.</summary>
        public double TakeMaxLag()
        {
            double max = _maxLag;
            _maxLag = 0;
            return max;
        }
    }
}
