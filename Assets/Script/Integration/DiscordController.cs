using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using Discord;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Core.Utility;
using YARG.Integration.Link;
using YARG.Integration.Link.Session;
using YARG.Localization;
using YARG.Menu.Persistent;
using YARG.Settings;

namespace YARG.Integration
{
    public enum DiscordRichPresenceMode
    {
        Show,
        Limited,
        Hide
    }

    public class DiscordController : MonoSingleton<DiscordController>
    {
        private const long APPLICATION_ID = 1091177744416637028;

        /// <summary>
        /// Environment variable for <see cref="CommandLineArgs.DiscordAppId"/>: another Discord application to use,
        /// for testing with a private app.
        /// </summary>
        private const string APP_ID_VARIABLE = "YARG_DISCORD_APP_ID";

        private const string ALBUM_API_URL = "https://api.enchor.us/album-art";

        // Discord's secrets and party IDs are 128-byte buffers, including the terminator
        private const int MAX_SECRET_LENGTH = 127;

#if UNITY_EDITOR || YARG_DEV_BUILD || YARG_TEST_BUILD
        private const string LARGE_TEXT_KEY = "Discord.Default.LargeText.Dev";
        private const string LARGE_ICON_KEY = "icon_dev";
#elif YARG_NIGHTLY_BUILD
        private const string LARGE_TEXT_KEY = "Discord.Default.LargeText.Nightly";
        private const string LARGE_ICON_KEY = "icon_nightly";
#else
        private const string LARGE_TEXT_KEY = "Discord.Default.LargeText.Stable";
        private const string LARGE_ICON_KEY = "icon_stable";
#endif

        private bool _initialized = false;
        private Discord.Discord _discord;

        // Keep track of this in case we want to update the value asynchronously
        private Activity _activity;

        private bool _wasInGameplay;
        private bool _wasPaused;

        private long _gameStartTime;
        private long _songStartTime;
        private long _pauseTime;

        private string _albumUrl;

        // The online session we advertise, so friends can join from Discord. Null when there's none to join.
        private string _partyId;
        private string _joinSecret;
        private int _partySize;
        private int _partyMax;

        // An invite code that was too long to advertise, so it's only logged once
        private string _unadvertisedCode;

        // An invite accepted in Discord, waiting for us to be back in the menu
        private string _pendingJoin;
        private bool _toldPendingJoin;

        // Whether we've already logged that Discord couldn't be reached, so retrying stays quiet until it connects
        private bool _toldNotConnected;

        // Whether we were in an online session last frame, so we only try to reach Discord again on joining one
        private bool _wasOnline;

        public void Initialize()
        {
            _initialized = true;

            CreateInstance();
        }

        public void CreateInstance()
        {
            // Skip if loading screen hasn't finished loading localization (localization is required for rich presence to function normally)
            if (!_initialized)
            {
                return;
            }

            // Don't create instance if Discord rich presence is turned off in settings
            if (SettingsManager.Settings.DiscordRichPresence.Value == DiscordRichPresenceMode.Hide)
            {
                return;
            }

            // Don't create new instance if instance already exists
            if (_discord is not null)
            {
                return;
            }

            long appId = ApplicationId;
            try
            {
                _discord = new Discord.Discord(appId, (ulong) CreateFlags.NoRequireDiscord);
            }
            catch (ResultException e) when (e.Result == Result.InternalError)
            {
                // We try again on focus and on going online, so this is only worth saying once
                if (!_toldNotConnected)
                {
                    _toldNotConnected = true;
                    YargLogger.LogWarning("Failed to start Discord presence client: Discord is not running or " +
                        "unreachable. Trying again when the game is focused or goes online.");
                }

                _discord = null;
                return;
            }
            catch (Exception e)
            {
                if (!_toldNotConnected)
                {
                    _toldNotConnected = true;
                    YargLogger.LogException(e, "Failed to start Discord presence client.");
                }

                _discord = null;
                return;
            }

            if (_toldNotConnected)
            {
                _toldNotConnected = false;
                YargLogger.LogInfo("Discord: connected");
            }

            if (appId != APPLICATION_ID)
            {
                YargLogger.LogFormatInfo("Discord: using application {0} instead of YARG's", appId);
            }

            // Listen to the changing of states
            GameStateFetcher.GameStateChange += OnGameStateChange;

            ListenForInvites();

            // Get the start time of the game (Discord requires it in this format)
            _gameStartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            // Set default activity
            SetDefaultActivity();
        }

