using System;
using System.Collections.Generic;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link
{
    /// <summary>
    /// The band fail meter in a synced song. The host's meter is the band's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each game mirrors its friends by replaying their inputs a network delay late, so if every game ran its own
    /// meter for the whole band, fails and saves would happen at slightly different moments and the games would
    /// disagree. Instead, on the host friends' mirrored players join the main <see cref="EngineManager"/>
    /// (<see cref="RemoteInputFeed.InHostBand"/>), so its fail meter works like local multiplayer. The host
    /// sends what it decides: a player failed (<see cref="PlayerFailedMessage"/>), the band was revived
    /// (<see cref="RevivedMessage"/>), the band failed (<see cref="FailedMessage"/>), and the meter's value with
    /// each player's part of it (<see cref="MeterMessage"/>, about ten times a second).
    /// </para>
    /// <para>
    /// The other games run their own meter with fail off, show the host's values on it (the band's fill and an
    /// icon for every player the host's meter counts, friends included), and apply the host's decisions: a
    /// failed player's highway goes down and every engine waits for a revive, so the next star power
    /// activation saves the band, as in local multiplayer.
    /// </para>
    /// </remarks>
    internal sealed class BandMeterSync
    {
        /// <summary>How often the host sends its meter's value, in seconds.</summary>
        private const double METER_INTERVAL = 0.1;

        private readonly SyncedSong    _song;
        private readonly EngineManager _engines;

        /// <summary>Host: failed players we've told everyone about (member ID, player index).</summary>
        private readonly HashSet<(string, int)> _failed = new();
        private double _nextSend;

        public BandMeterSync(SyncedSong song)
        {
            _song = song;
            _engines = song.GameManager.EngineManager;
            IsRemote = song.HostInPlay;

            if (song.IsHost)
            {
                _engines.OnPlayerFailed += OnHostPlayerFailed;
                _engines.OnPlayerRevived += OnHostPlayerRevived;
            }
        }

        /// <summary>
        /// Joiner: the host's meter is the band's. Not when the host isn't in this play of the song, which leaves
        /// our own meter in charge.
        /// </summary>
        public bool IsRemote { get; }

        /// <summary>Joiner: the host's meter, from its <see cref="MeterMessage"/>s.</summary>
        public HostMeterReadings Host { get; } = new();

        public void End()
        {
            if (_song.IsHost)
            {
                _engines.OnPlayerFailed -= OnHostPlayerFailed;
                _engines.OnPlayerRevived -= OnHostPlayerRevived;
            }
        }

        /// <summary>Whether the host's meter decides when <paramref name="gm"/>'s song fails.</summary>
        public bool IsRemoteFor(GameManager gm) => IsRemote && _song.GameManager == gm;

        /// <summary>The host's band meter, or <c>null</c> when this game's own meter is the band's.</summary>
        public float? HostHappinessFor(GameManager gm) => IsRemoteFor(gm) ? Host.Happiness : null;

        /// <summary>Host: send the meter's value now and then.</summary>
        public void Update(double now)
        {
            if (!_song.IsHost) return;

            var gm = _song.GameManager;
            if (gm == null || !gm.IsSongStarted || gm.Paused || now < _nextSend) return;
            _nextSend = now + METER_INTERVAL;

            // Each player's part, grouped by member, by player index, with null for a player the meter doesn't
            // count (a failed player, or one sitting out)
            var players = new Dictionary<string, List<float?>>();
            foreach (var player in gm.Players)
            {
                var container = player.EngineContainer;
                if (container == null || !_engines.Engines.Contains(container)) continue;
                if (!_song.TryGetPlayerKey(player, out string member, out int index)) continue;
                if (index >= LinkLimits.MAX_PLAYERS_PER_MEMBER) continue;

                if (!players.TryGetValue(member, out var values))
                {
                    values = new List<float?>();
                    players[member] = values;
                }
                while (values.Count <= index) values.Add(null);
                values[index] = (float) Math.Round(container.Happiness, 3);
            }

            _song.Send(new MeterMessage
            {
                Happiness = _engines.Happiness,
                Players = players,
            });
        }

        /// <summary>Host: a player's meter ran out (raised on every meter update until they're revived).</summary>
        private void OnHostPlayerFailed(int engineId)
        {
            var gm = _song.GameManager;
            if (gm == null) return;

            foreach (var player in gm.Players)
            {
                var container = player.EngineContainer;
                if (container == null || container.EngineId != engineId) continue;
                if (gm.EngineManagerFor(player.Player) != _engines) continue;

                if (!_song.TryGetPlayerKey(player, out string member, out int index) || !_failed.Add((member, index)))
                {
                    return;
                }

                YargLogger.LogInfo($"Link: {player.Player.Profile.Name} failed, telling friends");
                _song.Send(new PlayerFailedMessage { Member = member, Index = index });
                return;
            }
        }

        /// <summary>Host: star power saved the band.</summary>
        private void OnHostPlayerRevived()
        {
            if (_failed.Count == 0) return;

            _failed.Clear();
            YargLogger.LogInfo("Link: the band was revived, telling friends");
            _song.Send(new RevivedMessage());
        }

        /// <summary>Joiner: what the host's band meter decided.</summary>
        public void OnMessage(LinkMember member, SongPlayMessage message, double now)
        {
            if (!IsRemote || !member.IsHost || !_song.IsFromPlay(member, message)) return;

            var gm = _song.GameManager;
            if (gm == null || !gm.IsSongStarted || gm.PlayerHasFailed) return;

            switch (message)
            {
                case MeterMessage meter:
                    Host.Apply(meter, id => id == _song.Session.LocalMemberId || _song.FindMember(id) != null, now);
                    break;

                case PlayerFailedMessage failed:
                    FailPlayerFromHost(gm, failed.Member, failed.Index);
                    break;

                case RevivedMessage:
                    YargLogger.LogInfo("Link: the host's band meter revived the band");
                    gm.ReviveFromLink();
                    break;
            }
        }

        /// <summary>
        /// The fail meter's icons on a joiner: every player the host's meter counts, at the host's values. Our
        /// own players and friends' mirrored players use their highway's icon; friends' players without a
        /// highway (vocals, pro guitar) get their instrument's icon. Returns <c>null</c> when this game's own
        /// meter is the band's.
        /// </summary>
        public List<FailMeter.Marker> Markers(GameManager gm)
        {
            if (!IsRemoteFor(gm)) return null;

            var markers = new List<FailMeter.Marker>();
            foreach (var player in gm.Players)
            {
                var container = player.EngineContainer;
                if (container == null || !_song.TryGetPlayerKey(player, out string member, out int index))
                {
                    continue;
                }

                bool local = !player.IsRemote;
                var engines = gm.EngineManagerFor(player.Player);
                markers.Add(new FailMeter.Marker
                {
                    SpriteName = container.GetInstrumentSprite(gm.Song),
                    Color = player.Player.GetGameplayIconColor(),
                    Happiness = () => PlayerHappiness(member, index, local ? (container, engines) : default),
                    IsStarPowerActive = () => container.BaseEngine.BaseStats.IsStarPowerActive,
                });
            }

            // Friends' players that have no highway here can still be on the host's meter
            foreach (var setup in _song.Friends.Values)
            {
                foreach (var feed in setup.Feeds)
                {
                    if (feed.Mirrored) continue;

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

                    string member = feed.Member;
                    int index = feed.Index;
                    markers.Add(new FailMeter.Marker
                    {
                        SpriteName = spriteName,
                        Happiness = () => PlayerHappiness(member, index, default),
                        IsStarPowerActive = () => false,
                    });
                }
            }

            return markers;
        }

        /// <summary>
        /// A player's value on the host's meter. Until the host's first <see cref="MeterMessage"/>, our own
        /// players show our own values, like the band's fill does (<see cref="HostHappinessFor"/>).
        /// </summary>
        private float? PlayerHappiness(string member, int index,
            (EngineManager.EngineContainer Container, EngineManager Engines) local)
        {
            if (Host.Happiness.HasValue)
            {
                return Host.TryGetPlayer(member, index, out float happiness) ? happiness : null;
            }

            return local.Container != null && local.Engines.Engines.Contains(local.Container)
                ? local.Container.Happiness
                : null;
        }

        private void FailPlayerFromHost(GameManager gm, string member, int index)
        {
            BasePlayer target = null;
            if (member == _song.Session.LocalMemberId)
            {
                var players = _song.Local.Players;
                if (index >= 0 && index < players.Count) target = players[index];
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
