using System;
using System.Collections.Generic;

namespace YARG.Integration.Link.Play
{
    /// <summary>
    /// A joiner's copy of the host's band meter, from the host's <see cref="MeterMessage"/>s. The host's meter is
    /// the band's, so the joiner shows these values on its own meter.
    /// </summary>
    public sealed class HostMeterReadings
    {
        private readonly Dictionary<(string Member, int Index), float> _players = new();

        /// <summary>The band's fill on the host's meter, once the host has sent it.</summary>
        public float? Happiness { get; private set; }

        /// <summary>When the last <see cref="MeterMessage"/> arrived, for the debug overlay.</summary>
        public double LastAt { get; private set; } = double.NegativeInfinity;

        /// <summary>Take the values of a <see cref="MeterMessage"/> from the host.</summary>
        /// <param name="isMember">
        /// Whether a member ID is in this session (us included). Only those are kept, which also bounds how much a
        /// host can make us keep.
        /// </param>
        public void Apply(MeterMessage message, Func<string, bool> isMember, double now)
        {
            Happiness = message.Happiness;
            LastAt = now;

            if (message.Players == null) return;

            _players.Clear();
            foreach (var (member, values) in message.Players)
            {
                if (values == null || !isMember(member)) continue;

                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i] is { } happiness)
                    {
                        _players[(member, i)] = happiness;
                    }
                }
            }
        }

        /// <summary>
        /// A player's value on the host's meter (the member whose game they play on, and their index in that
        /// game's setup). <c>false</c> for a player the host's meter doesn't count.
        /// </summary>
        public bool TryGetPlayer(string member, int index, out float happiness) =>
            _players.TryGetValue((member, index), out happiness);
    }
}
