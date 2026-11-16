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
    /// replay. The catch is that inputs arrive late, so playback runs a little behind the song: never past the
    /// newest time the friend has reported, and with a delay that adapts to how late their data is arriving.
    /// </remarks>
    public class RemoteInputFeed
    {
        /// <summary>Delay used until the first data arrives.</summary>
        private const double INITIAL_DELAY = 0.15;

        /// <summary>Extra delay on top of the worst recent lateness, to absorb jitter.</summary>
        private const double DELAY_MARGIN = 0.04;

        private const double MIN_DELAY = 0.05;

        /// <summary>Beyond this the friend has paused or is far out of sync; just follow them.</summary>
        private const double MAX_DELAY = 30.0;

        /// <summary>How fast the delay grows when data is late (0.5 = their track runs at half speed).</summary>
        private const double DELAY_GROW_RATE = 0.5;

        /// <summary>How fast the delay shrinks once data is on time again (0.1 = their track runs 10% fast).</summary>
        private const double DELAY_SHRINK_RATE = 0.1;

        /// <summary>How fast the remembered worst lateness decays, in seconds per second.</summary>
        private const double PEAK_DECAY_RATE = 0.05;

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
        public int   Combo    { get; private set; }
        public float Stars    { get; private set; }
        public int   NotesHit { get; private set; }
        public int   TotalNotes { get; private set; }

        public bool HasData => !double.IsNegativeInfinity(_watermark);

        /// <summary>When data last arrived, in <see cref="LinkController"/> clock seconds.</summary>
        public double LastReceived { get; private set; } = double.NegativeInfinity;

        /// <summary>Current playback delay behind the song, in seconds.</summary>
        public double Delay { get; private set; } = INITIAL_DELAY;

        private readonly List<GameInput> _pending = new();

        // Index of the next input we expect from the friend's input list
        private int _nextInputIndex;
        private bool _gapLogged;
        private bool _overflowLogged;

        // The friend's input time (before their calibration) at the moment they sent their latest data.
        // Every input up to this time has been received.
        private double _watermark = double.NegativeInfinity;

        private double _peakLateness;
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

        public void SetStats(int score, int combo, float stars, int notesHit, int totalNotes)
        {
            Score = score;
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
        /// <param name="deltaTime">Frame time, for smoothing the delay.</param>
        /// <param name="playbackTime">Input time to update the mirrored engine to; never decreases.</param>
        /// <returns><c>false</c> if nothing has been received yet.</returns>
        public bool TryGetPlaybackTime(double localInputTime, double deltaTime, out double playbackTime)
        {
            if (!HasData)
            {
                playbackTime = double.NaN;
                return false;
            }

            // How far behind us the friend's data is right now
            double lateness = localInputTime - _watermark;
            _peakLateness = Math.Max(lateness, _peakLateness - PEAK_DECAY_RATE * deltaTime);

            double target = Math.Clamp(_peakLateness + DELAY_MARGIN, MIN_DELAY, MAX_DELAY);
            Delay = target > Delay
                ? Math.Min(target, Delay + DELAY_GROW_RATE * deltaTime)
                : Math.Max(target, Delay - DELAY_SHRINK_RATE * deltaTime);

            // Never simulate past what we've received, and never go backwards
            playbackTime = Math.Min(localInputTime - Delay, _watermark);
            playbackTime = Math.Max(playbackTime, _lastPlaybackTime);
            _lastPlaybackTime = playbackTime;
            return true;
        }
    }
}
