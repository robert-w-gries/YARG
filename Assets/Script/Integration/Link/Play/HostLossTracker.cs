using System;

namespace YARG.Integration.Link.Play
{
    /// <summary>
    /// A joiner's countdown back to the library once the song has lost its host. Pure: the caller says what it
    /// sees each frame, and shows and acts on the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host runs the song for everyone (it resumes synced pauses, and its meter is the band's), and there's
    /// no host migration. So a joiner whose host is gone from the song (it left alone, the connection dropped,
    /// or the session ended) can't carry on for long: a pause would never resume.
    /// </para>
    /// <para>
    /// After a short grace (<see cref="GRACE"/>: the host finishing the song a moment before us isn't leaving it)
    /// the countdown is announced. A host that left isn't coming back, so the joiner waits
    /// <see cref="GRACE"/> + <see cref="LEFT_TIMEOUT"/>. A lost connection ends the song after
    /// <see cref="DepartureTracker.LOST_CONNECTION_DROP"/>, the same time the host takes to drop the joiner, so
    /// the joiner doesn't wait to reconnect only to be dropped.
    /// </para>
    /// </remarks>
    public sealed class HostLossTracker
    {
        /// <summary>Don't count the host as gone for this long, in seconds: it may have just finished the song.</summary>
        public const double GRACE = 2.0;

        /// <summary>The countdown after <see cref="GRACE"/>, in seconds, when the host left the song.</summary>
        public const double LEFT_TIMEOUT = 10.0;

        /// <summary>The countdown for a host that left the song, or ended the session.</summary>
        public const double HOST_LEFT = GRACE + LEFT_TIMEOUT;

        /// <summary>The countdown for a lost connection to the host.</summary>
        public const double LOST_CONNECTION = DepartureTracker.LOST_CONNECTION_DROP;

        /// <summary>The host is gone, and the countdown is running.</summary>
        public bool Watching { get; private set; }

        /// <summary>The countdown has been shown to the player.</summary>
        public bool Announced { get; private set; }

        /// <summary>Why the host is gone, for the player.</summary>
        public string Reason { get; private set; }

        /// <summary>Since when the host has been gone.</summary>
        public double Since { get; private set; }

        /// <summary>When the countdown runs out.</summary>
        public double Deadline { get; private set; }

        /// <summary>
        /// The host is gone: start the countdown, ending <paramref name="timeout"/> after
        /// <paramref name="since"/> (by default, <paramref name="now"/>). If it's already running for another
        /// reason, take the new reason, and never move the deadline later.
        /// </summary>
        /// <returns>Whether the reason changed after the countdown was shown, so the player should hear it.</returns>
        public bool Watch(string reason, double timeout, double now, double? since = null)
        {
            double start = since ?? now;
            double deadline = start + timeout;

            if (!Watching)
            {
                Watching = true;
                Announced = false;
                Reason = reason;
                Since = start;
                Deadline = deadline;
                return false;
            }

            if (Reason == reason) return false;

            Reason = reason;
            Deadline = Math.Min(Deadline, deadline);
            return Announced;
        }

        /// <summary>The host is back, or the song is over.</summary>
        /// <returns>Whether the countdown had been shown, so the player should hear that the host is back.</returns>
        public bool Clear()
        {
            bool announced = Watching && Announced;
            Watching = false;
            Announced = false;
            Reason = null;
            return announced;
        }

        /// <summary>Whether to show the countdown now: the grace is over. True once.</summary>
        public bool TryAnnounce(double now)
        {
            if (!Watching || Announced || now < Since + GRACE) return false;

            Announced = true;
            return true;
        }

        /// <summary>Whether the countdown ran out: go back to the library. Clears it.</summary>
        public bool TryExpire(double now)
        {
            if (!Watching || now < Deadline) return false;

            Watching = false;
            Announced = false;
            return true;
        }
    }
}
