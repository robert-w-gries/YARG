using System.Collections.Generic;
using YARG.Core.Logging;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;
using YARG.Menu;
using YARG.Player;

namespace YARG.Integration.Link
{
    /// <summary>
    /// What friends' lobbies show about us (our status and players), and how we describe friends and our
    /// connections to them.
    /// </summary>
    public partial class LinkController
    {
        /// <summary>How often our status and players are checked for changes, for friends' lobbies, in seconds.</summary>
        private const double STATE_INTERVAL = 0.25;

        private double _nextStateTime;

        // Member statuses, as shown in the lobby (see LinkSession.SetLocalState)
        public const string STATUS_BROWSING = "browsing";
        public const string STATUS_CHOOSING = "choosing";
        public const string STATUS_READY    = "ready";
        public const string STATUS_PLAYING  = "playing";
        public const string STATUS_RESULTS  = "results";

        /// <summary>The scene a member in a song reports (see <see cref="SceneName"/>).</summary>
        private const string SCENE_GAMEPLAY = "gameplay";

        // How we last saw each directly connected member's connection, to log changes
        private readonly Dictionary<string, LinkPath> _paths = new();
        private double _nextPathCheck;

        /// <summary>
        /// What we're doing and who's playing, for friends' lobbies. The session only sends changes.
        /// </summary>
        private void UpdateLocalState()
        {
            if (Now < _nextStateTime) return;
            _nextStateTime = Now + STATE_INTERVAL;

            var players = new List<PlayerInfo>();
            foreach (var player in PlayerContainer.Players)
            {
                if (player.SittingOut) continue;

                var profile = player.Profile;
                players.Add(new PlayerInfo
                {
                    Name = profile.Name,
                    Instrument = profile.CurrentInstrument.ToString(),
                    Difficulty = profile.CurrentDifficulty.ToString(),
                    Bot = profile.IsBot,
                });
            }

            _session.SetLocalState(LocalStatus(), players);
        }

        private string LocalStatus()
        {
            switch (GlobalVariables.Instance.CurrentScene)
            {
                case SceneIndex.Gameplay:
                    if (_waiting && !_startAt.HasValue) return STATUS_READY;
                    return IsSyncedPauseActive ? STATUS_PAUSED : STATUS_PLAYING;
                case SceneIndex.Score:
                    return STATUS_RESULTS;
                case SceneIndex.Menu:
                    bool choosing = _remoteRequestHash != null ||
                        MenuManager.Instance != null && MenuManager.Instance.CurrentMenu == MenuManager.Menu.DifficultySelect;
                    return choosing ? STATUS_CHOOSING : STATUS_BROWSING;
                default:
                    return SceneName(GlobalVariables.Instance.CurrentScene);
            }
        }

        /// <summary>A member's status for people, for example "Ready" or "Picking a part".</summary>
        public static string DescribeStatus(string status) => status switch
        {
            STATUS_BROWSING => "Browsing",
            STATUS_CHOOSING => "Picking a part",
            STATUS_READY    => "Ready",
            STATUS_PLAYING  => "Playing",
            STATUS_PAUSED   => "Paused",
            STATUS_RESULTS  => "Results",
            null or ""      => "In the session",
            _               => char.ToUpperInvariant(status[0]) + status.Substring(1),
        };

        /// <summary>
        /// How we're connected to a member, for people: "Direct · 34 ms", "Relay · 120 ms", or for a joiner's
        /// view of another joiner, "Through the host".
        /// </summary>
        public string DescribeConnection(LinkMember member)
        {
            if (_session == null || member == null) return "";

            // Joiners only talk to the host, which passes everything on
            if (_session.Role == LinkRole.Join && !member.IsHost)
            {
                return "Through the host";
            }

            var stats = _session.GetStats(member);
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
        /// Whether a member's connection is good enough to play on: <c>false</c> when it's relayed with a high
        /// round trip, so the lobby can highlight it.
        /// </summary>
        public bool IsConnectionPoor(LinkMember member)
        {
            if (_session == null || member == null) return false;
            if (_session.Role == LinkRole.Join && !member.IsHost) return false;

            var stats = _session.GetStats(member);
            return stats.Connected && stats.Rtt is { } rtt && rtt.TotalMilliseconds > 150;
        }

        /// <summary>
        /// Log when a connection switches between direct and relayed, which says a lot about how a
        /// session will feel.
        /// </summary>
        private void LogPathChanges()
        {
            if (Now < _nextPathCheck) return;
            _nextPathCheck = Now + 2.0;

            foreach (var member in _session.Members)
            {
                if (member.Peer == null) continue;

                var stats = _session.GetStats(member);
                if (!stats.Connected) continue;
                if (_paths.TryGetValue(member.Id, out var last) && last == stats.Path) continue;

                _paths[member.Id] = stats.Path;
                string rtt = stats.Rtt is { } r ? $"{r.TotalMilliseconds:0} ms" : "unknown";
                YargLogger.LogInfo($"Link: connection to {member.Name} is {stats.Path}, round trip {rtt}");
            }
        }

        private static string SceneName(SceneIndex scene) => scene switch
        {
            SceneIndex.Menu        => LinkSession.SCENE_MENU,
            SceneIndex.Gameplay    => SCENE_GAMEPLAY,
            SceneIndex.Score       => "score",
            SceneIndex.Calibration => "calibration",
            _                      => scene.ToString().ToLowerInvariant(),
        };
    }
}
