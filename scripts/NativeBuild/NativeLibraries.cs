internal enum NativeLibrary
{
    /// <summary>Native/YargAudio, built with CMake.</summary>
    Audio,

    /// <summary>Native/YargLink, built with cargo.</summary>
    Link,
}

/// <summary>One platform's binary of a native library, as committed to the Unity project.</summary>
internal interface IUnityPlugin
{
    NativePlatform Platform { get; }

    string BinaryName { get; }

    string PluginDirectory { get; }

    /// <summary>The GitHub Actions artifact that carries this binary and its .meta.</summary>
    string ArtifactName { get; }
}

internal static class UnityPluginPaths
{
    public static string PluginBinaryPath(
        this IUnityPlugin plugin, RepositoryLayout repository) =>
        Path.Combine(repository.Root, plugin.PluginDirectory, plugin.BinaryName);

    public static string PluginMetadataPath(
        this IUnityPlugin plugin, RepositoryLayout repository) =>
        plugin.PluginBinaryPath(repository) + ".meta";

    // The committed .meta is the source: Unity owns it, the build only copies it along.
    public static string MetadataSourcePath(
        this IUnityPlugin plugin, RepositoryLayout repository) =>
        plugin.PluginMetadataPath(repository);
}

internal static class NativeLibraries
{
    public static string DisplayName(NativeLibrary library) => library switch
    {
        NativeLibrary.Audio => "YargAudio",
        NativeLibrary.Link => "YargLink",
        _ => throw new ArgumentOutOfRangeException(nameof(library)),
    };

    public static string WorkflowFile(NativeLibrary library) => library switch
    {
        NativeLibrary.Audio => "native-audio.yml",
        NativeLibrary.Link => "native-link.yml",
        _ => throw new ArgumentOutOfRangeException(nameof(library)),
    };

    /// <summary>Prefix shared by the library's artifact names, e.g. "yarg-audio-".</summary>
    public static string ArtifactPrefix(NativeLibrary library) => library switch
    {
        NativeLibrary.Audio => "yarg-audio-",
        NativeLibrary.Link => "yarg-link-",
        _ => throw new ArgumentOutOfRangeException(nameof(library)),
    };

    public static IReadOnlyList<IUnityPlugin> Plugins(NativeLibrary library) => library switch
    {
        NativeLibrary.Audio => NativePlugins.All,
        NativeLibrary.Link => LinkPlugins.All,
        _ => throw new ArgumentOutOfRangeException(nameof(library)),
    };

    public static NativePlatform CurrentHost() =>
        OperatingSystem.IsWindows()
            ? NativePlatform.Windows
            : OperatingSystem.IsLinux()
                ? NativePlatform.Linux
                : OperatingSystem.IsMacOS()
                    ? NativePlatform.MacOS
                    : throw new ToolException(
                        "Unsupported host. NativeBuild supports Windows, Linux, and macOS.");
}
