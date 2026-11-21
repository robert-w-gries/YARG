using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link
{
    /// <summary>
    /// What friends' lobbies show about a member (their status), and how we describe friends and our
    /// connections to them.
    /// </summary>
    public static class LinkStatus
    {
        // Member statuses, as shown in the lobby (see LinkSession.SetLocalState)
        public const string BROWSING = "browsing";
        public const string CHOOSING = "choosing";
        public const string READY    = "ready";
        public const string PLAYING  = "playing";
        public const string PAUSED   = "paused";
        public const string RESULTS  = "results";

        /// <summary>The scene a member in a song reports (see <see cref="SceneName"/>).</summary>
        public const string SCENE_GAMEPLAY = "gameplay";

        /// <summary>A round trip above this, in milliseconds, is too slow to play on comfortably.</summary>
        public const double POOR_RTT_MS = 150;

        /// <summary>A member's status for people, for example "Ready" or "Picking a part".</summary>
        public static string Describe(string status) => status switch
        {
            BROWSING   => "Browsing",
            CHOOSING   => "Picking a part",
            READY      => "Ready",
            PLAYING    => "Playing",
            PAUSED     => "Paused",
            RESULTS    => "Results",
            null or "" => "In the session",
            _          => char.ToUpperInvariant(status[0]) + status.Substring(1),
        };

        /// <summary>
        /// How we're connected to a member, for people: "Direct · 34 ms", "Relay · 120 ms", or for a joiner's
        /// view of another joiner, "Through the host".
        /// </summary>
        public static string DescribeConnection(LinkSession session, LinkMember member)
        {
            if (session == null || member == null) return "";

            // Joiners only talk to the host, which passes everything on
            if (session.Role == LinkRole.Join && !member.IsHost)
            {
                return "Through the host";
            }

            var stats = session.GetStats(member);
            if (!stats.Connected) return "Connecting";

            string path = stats.Path switch
            {
                LinkPath.Direct => "Direct",
                LinkPath.Relay  => "Relay",
                _               => "Connected",
            };
            return stats.Rtt is { } rtt ? $"{path} · {rtt.TotalMilliseconds:0} ms" : path;
        }

        /// <summary>
        /// Whether a member's connection is too slow to play on comfortably (a high round trip), so the lobby can
        /// highlight it.
        /// </summary>
        public static bool IsConnectionPoor(LinkSession session, LinkMember member)
        {
            if (session == null || member == null) return false;
            if (session.Role == LinkRole.Join && !member.IsHost) return false;

            var stats = session.GetStats(member);
            return stats.Connected && stats.Rtt is { } rtt && rtt.TotalMilliseconds > POOR_RTT_MS;
        }

        /// <summary>The scene name a member reports to the session.</summary>
        public static string SceneName(SceneIndex scene) => scene switch
        {
            SceneIndex.Menu        => LinkSession.SCENE_MENU,
            SceneIndex.Gameplay    => SCENE_GAMEPLAY,
            SceneIndex.Score       => LinkSession.SCENE_SCORE,
            SceneIndex.Calibration => "calibration",
            _                      => scene.ToString().ToLowerInvariant(),
        };
    }
}