        private void OnGameStateChange(GameStateFetcher.State state)
        {
            // Skip if Discord is not initialized
            if (_discord is null)
            {
                return;
            }

            // Set default activity if the user is not playing, or if the Discord rich presence setting is set to limited
            if (state.CurrentScene != SceneIndex.Gameplay || SettingsManager.Settings.DiscordRichPresence.Value == DiscordRichPresenceMode.Limited)
            {
                _wasInGameplay = false;
                _wasPaused = false;

                _albumUrl = null;

                SetDefaultActivity();
                return;
            }

            var song = state.SongEntry;

            if (song is null)
            {
                return;
            }

            // If it's the first time entering the song, collect all the info we need
            if (!_wasInGameplay)
            {
                _songStartTime = GetUnixTime();
            }

            // Deal with pausing
            if (state.Paused)
            {
                // Deal with starting to pause...

                _pauseTime = GetUnixTime();

                _wasPaused = true;
            }
            else if (_wasPaused)
            {
                // Deal with unpausing...

                var timePausedFor = GetUnixTime() - _pauseTime;
                _songStartTime += timePausedFor;

                _wasPaused = false;
            }

            // Localize Discord Rich Presence
            // Discord shows text as written, so the song's style tags are stripped

            var discordDetails = Localize.KeyFormat("Discord.Song.Name",
                RichTextUtils.StripRichTextTags(song.Name), RichTextUtils.StripRichTextTags(song.Artist));

            string discordState;
            if (state.Paused)
            {
                discordState = Localize.Key("Discord.Song.Paused");
            }
            else
            {
                discordState = Localize.KeyFormat("Discord.Song.Album", RichTextUtils.StripRichTextTags(song.Album));
            }

            // Get activity
            _activity = new Activity
            {
                Assets =
                {
                    LargeImage = LARGE_ICON_KEY,
                    LargeText = Localize.Key(LARGE_TEXT_KEY)
                },
                Details = discordDetails,
                State = discordState,
                Timestamps =
                {
                    // If it's paused, don't show the time elapsed
                    Start = state.Paused ? 0 : _songStartTime
                }
            };

            // If the album art was already loaded at this point, set it
            UpdateActivityWithAlbumArt();

            SetActivity(_activity);

            // Attempt to load album art (this will automatically update the activity)
            if (!_wasInGameplay && _albumUrl is null)
            {
                LoadAlbumArt(song).Forget();
            }

            _wasInGameplay = true;
        }

        private async UniTask LoadAlbumArt(SongEntry song)
        {
            // Try to get the album art from the Chorus Encore API
            try
            {
                using var client = new HttpClient();

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(ALBUM_API_URL),
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["name"] = song.Name,
                        ["artist"] = song.Artist,
                        ["charter"] = song.Charter
                    })
                };

                var response = await client.SendAsync(request);

