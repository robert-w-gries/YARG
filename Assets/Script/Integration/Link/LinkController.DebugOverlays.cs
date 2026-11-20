using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;
using YARG.Settings;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Debug overlays for online play, each behind its own setting in Settings › Debug (off by default):
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Score box values</b> (<see cref="SettingsManager.SettingContainer.LinkDebugScoreBox"/>): this
    /// game's own values next to the ones the score box and fail meter show, which come from the whole band
    /// (score, stars) or from the host (band meter). For each friend's player, the mirror engine here next to
    /// what the friend's game reports. Refer to the authority table on <see cref="GameManager"/>.</item>
    /// <item><b>Connections</b> (<see cref="SettingsManager.SettingContainer.LinkDebugConnections"/>): the
    /// health of each member's connection, in any scene while a session is open.</item>
    /// </list>
    /// </remarks>
    public partial class LinkController
    {
        /// <summary>A reported value and our own differ by more than this before the panel highlights it.</summary>
        private const float HAPPINESS_TOLERANCE = 0.01f;

        /// <summary>How often the data rates in the connections panel are measured, in seconds.</summary>
        private const double RATE_INTERVAL = 1.0;

        private const string DIM   = "#909090";
        private const string GOOD  = "#80ff80";
        private const string WARN  = "#ffd060";
        private const string BAD   = "#ff8080";

        private GUIStyle _debugPanelStyle;

        /// <summary>The last data rate sample for each directly connected member, by member ID.</summary>
        private readonly Dictionary<string, RateSample> _rates = new();

        private struct RateSample
        {
            public double Time;
            public long   Sent;
            public long   Received;
            public double SentPerSecond;
            public double ReceivedPerSecond;
        }

        private static bool ShowScoreBoxDebug => SettingsManager.Settings?.LinkDebugScoreBox.Value ?? false;
        private static bool ShowConnectionsDebug => SettingsManager.Settings?.LinkDebugConnections.Value ?? false;

        #region Score box values

        /// <summary>The score box values panel, on the right below the score box.</summary>
        private void DrawScoreBoxDebug(GameManager gm)
        {
            if (!ShowScoreBoxDebug) return;

            var panel = new DebugPanel();
            bool isHost = _session.Role == LinkRole.Host;
            bool hostDecides = IsBandMeterRemote(gm);

            string meterAuthority;
            if (isHost)
            {
                meterAuthority = "this game (host)";
            }
            else if (!hostDecides)
            {
                meterAuthority = "this game (the host isn't in this play)";
            }
            else if (_band.HostHappiness == null)
            {
                meterAuthority = Tint("host (no meter received yet)", WARN);
            }
            else
            {
                double age = Now - _band.LastMeterAt;
                string ageText = $"{age * 1000:0} ms ago";
                meterAuthority = $"host (last update {(age > 1 ? Tint(ageText, BAD) : ageText)})";
            }

            panel.Title("Score box values");
            panel.Span(Tint($"Band meter: {meterAuthority}", DIM));
            panel.Span(Tint("Score and stars: added up by every game from each game's own reports", DIM));

            panel.Row(Tint("Band", DIM), Tint("this game", DIM), Tint("score box", DIM), Tint("diff", DIM));

            // This game's band score counts only its own players; the score box counts everyone
            int localScore = gm.BandScore;
            int shownScore = gm.ScoreBoxScore;
            panel.Row("Score", $"{localScore:N0}", $"{shownScore:N0}", Diff(shownScore - localScore, neutral: true));
            panel.Row("Stars", $"{gm.BandStars:0.00}", $"{gm.ScoreBoxStars:0.00}",
                Diff(gm.ScoreBoxStars - gm.BandStars, "0.00", neutral: true));

            float localMeter = gm.EngineManager.Happiness;
            float shownMeter = gm.BandHappiness;
            panel.Row("Meter", $"{localMeter:0.000}", $"{shownMeter:0.000}",
                Diff(shownMeter - localMeter, "0.000", HAPPINESS_TOLERANCE));
            panel.Row("Failed", gm.PlayerHasFailed ? Tint("yes", BAD) : "no", "", "");

            // Our own players: our engine is the authority for the score, the host for the meter
            panel.Row(Tint("Our players", DIM), Tint("this game", DIM), Tint("host", DIM), Tint("diff", DIM));
            foreach (var player in _shared.Players)
            {
                if (player == null) continue;
                string name = $"<b>{Escape(player.Player.Profile.Name)}</b>";
                panel.Row(name, $"{player.Score:N0}", "", "");
                AddMeterRow(panel, gm, player, isHost, hostDecides);
            }

            // Friends' players: the friend's game is the authority; the mirror here replays their inputs a
            // network delay late, so small differences come and go
            foreach (var setup in _songSetups.Values)
            {
                foreach (var feed in setup.Feeds)
                {
                    var mirror = FindMirror(gm, feed);
                    panel.Row(Tint($"{Escape(feed.Name)}", DIM), Tint("mirror", DIM), Tint("reported", DIM),
                        Tint("diff", DIM));

                    if (mirror == null)
                    {
                        panel.Row("  score", Tint("no highway", DIM), $"{feed.Score:N0}", "");
                        panel.Row("  stars", "", $"{feed.Stars:0.00}", "");
                        panel.Row("  notes", "", $"{feed.NotesHit}/{feed.TotalNotes}", "");
                    }
                    else
                    {
                        panel.Row("  score", $"{mirror.Score:N0}", $"{feed.Score:N0}",
                            Diff(feed.Score - mirror.Score));
                        panel.Row("  bonus", $"{mirror.BandBonusScore:N0}", $"{feed.BandBonus:N0}",
                            Diff(feed.BandBonus - mirror.BandBonusScore));
                        panel.Row("  stars", $"{mirror.Stars:0.00}", $"{feed.Stars:0.00}",
                            Diff(feed.Stars - mirror.Stars, "0.00"));
                        panel.Row("  combo", $"{mirror.Combo}", $"{feed.Combo}", Diff(feed.Combo - mirror.Combo));
                        panel.Row("  notes", $"{mirror.NotesHit}/{mirror.BaseStats.TotalNotes}",
                            $"{feed.NotesHit}/{feed.TotalNotes}", Diff(feed.NotesHit - mirror.NotesHit));
                        AddMeterRow(panel, gm, mirror, isHost, hostDecides);
                    }

                    if (!feed.HasData)
                    {
                        panel.Span(Tint("  nothing received yet", WARN));
                    }
                }
            }

            panel.Draw(this, alignRight: true, y: Screen.height * 0.3f);
        }

        /// <summary>
        /// A player's value on our meter and on the band's meter (the host's). On the host they're the same.
        /// </summary>
        private void AddMeterRow(DebugPanel panel, GameManager gm, BasePlayer player, bool isHost,
            bool hostDecides)
        {
            var container = player.EngineContainer;
            if (container == null)
            {
                panel.Row("  meter", Tint("none", DIM), "", "");
                return;
            }

            // On a joiner, friends' players are on an engine manager of their own, outside our meter
            bool onOurMeter = gm.EngineManagerFor(player.Player).Engines.Contains(container);
            string local = onOurMeter ? $"{container.Happiness:0.000}" : Tint($"{container.Happiness:0.000}*", DIM);

            if (isHost || !hostDecides)
            {
                panel.Row("  meter", local, Tint("(this game)", DIM), "");
                return;
            }

            if (!TryGetBandKey(player, out string member, out int index) ||
                !_band.HostPlayers.TryGetValue((member, index), out float host))
            {
                panel.Row("  meter", local, Tint(_band.HostHappiness == null ? "-" : "not counted", DIM), "");
                return;
            }

            panel.Row("  meter", local, $"{host:0.000}", Diff(host - container.Happiness, "0.000", HAPPINESS_TOLERANCE));
        }

        private static BasePlayer FindMirror(GameManager gm, RemoteInputFeed feed)
        {
            foreach (var player in gm.Players)
            {
                if (player.Player.RemoteFeed == feed) return player;
            }
            return null;
        }

        private static string Diff(int diff, bool neutral = false)
        {
            if (diff == 0) return Tint("=", DIM);
            string text = diff.ToString("+#,0;-#,0");
            return neutral ? text : Tint(text, WARN);
        }

        private static string Diff(float diff, string format, float tolerance = 0f, bool neutral = false)
        {
            if (Mathf.Abs(diff) <= Mathf.Max(tolerance, 1e-6f)) return Tint("=", DIM);
            string text = diff.ToString($"+{format};-{format}");
            return neutral ? text : Tint(text, WARN);
        }

        #endregion

        #region Connections

        /// <summary>The connection health panel, at the top left. Returns where the next left panel can go.</summary>
        private float DrawConnectionsDebug()
        {
            if (!ShowConnectionsDebug || _session == null) return 0;

            var panel = new DebugPanel();
            bool isHost = _session.Role == LinkRole.Host;

            string state;
            if (isHost)
            {
                state = Tint("hosting", GOOD);
            }
            else if (_session.IsConnected)
            {
                state = Tint("connected to the host", GOOD);
            }
            else if (_session.IsJoining)
            {
                state = Tint("joining", WARN);
            }
            else
            {
                state = Tint(_lastProblem == null ? "reconnecting" : $"reconnecting ({Escape(_lastProblem)})", BAD);
            }

            panel.Title("Connections");
            panel.Span($"{Escape(_session.LocalName)} · {state} · {_session.Members.Count} friend(s)");
            panel.Row(Tint("Member", DIM), Tint("link", DIM), Tint("rtt", DIM), Tint("up / down", DIM),
                Tint("total", DIM), Tint("clock", DIM), Tint("inputs", DIM), Tint("state", DIM));

            var seen = new HashSet<string>();
            foreach (var member in _session.Members)
            {
                seen.Add(member.Id);
                panel.Row(MemberCells(member, isHost));
            }

            // Forget rates of members who left
            if (_rates.Count > seen.Count)
            {
                var gone = new List<string>();
                foreach (var id in _rates.Keys)
                {
                    if (!seen.Contains(id)) gone.Add(id);
                }
                foreach (var id in gone) _rates.Remove(id);
            }

            if (_session.Members.Count == 0)
            {
                panel.Span(Tint("No friends in the session", DIM));
            }

            AddSongSyncRows(panel);

            return panel.Draw(this, alignRight: false, y: 16 * Scale);
        }

        private string[] MemberCells(LinkMember member, bool isHost)
        {
            string name = member.IsHost ? $"{Escape(member.Name)} {Tint("(host)", DIM)}" : Escape(member.Name);

            string link, rtt = "-", rates = "-", totals = "-";
            if (!isHost && !member.IsHost)
            {
                // Joiners only talk to the host, which passes everything on
                link = Tint("via host", DIM);
            }
            else
            {
                var stats = _session.GetStats(member);
                if (!stats.Connected)
                {
                    link = Tint("connecting", WARN);
                }
                else
                {
                    link = stats.Path switch
                    {
                        LinkPath.Direct => Tint("direct", GOOD),
                        LinkPath.Relay  => Tint("relay", WARN),
                        _               => "connected",
                    };

                    if (stats.Rtt is { } r)
                    {
                        double ms = r.TotalMilliseconds;
                        rtt = Tint($"{ms:0} ms", ms > 150 ? BAD : ms > 80 ? WARN : GOOD);
                    }

                    var rate = SampleRates(member.Id, stats);
                    rates = $"{Bytes(rate.SentPerSecond)}/s / {Bytes(rate.ReceivedPerSecond)}/s";
                    totals = $"{Bytes(stats.BytesSent)} / {Bytes(stats.BytesReceived)}";
                }
            }

            string clock = "-";
            if (_clocks.TryGetValue(member.Id, out var estimate))
            {
                clock = estimate.HasEstimate
                    ? Tint($"{estimate.Offset * 1000:+0;-0;0} ms", Math.Abs(estimate.Offset) > 0.05 ? WARN : GOOD)
                    : Tint("measuring", DIM);
                if (estimate.Resets > 0) clock += Tint($" ({estimate.Resets} reset)", DIM);
            }

            string inputs = "-";
            if (_lastInputsAt.TryGetValue(member.Id, out double at))
            {
                double age = Now - at;
                var gm = _shared?.GameManager;
                bool silent = gm != null && !gm.Paused && age > RemoteInputFeed.STALL_SECONDS;
                inputs = Tint($"{age * 1000:0} ms ago", silent ? BAD : age > 0.25 ? WARN : GOOD);
            }

            var flags = new List<string>();
            if (!string.IsNullOrEmpty(member.Status)) flags.Add(Escape(member.Status));
            else if (!string.IsNullOrEmpty(member.Scene)) flags.Add(Escape(member.Scene));
            if (IsConnectionPoor(member)) flags.Add(Tint("poor", BAD));
            if (_friendGoneSince.TryGetValue(member.Id, out double since))
            {
                flags.Add(Tint($"gone {Now - since:0.0} s", BAD));
            }
            if (_droppedFriends.Contains(member.Id)) flags.Add(Tint("dropped", BAD));

            return new[] { name, link, rtt, rates, totals, clock, inputs, string.Join(" · ", flags) };
        }

        /// <summary>Song-wide sync: our clock correction, pauses and a lost host.</summary>
        private void AddSongSyncRows(DebugPanel panel)
        {
            var gm = _shared?.GameManager;
            if (gm != null && gm.IsSongStarted)
            {
                string sync;
                if (_session.Role == LinkRole.Host)
                {
                    sync = "this game's clock is the reference";
                }
                else if (!_shared.HostInPlay)
                {
                    sync = Tint("the host isn't in this play; no correction", DIM);
                }
                else
                {
                    sync = _clockCorrecting ? Tint("easing toward the host", WARN) : Tint("in step", GOOD);
                    if (_clockCorrected != 0) sync += Tint($" · moved {_clockCorrected * 1000:+0;-0;0} ms so far", DIM);
                }
                panel.Span($"Song clock: {sync}");

                if (gm.Paused || _resumeAt != null)
                {
                    string pause = _resumeAt is { } at
                        ? Tint($"resuming in {Math.Max(0, at - Now):0.0} s", WARN)
                        : Tint("paused", WARN);
                    if (_pause is { Active: true, PausedBy: { } by })
                    {
                        string who = by == _session.LocalMemberId ? "you" : FindMember(by)?.Name ?? "a friend";
                        pause += Tint($" (by {Escape(who)})", DIM);
                    }
                    panel.Span($"Pause: {pause}");
                }
            }

            if (_hostGone != null)
            {
                panel.Span(Tint($"Host gone {Now - _hostGone.Since:0.0} s: {Escape(_hostGone.Reason ?? "unknown")}", BAD));
            }
        }

        private RateSample SampleRates(string memberId, PeerStats stats)
        {
            if (!_rates.TryGetValue(memberId, out var sample))
            {
                sample = new RateSample { Time = Now, Sent = stats.BytesSent, Received = stats.BytesReceived };
                _rates[memberId] = sample;
                return sample;
            }

            double elapsed = Now - sample.Time;
            if (elapsed < RATE_INTERVAL) return sample;

            sample.SentPerSecond = Math.Max(0, stats.BytesSent - sample.Sent) / elapsed;
            sample.ReceivedPerSecond = Math.Max(0, stats.BytesReceived - sample.Received) / elapsed;
            sample.Time = Now;
            sample.Sent = stats.BytesSent;
            sample.Received = stats.BytesReceived;
            _rates[memberId] = sample;
            return sample;
        }

        private static string Bytes(double bytes) => bytes switch
        {
            < 1024             => $"{bytes:0} B",
            < 1024 * 1024      => $"{bytes / 1024:0.0} KB",
            _                  => $"{bytes / (1024 * 1024):0.0} MB",
        };

        #endregion

        #region Drawing

        private static float Scale => Screen.height / 1080f;

        private static string Tint(string text, string color) => $"<color={color}>{text}</color>";

        /// <summary>
        /// A box of text in aligned columns. A row with one cell spans the whole box.
        /// </summary>
        private sealed class DebugPanel
        {
            private readonly List<string[]> _rows = new();
            public void Title(string text) => _rows.Add(new[] { $"<b>{text}</b>" });

            public void Span(string text) => _rows.Add(new[] { text });

            public void Row(params string[] cells) => _rows.Add(cells);

            /// <summary>Draws the box at <paramref name="y"/>, and returns the y just below it.</summary>
            public float Draw(LinkController owner, bool alignRight, float y)
            {
                float scale = Scale;
                owner._debugPanelStyle ??= new GUIStyle(GUI.skin.label)
                {
                    richText = true, wordWrap = false, padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                };
                var style = owner._debugPanelStyle;
                style.fontSize = Mathf.RoundToInt(16 * scale);

                float gap = 14 * scale;
                float pad = 10 * scale;
                float lineHeight = style.CalcHeight(new GUIContent("Ag"), 1000) + 2 * scale;

                // Column widths over the rows with columns; spanning rows only set the minimum width
                var widths = new List<float>();
                float spanWidth = 0;
                foreach (var row in _rows)
                {
                    if (row.Length == 1)
                    {
                        spanWidth = Mathf.Max(spanWidth, style.CalcSize(new GUIContent(row[0])).x);
                        continue;
                    }
                    for (int i = 0; i < row.Length; i++)
                    {
                        float w = style.CalcSize(new GUIContent(row[i] ?? "")).x;
                        if (i >= widths.Count) widths.Add(w);
                        else widths[i] = Mathf.Max(widths[i], w);
                    }
                }

                float columnsWidth = 0;
                for (int i = 0; i < widths.Count; i++) columnsWidth += widths[i] + (i > 0 ? gap : 0);

                float width = Mathf.Max(columnsWidth, spanWidth) + pad * 2;
                float height = _rows.Count * lineHeight + pad * 2;
                float x = alignRight ? Screen.width - width - 16 * scale : 16 * scale;
                var box = new Rect(x, y, width, height);

                var oldColor = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.DrawTexture(box, Texture2D.whiteTexture);
                GUI.color = oldColor;

                float rowY = y + pad;
                for (int r = 0; r < _rows.Count; r++)
                {
                    var row = _rows[r];
                    if (row.Length == 1)
                    {
                        GUI.Label(new Rect(x + pad, rowY, width - pad * 2, lineHeight), row[0], style);
                    }
                    else
                    {
                        float cellX = x + pad;
                        for (int i = 0; i < row.Length; i++)
                        {
                            GUI.Label(new Rect(cellX, rowY, widths[i] + 1, lineHeight), row[i] ?? "", style);
                            cellX += widths[i] + gap;
                        }
                    }
                    rowY += lineHeight;
                }

                return box.yMax + 8 * scale;
            }
        }

        #endregion
    }
}
