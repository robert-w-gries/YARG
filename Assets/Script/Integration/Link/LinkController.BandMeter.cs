using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Gameplay.HUD;
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
    /// Each game mirrors its friends by replaying their inputs a network delay late, so if every game ran its own
    /// meter for the whole band, fails and saves would happen at slightly different moments and the games would
    /// disagree. Instead, on the host friends' mirrored players join the main <see cref="EngineManager"/>
    /// (<see cref="RemoteInputFeed.InHostBand"/>), so its fail meter works like local multiplayer. The host
    /// sends what it decides: a player failed (<c>player_failed</c>), the band was revived (<c>revived</c>),
    /// the band failed (<c>failed</c>), and the meter's value with each player's part of it (<c>meter</c>,
    /// about ten times a second).
    /// </para>
    /// <para>
    /// The other games run their own meter with fail off, show the host's values on it (the band's fill and an
    /// icon for every player the host's meter counts, friends included), and apply the host's decisions: a
    /// failed player's highway goes down and every engine waits for a revive, so the next star power
    /// activation saves the band, as in local multiplayer.
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
            public string        PlayId;
            public bool          IsHost;

            /// <summary>
            /// Joiner: the host's meter is the band's. Not when the host isn't in this play of the song, which
            /// leaves our own meter in charge.
            /// </summary>
            public bool Remote;

            /// <summary>Host: failed players we've told everyone about (member ID, player index).</summary>
            public readonly HashSet<(string, int)> Failed = new();
            public double NextSend;

            /// <summary>Joiner: the host's meter, once it has said.</summary>
            public float? HostHappiness;

            /// <summary>
            /// Joiner: each player's value on the host's meter (member ID, player index), as of its last
            /// <c>meter</c> message. A player who isn't in it doesn't count toward the host's meter.
            /// </summary>
            public readonly Dictionary<(string, int), float> HostPlayers = new();
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
            _band is { Remote: true } && _band.GameManager == gameManager;

        private void StartBandMeter(GameManager gameManager)
        {
            EndBandMeter();
            _band = new BandMeter
            {
                GameManager = gameManager,
                Engines = gameManager.EngineManager,
                SongHash = _shared.SongHash,
                PlayId = _shared.PlayId,
                IsHost = _session.Role == LinkRole.Host,
                Remote = _shared.HostInPlay,
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

            // Each player's part, grouped by member: { member: [happiness, ...] } by player index, with null
            // for a player the meter doesn't count (a failed player, or one sitting out)
            var players = new JObject();
            foreach (var player in gm.Players)
            {
                var container = player.EngineContainer;
                if (container == null || !_band.Engines.Engines.Contains(container)) continue;
                if (!TryGetBandKey(player, out string member, out int index)) continue;
                if (index >= LinkLimits.MAX_PLAYERS_PER_MEMBER) continue;

                if (players[member] is not JArray values)
                {
                    values = new JArray();
                    players[member] = values;
                }
                while (values.Count <= index) values.Add(JValue.CreateNull());
                values[index] = Math.Round(container.Happiness, 3);
            }

            // Unreliable: only the latest value matters
            _session.Broadcast(KIND_METER, new JObject
            {
                ["song"] = _band.SongHash,
                ["play"] = _band.PlayId,
                ["happiness"] = _band.Engines.Happiness,
                ["players"] = players,
            }, Delivery.Unreliable);
        }

        /// <summary>
        /// Who a player is across the session: the member whose game they play on, and their index in that
        /// game's <c>setup</c>.
        /// </summary>
        private bool TryGetBandKey(BasePlayer player, out string member, out int index)
        {
            if (player.Player.RemoteFeed is { } feed)
            {
                member = feed.Member;
                index = feed.Index;
            }
            else
            {
                member = _session.LocalMemberId;
                index = _shared?.Players.IndexOf(player) ?? -1;
            }
            return index >= 0;
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

                if (!TryGetBandKey(player, out string member, out int index) ||
                    !_band.Failed.Add((member, index))) return;

                YargLogger.LogInfo($"Link: {player.Player.Profile.Name} failed, telling friends");
                _session.Broadcast(KIND_PLAYER_FAILED, new JObject
                {
                    ["song"] = _band.SongHash,
                    ["play"] = _band.PlayId,
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
            _session.Broadcast(KIND_REVIVED, new JObject { ["song"] = _band.SongHash, ["play"] = _band.PlayId });
        }

        /// <summary>Joiner: what the host's band meter decided.</summary>
        private void OnBandMeterData(LinkMember member, string kind, JObject data)
        {
            if (_band is not { Remote: true } || !member.IsHost) return;
            if ((string) data["song"] != _band.SongHash || !IsFromSongPlay(member, data)) return;

            var gm = _band.GameManager;
            if (gm == null || !gm.IsSongStarted || gm.PlayerHasFailed) return;

            switch (kind)
            {
                case KIND_METER:
                    _band.HostHappiness = SaneHappiness((float?) data["happiness"]);
                    if (data["players"] is JObject players)
                    {
                        ReadHostPlayers(players);
                    }
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

        /// <summary>Joiner: each player's value on the host's meter.</summary>
        private void ReadHostPlayers(JObject players)
        {
            _band.HostPlayers.Clear();
            foreach (var pair in players)
            {
                string member = pair.Key;
                var token = pair.Value;

                // Only members of this session, which also bounds how much a host can make us keep
                if (member != _session.LocalMemberId && FindMember(member) == null) continue;
                if (token is not JArray values) continue;

                int count = Math.Min(values.Count, LinkLimits.MAX_PLAYERS_PER_MEMBER);
                for (int i = 0; i < count; i++)
                {
                    if (values[i].Type is JTokenType.Float or JTokenType.Integer &&
                        SaneHappiness((float) values[i]) is { } happiness)
                    {
                        _band.HostPlayers[(member, i)] = happiness;
                    }
                }
            }
        }

        private static float? SaneHappiness(float? value) =>
            value is { } v && !float.IsNaN(v) && !float.IsInfinity(v) ? Mathf.Clamp01(v) : null;

        /// <summary>
        /// The fail meter's icons on a joiner: every player the host's meter counts, at the host's values. Our
        /// own players and friends' mirrored players use their highway's icon; friends' players without a
        /// highway (vocals, pro guitar) get their instrument's icon. Returns <c>null</c> when this game's own
        /// meter is the band's.
        /// </summary>
        public List<FailMeter.Marker> BandMeterMarkers(GameManager gameManager)
        {
            if (!IsBandMeterRemote(gameManager)) return null;

            var band = _band;
            var markers = new List<FailMeter.Marker>();

            foreach (var player in gameManager.Players)
            {
                var container = player.EngineContainer;
                if (container == null || !TryGetBandKey(player, out string member, out int index))
                {
                    continue;
                }

                var key = (member, index);
                bool local = !player.IsRemote;
                var engines = gameManager.EngineManagerFor(player.Player);
                markers.Add(new FailMeter.Marker
                {
                    SpriteName = container.GetInstrumentSprite(gameManager.Song),
                    Color = player.Player.GetGameplayIconColor(),
                    Happiness = () => HostPlayerHappiness(band, key, local ? (container, engines) : default),
                    IsStarPowerActive = () => container.BaseEngine.BaseStats.IsStarPowerActive,
                });
            }

            // Friends' players that have no highway here can still be on the host's meter
            foreach (var setup in _songSetups.Values)
            {
                foreach (var feed in setup.Feeds)
                {
                    if (feed.Mirrored) continue;

                    var key = (feed.Member, feed.Index);
                    string spriteName = null;
                    if (Enum.TryParse(feed.Instrument, out Instrument instrument))
                    {
                        spriteName = InstrumentIconProvider.GetInstrumentSprite(instrument);
                    }
                    else
                    {
                        // The game of the friend is newer or modified. The icon keeps the default image of the meter.
                        YargLogger.LogWarning($"Link: unknown instrument \"{feed.Instrument}\" for {feed.Name}, " +
                            "using the default fail meter icon");
                    }

                    markers.Add(new FailMeter.Marker
                    {
                        SpriteName = spriteName,
                        Happiness = () => HostPlayerHappiness(band, key, default),
                        IsStarPowerActive = () => false,
                    });
                }
            }

            return markers;
        }

        /// <summary>
        /// A player's value on the host's meter. Until the host's first <c>meter</c> message, our own players
        /// show our own values, like the band's fill does (<see cref="HostBandHappiness"/>).
        /// </summary>
        private static float? HostPlayerHappiness(BandMeter band, (string, int) key,
            (EngineManager.EngineContainer Container, EngineManager Engines) local)
        {
            if (band.HostHappiness.HasValue)
            {
                return band.HostPlayers.TryGetValue(key, out float happiness) ? happiness : null;
            }

            return local.Container != null && local.Engines.Engines.Contains(local.Container)
                ? local.Container.Happiness
                : null;
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
