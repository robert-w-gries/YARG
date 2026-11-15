using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using YARG.Integration.Link.Transport;

namespace YARG.Link.Tests
{
    /// <summary>
    /// Finds the YargLink library for tests: <c>YARG_LINK_LIBRARY</c> if set, else the newest build in
    /// <c>Native/YargLink/target</c> (run <c>cargo build</c> there first). Tests that need it are
    /// skipped when there's none, unless <c>YARG_LINK_REQUIRE_NATIVE=1</c> (set by NativeBuild, so a
    /// plugin check can't pass by skipping).
    /// </summary>
    internal static class NativeLibrary
    {
        private static readonly Lazy<string> _path = new(Find);

        public static string Path => _path.Value;

        public static NativeLinkTransport Transport()
        {
            if (Path == null)
            {
                const string message = "YargLink isn't built; run `cargo build` in Native/YargLink";
                if (Environment.GetEnvironmentVariable("YARG_LINK_REQUIRE_NATIVE") == "1")
                {
                    Assert.Fail(message);
                }
                Assert.Ignore(message);
            }

            // Localhost only: no relays and no DNS, so tests run offline
            return new NativeLinkTransport(Path, new NativeLinkConfig
            {
                Relay = false,
                AddressLookup = false,
                BindAddress = "127.0.0.1:0",
                TicketAddresses = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                LogFilter = "warn",
            });
        }

        private static string Find()
        {
            var fromEnv = Environment.GetEnvironmentVariable("YARG_LINK_LIBRARY");
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return File.Exists(fromEnv) ? fromEnv : null;
            }

            string name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yarg_link.dll"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libyarg_link.dylib"
                : "libyarg_link.so";

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var target = System.IO.Path.Combine(dir.FullName, "Native", "YargLink", "target");
                if (!Directory.Exists(target)) continue;

                return new[] { "debug", "release" }
                    .Select(profile => new FileInfo(System.IO.Path.Combine(target, profile, name)))
                    .Where(f => f.Exists)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName;
            }
            return null;
        }
    }
}
