using System;
using System.IO;
using YARG.Core.Logging;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Making the transport for a session: the YargLink library, its settings, and its log lines.
    /// </summary>
    public partial class LinkController
    {
        /// <summary>The iroh transport, or <c>null</c> (with why) if it can't be used here.</summary>
        /// <remarks>
        /// Every transport gets a new identity, so a host's invite code stops working when its session ends, and
        /// a joiner can't be recognised from one session to the next.
        /// </remarks>
        private static NativeLinkTransport CreateNativeTransport(LinkRole role, out string problem)
        {
            string path;
            try
            {
                path = NativeLinkLibrary.GetPath();
            }
            catch (IOException e)
            {
                problem = $"couldn't prepare the YargLink library: {e.Message}";
                return null;
            }

            if (path == null)
            {
                problem = "there's no YargLink library for this platform";
                return null;
            }

            try
            {
                var transport = new NativeLinkTransport(path, new NativeLinkConfig
                {
                    // A joiner only talks to its host, so nobody else gets to connect to it
                    Accept = role == LinkRole.Host,
                    MaxPeers = TransportMaxPeers,
                    LogFilter = NativeLogFilter(YargLogger.MinimumLogLevel),
                });
                transport.Log = LogNative;
                problem = null;
                return transport;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                or InvalidOperationException)
            {
                problem = e.Message;
                return null;
            }
        }

        /// <summary>
        /// The library's <c>log_filter</c> for the game's minimum log level. The library drops each line that its
        /// filter does not select, before the game sees it. Its default, <c>"warn,yarg_link=info"</c>, drops all
        /// debug lines. Thus when the player selects Debug or Trace in the settings, ask the library for its own
        /// lines at that level too. iroh's lines stay at "warn": at debug level, iroh writes many lines each second.
        /// The filter is read when the transport is made, so a change of the setting applies from the next session.
        /// </summary>
        private static string NativeLogFilter(LogLevel minimum) => minimum switch
        {
            LogLevel.Trace => "warn,yarg_link=trace",
            LogLevel.Debug => "warn,yarg_link=debug",
            _              => null,
        };

        private static void LogNative(NativeLogLevel level, string message)
        {
            switch (level)
            {
                case NativeLogLevel.Error:
                    YargLogger.LogError($"Link (native): {message}");
                    break;
                case NativeLogLevel.Warning:
                    YargLogger.LogWarning($"Link (native): {message}");
                    break;
                case NativeLogLevel.Info:
                    YargLogger.LogInfo($"Link (native): {message}");
                    break;
                default:
                    YargLogger.LogDebug($"Link (native): {message}");
                    break;
            }
        }

        /// <summary>
        /// Connections a transport takes at once: every joiner the session admits, plus room for a few still
        /// saying hello (which the session caps itself).
        /// </summary>
        private static int TransportMaxPeers
        {
            get
            {
                var defaults = new LinkSessionOptions();
                return defaults.MaxMembers + defaults.MaxUnadmittedPeers;
            }
        }
    }
}
