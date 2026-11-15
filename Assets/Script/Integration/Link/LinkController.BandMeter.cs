using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using YARG.Core.Engine;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link
{
    /// <summary>
    /// The band fail meter in an online session. The host's meter is the band's:
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each game mirrors its friends by replaying their inputs about 150 ms late, so if every game ran its own
    /// meter for the whole band, fails and saves would happen at slightly different moments and the games would
    /// disagree. Instead, on the host friends' mirrored players join the main <see cref="EngineManager"/>
    /// (<see cref="RemoteInputFeed.InHostBand"/>), so its fail meter works like local multiplayer. The host
    /// sends what it decides: a player failed (<c>player_failed</c>), the band was revived (<c>revived</c>),
    /// the band failed (<c>failed</c>), and the meter's value (<c>meter</c>, about ten times a second).
    /// </para>
    /// <para>
    /// The other games run their own meter with fail off, show the host's value on it, and apply the host's
    /// decisions: a failed player's highway goes down and every engine waits for a revive, so the next star
    /// power activation saves the band, as in local multiplayer.
    /// </para>
    /// </remarks>
    public partial class LinkController
    {
        private const string KIND_PLAYER_FAILED = "player_failed";
        private const string KIND_REVIVED       = "revived";
        private const string KIND_METER         = "meter";

        /// <summary>How often the host sends its meter's value.</summary>
        private const double METER_INTERVAL = 0.1;

        private sealed class BandMeter
        {
            public GameManager   GameManager;
            public EngineManager Engines;
            public string        SongHash;
            public bool          IsHost;

            /// <summary>Host: failed players we've told everyone about (member ID, player index).</summary>
            public readonly HashSet<(string, int)> Failed = new();
            public double NextSend;

            /// <summary>Joiner: the host's meter, once it has said.</summary>
            public float? HostHappiness;
        }

        private BandMeter _band;

        /// <summary>
        /// The host's band meter, for <see cref="GameManager.BandHappiness"/>, or <c>null</c> when this game's own
        /// meter is the band's.
        /// </summary>
        public float? HostBandHappiness(GameManager gameManager) =>
            IsBandMeterRemote(gameManager) ? _band.HostHappiness : null;

        /// <summary>
        /// Whether the host's meter decides when this song fails, so this game's own meter mustn't.
        /// </summary>
        public bool IsBandMeterRemote(GameManager gameManager) =>
            _band is { IsHost: false } && _band.GameManager == gameManager;

        private void StartBandMeter(GameManager gameManager)
        {
            EndBandMeter();
            _band = new BandMeter
            {
                GameManager = gameManager,
                Engines = gameManager.EngineManager,
                SongHash = _shared.SongHash,
                IsHost = _session.Role == LinkRole.Host,
            };

            if (_band.IsHost)
            {
                _band.Engines.OnPlayerFailed += OnHostPlayerFailed;
                _band.Engines.OnPlayerRevived += OnHostPlayerRevived;
            }
        }

        private void EndBandMeter()
        {
            if (_band is { IsHost: true })
            {
                _band.Engines.OnPlayerFailed -= OnHostPlayerFailed;
                _band.Engines.OnPlayerRevived -= OnHostPlayerRevived;
            }
            _band = null;
        }

        /// <summary>Host: send the meter's value now and then.</summary>
        private void UpdateBandMeter()
        {
            if (_band is not { IsHost: true }) return;

            var gm = _band.GameManager;
            if (gm == null || !gm.IsSongStarted || gm.Paused || Now < _band.NextSend) return;
            _band.NextSend = Now + METER_INTERVAL;

            // Unreliable: only the latest value matters
            _session.Broadcast(KIND_METER, new JObject
            {
                ["song"] = _band.SongHash,
                ["happiness"] = _band.Engines.Happiness,
            }, Delivery.Unreliable);
        }

        /// <summary>Host: a player's meter ran out (raised on every meter update until they're revived).</summary>
        private void OnHostPlayerFailed(int engineId)
        {
            var gm = _band?.GameManager;
            if (gm == null || _shared == null) return;

            foreach (var player in gm.Players)
            {
                var container = player.EngineContainer;
                if (container == null || container.EngineId != engineId) continue;
                if (gm.EngineManagerFor(player.Player) != _band.Engines) continue;

                string member;
                int index;
                if (player.Player.RemoteFeed is { } feed)
                {
                    member = feed.Member;
                    index = feed.Index;
                }
                else
                {
                    member = _session.LocalMemberId;
                    index = _shared.Players.IndexOf(player);
                }

                if (index < 0 || !_band.Failed.Add((member, index))) return;

                YargLogger.LogInfo($"Link: {player.Player.Profile.Name} failed, telling friends");
                _session.Broadcast(KIND_PLAYER_FAILED, new JObject
                {
                    ["song"] = _band.SongHash,
                    ["member"] = member,
                    ["index"] = index,
                });
                return;
            }
        }

        /// <summary>Host: star power saved the band.</summary>
        private void OnHostPlayerRevived()
        {
            if (_band == null || _band.Failed.Count == 0) return;

            _band.Failed.Clear();
            YargLogger.LogInfo("Link: the band was revived, telling friends");
            _session.Broadcast(KIND_REVIVED, new JObject { ["song"] = _band.SongHash });
        }

        /// <summary>Joiner: what the host's band meter decided.</summary>
        private void OnBandMeterData(LinkMember member, string kind, JObject data)
        {
            if (_band is not { IsHost: false } || !member.IsHost) return;
            if ((string) data["song"] != _band.SongHash) return;

            var gm = _band.GameManager;
            if (gm == null || !gm.IsSongStarted || gm.PlayerHasFailed) return;

            switch (kind)
            {
                case KIND_METER:
                    _band.HostHappiness = (float?) data["happiness"];
                    break;

                case KIND_PLAYER_FAILED:
                    FailPlayerFromHost(gm, (string) data["member"], (int?) data["index"] ?? -1);
                    break;

                case KIND_REVIVED:
                    YargLogger.LogInfo("Link: the host's band meter revived the band");
                    gm.ReviveFromLink();
                    break;
            }
        }

        private void FailPlayerFromHost(GameManager gm, string member, int index)
        {
            BasePlayer target = null;
            if (member == _session.LocalMemberId)
            {
                if (index >= 0 && index < _shared.Players.Count) target = _shared.Players[index];
            }
            else
            {
                foreach (var player in gm.Players)
                {
                    if (player.Player.RemoteFeed is { } feed && feed.Member == member && feed.Index == index)
                    {
                        target = player;
                        break;
                    }
                }
            }

            YargLogger.LogInfo($"Link: the host's band meter failed {target?.Player.Profile.Name ?? "a player"}");

            // As in local multiplayer: everyone now waits for a revive, which the next star power activation
            // gives. Only the failed player's own container counts as failed.
            foreach (var player in gm.Players)
            {
                var container = player.EngineContainer;
                if (container == null) continue;
                container.PlayerHasFailed(player == target ? container.EngineId : int.MinValue, false);
            }

            if (target is TrackPlayer track)
            {
                track.FailFromLink();
            }
        }
    }
}
