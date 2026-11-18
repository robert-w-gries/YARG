using System;
using System.Linq;
using UnityEngine;

namespace YARG
{
    [DefaultExecutionOrder(-4999)]
    public static class CommandLineArgs
    {
        // Yes, the arguments should probably be prefixed with "--", however, this is based upon
        // Unity's existing command line arguments to make them consistent in style.

        /// <summary>
        /// Whether or not the game should be launched in offline mode. Offline mode disables
        /// online features such as fetching the OpenSource icons.
        /// </summary>
        private const string OFFLINE_ARG = "-offline";

        /// <summary>
        /// Defines whether we should save frame time data to replays
        /// </summary>
        private const string VERBOSE_REPLAYS = "-verbose-replays";

        /// <summary>
        /// Used to select the language the game will be launched in. The argument after should be
        /// the language code.
        /// </summary>
        private const string LANGUAGE_ARG = "-lang";

        /// <summary>
        /// Used to reference the download location of YARG and all of its setlists (by the launcher).
        /// The argument after should be the download location path.
        /// </summary>
        private const string DOWNLOAD_LOCATION_ARG = "-download-location";

        private const string PERSISTENT_DATA_PATH_ARG = "-persistent-data-path";

        /// <summary>
        /// Online link (development): join the session hosted at this ticket instead of hosting one.
        /// </summary>
        private const string LINK_JOIN_ARG = "-link-join";

        /// <summary>
        /// Online link (development): host a session at startup, without opening the lobby.
        /// </summary>
        private const string LINK_HOST_ARG = "-link-host";

        /// <summary>
        /// Online link (development): the name other players see.
        /// </summary>
        private const string LINK_NAME_ARG = "-link-name";

        /// <summary>
        /// Discord: use another Discord application for Rich Presence and invites, for testing with a private app.
        /// </summary>
        public const string DISCORD_APP_ID_ARG = "-discord-app-id";

        public static bool Offline { get; private set; }

        public static bool VerboseReplays { get; private set; }

        public static string Language           { get; private set; }
        public static string DownloadLocation   { get; private set; }
        public static string PersistentDataPath { get; private set; }

        public static string LinkJoin { get; private set; }
        public static bool   LinkHost { get; private set; }
        public static string LinkName { get; private set; }

        public static long? DiscordAppId { get; private set; }


        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void InitCommandLineArgs()
        {
            var args = Environment.GetCommandLineArgs();

            // Remember, the first argument is always the application itself
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case OFFLINE_ARG:
                        Offline = true;
                        break;
                    case VERBOSE_REPLAYS:
                        VerboseReplays = true;
                        break;
                    case LANGUAGE_ARG:
                        i++;
                        if (i < args.Length)
                        {
                            Language = args[i];
                        }

                        break;
                    case DOWNLOAD_LOCATION_ARG:
                        i++;
                        if (i < args.Length)
                        {
                            DownloadLocation = args[i];
                        }

                        break;
                    case PERSISTENT_DATA_PATH_ARG:
                        i++;
                        if (i < args.Length)
                        {
                            PersistentDataPath = args[i];
                        }

                        break;
                    case LINK_JOIN_ARG:
                        i++;
                        if (i < args.Length)
                        {
                            LinkJoin = args[i];
                        }

                        break;
                    case LINK_HOST_ARG:
                        LinkHost = true;
                        break;
                    case LINK_NAME_ARG:
                        i++;
                        if (i < args.Length)
                        {
                            LinkName = args[i];
                        }

                        break;
                    case DISCORD_APP_ID_ARG:
                        i++;
                        if (i < args.Length && long.TryParse(args[i], out long appId) && appId > 0)
                        {
                            DiscordAppId = appId;
                        }

                        break;
                }
            }
        }
    }
}