                // If it's a 404 (the chart was not found) we can safely skip
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }

                // If it's a non-404 and a non-success code, we should throw
                response.EnsureSuccessStatusCode();

                _albumUrl = await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to fetch album art for song.");
            }

            UpdateActivityWithAlbumArt();
            SetActivity(_activity);
        }

        private void Update()
        {
            // Discord may have been started after the game, and invites only work once we've reached it
            bool online = IsOnline();
            if (online && !_wasOnline)
            {
                CreateInstance();
            }

            _wasOnline = online;

            if (_discord is null)
            {
                return;
            }

            UpdateParty();
            TryPendingJoin();

            try
            {
                _discord.RunCallbacks();
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to run Discord client callbacks!");

                TryDispose();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // Coming back to the game is the usual moment after opening Discord. Does nothing if we're
            // already connected.
            if (hasFocus)
            {
                CreateInstance();
            }
        }

        private void OnDestroy() {
            GameStateFetcher.GameStateChange -= OnGameStateChange;
        }

        private void OnApplicationQuit()
        {
            TryDispose();
        }

        public void TryDispose()
        {
            if (_discord == null)
            {
                return;
            }

            try
            {
                GameStateFetcher.GameStateChange -= OnGameStateChange;

                _discord.GetActivityManager().ClearActivity(_ => { });
                _discord.Dispose();
                _discord = null;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to dispose of Discord client.");
            }
        }

        private void UpdateActivityWithAlbumArt()
        {
            if (_albumUrl is null)
            {
                return;
            }

            // Make the small icon YARG instead of the big one
            _activity.Assets.SmallImage = LARGE_ICON_KEY;

            // Discord supports URLs as the large image
            _activity.Assets.LargeImage = _albumUrl;
        }

        private void SetActivity(Activity activity)
        {
            if (_joinSecret is not null)
            {
                activity.Party = new ActivityParty
                {
                    Id = _partyId,
                    Size = new PartySize
                    {
                        CurrentSize = _partySize,
                        MaxSize = _partyMax
                    },
                    // Friends join through an invite or by asking, not from anyone's profile
                    Privacy = ActivityPartyPrivacy.Private
                };
                activity.Secrets = new ActivitySecrets
                {
                    Join = _joinSecret
                };

                // Left at zero, Discord treats the invite as joinable from nowhere ("Join from a supported
                // device"). The game only runs on desktop.
                activity.SupportedPlatforms = (uint) ActivitySupportedPlatformFlags.Desktop;
            }

            _discord?.GetActivityManager().UpdateActivity(activity, result =>
            {
                if (result != Result.Ok)
                {
                    YargLogger.LogFormatWarning("Discord Activity Error: ", result);
                }
            });
        }

        private void SetDefaultActivity()
        {
            _activity = new Activity
            {
                Assets =
                {
                    // The image and key are defined in the Discord developer portal
                    LargeImage = LARGE_ICON_KEY,
                    LargeText = Localize.Key(LARGE_TEXT_KEY)
                },
                Details = Localize.Key("Discord.Default.Details"),
                State = Localize.Key("Discord.Default.State"),
                Timestamps =
                {
                    Start = _gameStartTime
                }
            };

            SetActivity(_activity);
        }

        #region Invites

        private void ListenForInvites()
        {
            var activities = _discord.GetActivityManager();
            activities.OnActivityJoin += OnActivityJoin;

#if !UNITY_EDITOR
            // Lets Discord start the game when someone accepts an invite while it's closed. In the editor this
            // would register the editor itself, so it's skipped there.
            try
            {
                // With another application, Discord has to start the game with it too, or the invite never
                // reaches us
                if (ApplicationId == APPLICATION_ID)
                {
                    activities.RegisterCommand();
                }
                else
                {
                    string exe = Environment.GetCommandLineArgs()[0];
                    activities.RegisterCommand(
                        $"\"{exe}\" {CommandLineArgs.DISCORD_APP_ID_ARG} {ApplicationId}");
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to register the game's launch command with Discord.");
            }
#endif
        }

        /// <summary>
        /// Advertise the online session we're in, or stop advertising it, when that changes.
        /// </summary>
        private void UpdateParty()
        {
            GetParty(out string id, out string secret, out int size, out int max);
            if (id == _partyId && secret == _joinSecret && size == _partySize && max == _partyMax)
            {
                return;
            }

            _partyId = id;
            _joinSecret = secret;
            _partySize = size;
            _partyMax = max;

            SetActivity(_activity);
        }

        private void GetParty(out string id, out string secret, out int size, out int max)
        {
            id = null;
            secret = null;
            size = 0;
            max = 0;

            // A joiner only advertises the session once the host has let them in
            if (!IsOnline())
            {
                return;
            }

            var link = LinkController.Instance;
            var session = link.CurrentSession;
            string code = link.InviteCode;

            // The default invite code is a bare endpoint ID, which fits. One with addresses might not, and
            // cutting it short would send friends somewhere else, so it isn't advertised.
            if (Encoding.UTF8.GetByteCount(code) > MAX_SECRET_LENGTH)
            {
                if (code != _unadvertisedCode)
                {
                    _unadvertisedCode = code;
                    YargLogger.LogWarning("Discord: the invite code is too long for a Discord invite, so friends " +
                        "can't join from Discord.");
                }

                return;
            }

            // Everyone in the session has the same party, without showing the join secret to anyone who can
            // see our activity
            id = PartyId(code);
            secret = code;
            size = session.Members.Count + 1;
            max = Math.Max(size, new LinkSessionOptions().MaxMembers + 1);
        }

        /// <summary>
        /// Whether we're in an online session that others can join: hosting one, or let into one.
        /// </summary>
        private static bool IsOnline()
        {
            var link = LinkController.Instance;
            return LinkController.IsEnabled && link is not null && link.CurrentSession is { IsConnected: true } &&
                !string.IsNullOrEmpty(link.InviteCode);
        }

        private static string PartyId(string code)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(code));
            return BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Called (from <see cref="Discord.Discord.RunCallbacks"/>) when the player accepts an invite or a
        /// friend accepts their request to join, including when Discord started the game for it.
        /// </summary>
        private void OnActivityJoin(string secret)
        {
            YargLogger.LogInfo("Discord: joining a friend's online session");

            _pendingJoin = secret;
            _toldPendingJoin = false;
            TryPendingJoin();
        }

        private void TryPendingJoin()
        {
            if (_pendingJoin is null)
            {
                return;
            }

            var link = LinkController.Instance;
            if (GlobalVariables.Instance == null || link is null)
            {
                return;
            }

            if (!LinkController.IsEnabled)
            {
                _pendingJoin = null;
                ToastManager.ToastWarning("To join friends from Discord, turn on Settings › Experimental › " +
                    "Enable online link, then accept the invite again.");
                return;
            }

            // Already there (or hosting it)
            if (_pendingJoin == link.InviteCode)
            {
                _pendingJoin = null;
                return;
            }

            // Joining ends the session we're in, so don't do it in the middle of a song
            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu)
            {
                if (!_toldPendingJoin)
                {
                    _toldPendingJoin = true;
                    ToastManager.ToastInformation("You'll join your friend's session when you're back in the menu.");
                }

                return;
            }

            string code = _pendingJoin;
            _pendingJoin = null;
            link.Join(code);
        }

        #endregion

        /// <summary>
        /// The Discord application to use: YARG's, unless <see cref="CommandLineArgs.DiscordAppId"/> or
        /// <see cref="APP_ID_VARIABLE"/> names another.
        /// </summary>
        private static long ApplicationId
        {
            get
            {
                if (CommandLineArgs.DiscordAppId is { } fromArgs)
                {
                    return fromArgs;
                }

                return long.TryParse(Environment.GetEnvironmentVariable(APP_ID_VARIABLE), out long fromEnv) &&
                    fromEnv > 0 ? fromEnv : APPLICATION_ID;
            }
        }

        private static long GetUnixTime()
        {
            return DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }
    }
}