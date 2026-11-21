using System.Collections.Generic;
using YARG.Core.Logging;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Logs when a connection switches between direct and relayed, which says a lot about how a session will
    /// feel.
    /// </summary>
    internal sealed class ConnectionLog
    {
        private const double CHECK_INTERVAL = 2.0;

        // How we last saw each directly connected member's connection
        private readonly Dictionary<string, LinkPath> _paths = new();
        private double _nextCheck;

        public void Update(LinkSession session, double now)
        {
            if (now < _nextCheck) return;
            _nextCheck = now + CHECK_INTERVAL;

            foreach (var member in session.Members)
            {
                if (member.Peer == null) continue;

                var stats = session.GetStats(member);
                if (!stats.Connected) continue;
                if (_paths.TryGetValue(member.Id, out var last) && last == stats.Path) continue;

                _paths[member.Id] = stats.Path;
                string rtt = stats.Rtt is { } r ? $"{r.TotalMilliseconds:0} ms" : "unknown";
                YargLogger.LogInfo($"Link: connection to {member.Name} is {stats.Path}, round trip {rtt}");
            }
        }

        public void Clear() => _paths.Clear();
    }
}
