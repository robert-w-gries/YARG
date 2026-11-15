using System;
using System.IO;
using YARG.Helpers;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Where the YargLink native library (<c>Native/YargLink</c>) is, for
    /// <see cref="Transport.NativeLinkTransport"/>.
    /// </summary>
    /// <remarks>
    /// Like YargAudio, the Editor loads a copy named after the plugin's timestamp, so the plugin in
    /// Assets can be rebuilt while the old copy stays loaded. A rebuilt library is only picked up after
    /// restarting the Editor: native libraries can't be unloaded. <c>YARG_LINK_LIBRARY</c> overrides
    /// the path, handy for pointing at <c>Native/YargLink/target/debug</c> while working on it.
    /// </remarks>
    public static class NativeLinkLibrary
    {
        /// <summary>The library to load, or <c>null</c> if this platform has none.</summary>
        public static string GetPath()
        {
            var overridden = Environment.GetEnvironmentVariable("YARG_LINK_LIBRARY");
            if (!string.IsNullOrEmpty(overridden))
            {
                return File.Exists(overridden) ? overridden : null;
            }

#if UNITY_EDITOR
            var source = SourcePluginPath();
            if (source == null || !File.Exists(source))
            {
                return null;
            }

            var shadowDir = Path.Combine(Path.GetTempPath(), "YargLinkShadow");
            var shadow = Path.Combine(shadowDir,
                $"{Path.GetFileNameWithoutExtension(source)}_{File.GetLastWriteTimeUtc(source).Ticks}" +
                Path.GetExtension(source));
            if (!File.Exists(shadow))
            {
                Directory.CreateDirectory(shadowDir);
                var temp = shadow + ".tmp";
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, shadow);
            }
            return shadow;
#else
            var data = PathHelper.ApplicationDataPath;
#if UNITY_STANDALONE_OSX
            var path = Path.Combine(data, "Plugins", "libyarg_link.dylib");
#elif UNITY_STANDALONE_LINUX
            var path = Path.Combine(data, "Plugins", "libyarg_link.so");
#elif UNITY_STANDALONE_WIN
            var path = Path.Combine(data, "Plugins", "x86_64", "yarg_link.dll");
#else
            string path = null;
#endif
            return path != null && File.Exists(path) ? path : null;
#endif
        }

#if UNITY_EDITOR
        private static string SourcePluginPath()
        {
            var plugins = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Plugins", "YargLink");
#if UNITY_EDITOR_OSX
            return Path.Combine(plugins, "Mac", "libyarg_link.dylib");
#elif UNITY_EDITOR_LINUX
            return Path.Combine(plugins, "Linux", "x86_64", "libyarg_link.so");
#elif UNITY_EDITOR_WIN
            return Path.Combine(plugins, "Windows", "x86_64", "yarg_link.dll");
#else
            return null;
#endif
        }
#endif
    }
}
