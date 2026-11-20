using System.Text.RegularExpressions;

/// <summary>A YargLink plugin and the Rust targets it is built from (two for macOS, joined by lipo).</summary>
internal sealed record LinkPlugin(
    NativePlatform Platform,
    string BinaryName,
    string PluginDirectory,
    string ArtifactName,
    string[] Targets) : IUnityPlugin;

internal static class LinkPlugins
{
    public static IReadOnlyList<LinkPlugin> All { get; } =
    [
        new(
            NativePlatform.Windows,
            "yarg_link.dll",
            "Assets/Plugins/YargLink/Windows/x86_64",
            "yarg-link-windows-x64",
            ["x86_64-pc-windows-msvc"]),
        new(
            NativePlatform.Linux,
            "libyarg_link.so",
            "Assets/Plugins/YargLink/Linux/x86_64",
            "yarg-link-linux-x64",
            ["x86_64-unknown-linux-gnu"]),
        new(
            NativePlatform.MacOS,
            "libyarg_link.dylib",
            "Assets/Plugins/YargLink/Mac",
            "yarg-link-macos-universal",
            ["aarch64-apple-darwin", "x86_64-apple-darwin"]),
    ];

    public static LinkPlugin ForCurrentHost()
    {
        NativePlatform platform = NativeLibraries.CurrentHost();
        return All.Single(plugin => plugin.Platform == platform);
    }
}

/// <summary>
/// Builds Native/YargLink with cargo, tests it, and copies it into the Unity project.
/// </summary>
/// <remarks>
/// The build is reproducible, so <c>--verify-committed-plugin</c> can compare bytes:
/// the toolchain is pinned by rust-toolchain.toml, dependencies by Cargo.lock (<c>--locked</c>),
/// and the paths rustc embeds (panic locations) are remapped to fixed prefixes, so a build in
/// one checkout matches a build in another. Windows isn't compared, like YargAudio: the MSVC
/// toolset on CI runners changes with the runner image.
/// </remarks>
internal static class LinkBuildCommand
{
    // Matches YargAudio and Unity 6's Ubuntu 20.04 support. Don't change one without the other.
    private const string GlibcFloor = "2.31";
    private const string LinuxImage = "ubuntu:20.04";

    // Unity 6's minimum macOS, and YargAudio's CMAKE_OSX_DEPLOYMENT_TARGET.
    private const string MacDeploymentTarget = "11.0";

    private const string RemappedSource = "/yarg-link";
    private const string RemappedCargoHome = "/cargo";

    /// <summary>
    /// The functions that the plugin must export: each function that <c>include/yarg_link.h</c> declares with
    /// <c>YL_API</c>. When you add a function to the header, add it here, and to the <c>BIND</c> list in
    /// <c>tests/c/harness.c</c>. If you forget it here, the Windows step of <c>native-link.yml</c> still finds
    /// the missing export: it reads the list from the header.
    /// </summary>
    private static readonly string[] Exports =
    [
        "yl_abi_version",
        "yl_connect",
        "yl_create",
        "yl_destroy",
        "yl_disconnect",
        "yl_last_error",
        "yl_local_ticket",
        "yl_peer_id",
        "yl_peer_stats",
        "yl_poll",
        "yl_send",
    ];

    public static async Task<int> RunAsync(
        RepositoryLayout repository,
        BuildOptions options)
    {
        LinkPlugin plugin = LinkPlugins.ForCurrentHost();
        Console.WriteLine($"Building {plugin.Platform} YargLink plugin.");

        string builtBinary = plugin.Platform == NativePlatform.Linux && !IsGlibcFloorHost()
            ? await BuildInContainerAsync(repository, plugin)
            : await BuildOnHostAsync(repository, plugin);
        BuildCommand.RequireFile(builtBinary, "Native build output");

        await CheckBinaryAsync(repository, plugin, builtBinary);
        await RunIntegrationAsync(repository, plugin, builtBinary);

        if (options.OutputDirectory is not null)
        {
            string destination = Path.GetFullPath(options.OutputDirectory, repository.Root);
            BuildCommand.CopyPlugin(repository, plugin, builtBinary, destination);
        }

        if (options.VerifyCommittedPlugin)
        {
            await VerifyCommittedPluginAsync(repository, plugin, builtBinary);
        }
        else if (options.OutputDirectory is null && !options.NoCopy)
        {
            string destination = Path.GetDirectoryName(plugin.PluginBinaryPath(repository))!;
            BuildCommand.CopyPlugin(repository, plugin, builtBinary, destination);
        }

        Console.WriteLine("Native build completed.");
        return 0;
    }

    private static async Task<string> BuildOnHostAsync(
        RepositoryLayout repository,
        LinkPlugin plugin)
    {
        string crate = repository.LinkDirectory;

        // The toolchain pinned by rust-toolchain.toml, then the Rust unit tests on the host target.
        await ProcessRunner.RunAsync("rustup", ["toolchain", "install"], crate);
        await ProcessRunner.RunAsync("cargo", ["test", "--locked"], crate);

        var builtSlices = new List<string>();
        foreach (string target in plugin.Targets)
        {
            await ProcessRunner.RunAsync("rustup", ["target", "add", target], crate);
            await ProcessRunner.RunAsync(
                "cargo",
                ["build", "--release", "--locked", "--target", target, "--target-dir", "target"],
                crate,
                ReleaseEnvironment(plugin, crate, CargoHome()));
            builtSlices.Add(Path.Combine(crate, "target", target, "release", plugin.BinaryName));
        }

        if (plugin.Platform != NativePlatform.MacOS)
        {
            return builtSlices.Single();
        }

        string universalDirectory = Path.Combine(crate, "target", "universal-apple-darwin", "release");
        Directory.CreateDirectory(universalDirectory);
        string universal = Path.Combine(universalDirectory, plugin.BinaryName);
        await ProcessRunner.RunAsync(
            "lipo", ["-create", "-output", universal, .. builtSlices], repository.Root);
        return universal;
    }

    /// <summary>
    /// Builds the Linux plugin in an Ubuntu 20.04 container, so it doesn't need a newer glibc
    /// than players have. CI runs inside that image already and builds on the host instead.
    /// </summary>
    private static async Task<string> BuildInContainerAsync(
        RepositoryLayout repository,
        LinkPlugin plugin)
    {
        const string source = "/src";
        const string cargoHome = "/root/.cargo";
        string targetDirectory = $"target/glibc-{GlibcFloor}";
        string target = plugin.Targets.Single();
        string owner = await CaptureLineAsync("id", ["-u"], repository.Root) + ":" +
            await CaptureLineAsync("id", ["-g"], repository.Root);

        // Created here so docker doesn't create it (and target/) as root.
        Directory.CreateDirectory(Path.Combine(repository.LinkDirectory, targetDirectory));

        List<string> arguments =
        [
            "run", "--rm",
            "-v", $"{repository.LinkDirectory}:{source}",
            // Cache the toolchain and crates between builds.
            "-v", $"yarg-link-cargo:{cargoHome}",
            "-v", "yarg-link-rustup:/root/.rustup",
            "-w", source,
            "-e", $"CARGO_TARGET_DIR={source}/{targetDirectory}",
        ];
        foreach ((string name, string value) in ReleaseEnvironment(plugin, source, cargoHome))
        {
            arguments.AddRange(["-e", $"{name}={value}"]);
        }

        // The cargo tests run in the container too, because the host may not have rustup to
        // honor rust-toolchain.toml.
        string script = $"""
            export DEBIAN_FRONTEND=noninteractive
            apt-get update -qq
            apt-get install -y -qq --no-install-recommends curl ca-certificates gcc libc6-dev >/dev/null
            export PATH={cargoHome}/bin:$PATH
            if ! command -v rustup >/dev/null; then
                curl -sSf https://sh.rustup.rs | sh -s -- -y -q --profile minimal --default-toolchain none --no-modify-path
            fi
            rustup toolchain install
            trap 'chown -R {owner} {source}/{targetDirectory}' EXIT
            cargo test --locked
            cargo build --release --locked --target {target}
            """;
        arguments.AddRange([LinuxImage, "bash", "-euo", "pipefail", "-c", script]);

        await ProcessRunner.RunAsync("docker", arguments, repository.Root);
        return Path.Combine(
            repository.LinkDirectory, targetDirectory, target, "release", plugin.BinaryName);
    }

    private static Dictionary<string, string> ReleaseEnvironment(
        LinkPlugin plugin, string crate, string cargoHome)
    {
        List<string> rustFlags =
        [
            $"--remap-path-prefix={crate}={RemappedSource}",
            $"--remap-path-prefix={cargoHome}={RemappedCargoHome}",
        ];

        var environment = new Dictionary<string, string>
        {
            ["CARGO_INCREMENTAL"] = "0",
        };

        switch (plugin.Platform)
        {
            case NativePlatform.Windows:
                // Static CRT: no VC++ redistributable needed. /Brepro: no link timestamp.
                rustFlags.AddRange(["-Ctarget-feature=+crt-static", "-Clink-arg=/Brepro"]);
                break;
            case NativePlatform.MacOS:
                environment["MACOSX_DEPLOYMENT_TARGET"] = MacDeploymentTarget;
                // Strip in the linker instead of afterwards, so the linker's own ad-hoc
                // signature stays valid. A stripped-then-re-signed dylib risks dyld's SIGKILL
                // (see native-audio.yml).
                environment["CARGO_PROFILE_RELEASE_STRIP"] = "none";
                rustFlags.Add("-Clink-arg=-Wl,-x");
                break;
        }

        // The encoded form survives spaces in paths; it is separated by 0x1f.
        environment["CARGO_ENCODED_RUSTFLAGS"] = string.Join('\x1f', rustFlags);
        return environment;
    }

    private static async Task CheckBinaryAsync(
        RepositoryLayout repository,
        LinkPlugin plugin,
        string binary)
    {
        switch (plugin.Platform)
        {
            case NativePlatform.Linux:
            {
                CommandResult symbols = await CaptureCheckedAsync(
                    "objdump", ["-T", binary], repository.Root);
                Version newest = Regex.Matches(symbols.StandardOutput, @"GLIBC_(\d+(?:\.\d+)+)")
                    .Select(match => Version.Parse(match.Groups[1].Value))
                    .DefaultIfEmpty(new Version(0, 0))
                    .Max()!;
                Console.WriteLine($"Newest glibc symbol: {newest}");
                if (newest > Version.Parse(GlibcFloor))
                {
                    throw new ToolException(
                        $"{plugin.BinaryName} needs glibc {newest}, newer than the {GlibcFloor} floor.");
                }

                RequireExports(plugin, symbols.StandardOutput);
                break;
            }
            case NativePlatform.MacOS:
            {
                await ProcessRunner.RunAsync(
                    "lipo", [binary, "-verify_arch", "x86_64", "arm64"], repository.Root);
                CommandResult symbols = await CaptureCheckedAsync(
                    "nm", ["-gU", binary], repository.Root);
                RequireExports(plugin, symbols.StandardOutput);
                break;
            }
        }
    }

    private static void RequireExports(LinkPlugin plugin, string symbolTable)
    {
        string[] missing = Exports
            .Where(name => !Regex.IsMatch(symbolTable, $@"(?m)\b_?{name}\s*$"))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ToolException(
                $"{plugin.BinaryName} doesn't export {string.Join(", ", missing)}.");
        }
    }

    private static async Task RunIntegrationAsync(
        RepositoryLayout repository,
        LinkPlugin plugin,
        string libraryPath)
    {
        string crate = repository.LinkDirectory;
        string harnessDirectory = Path.Combine(crate, "target", "c-harness");
        Directory.CreateDirectory(harnessDirectory);
        string harnessSource = Path.Combine(crate, "tests", "c", "harness.c");
        string[] cFlags = ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O1"];

        // The C harness loads the library the way the game does. Windows runners have no C
        // compiler on PATH, so there the C# tests below are the load test.
        switch (plugin.Platform)
        {
            case NativePlatform.Linux:
            {
                string harness = Path.Combine(harnessDirectory, "harness");
                await ProcessRunner.RunAsync(
                    "cc", [.. cFlags, "-o", harness, harnessSource, "-ldl"], crate);
                await ProcessRunner.RunAsync(harness, [libraryPath], crate);
                break;
            }
            case NativePlatform.MacOS:
            {
                // One harness per slice. The x86_64 one runs under Rosetta.
                foreach (string arch in new[] { "arm64", "x86_64" })
                {
                    string harness = Path.Combine(harnessDirectory, $"harness-{arch}");
                    await ProcessRunner.RunAsync(
                        "cc", [.. cFlags, "-arch", arch, "-o", harness, harnessSource], crate);
                    await ProcessRunner.RunAsync("arch", [$"-{arch}", harness, libraryPath], crate);
                }

                break;
            }
        }

        // The game's own bindings (NativeLinkTransport) and session layer, on this library.
        await ProcessRunner.RunAsync(
            "dotnet",
            [
                "test", "Tests/YARG.Link.Tests", "--configuration", "Release",
                // One line per test, and a hung test is aborted and named instead of
                // holding the job until the CI timeout.
                "--logger", "console;verbosity=normal",
                "--blame-hang-timeout", "3m", "--blame-hang-dump-type", "none",
            ],
            repository.Root,
            new Dictionary<string, string>
            {
                ["YARG_LINK_LIBRARY"] = Path.GetFullPath(libraryPath),
                ["YARG_LINK_REQUIRE_NATIVE"] = "1",
            });
    }

    private static async Task VerifyCommittedPluginAsync(
        RepositoryLayout repository,
        LinkPlugin plugin,
        string builtBinary)
    {
        string committedBinary = plugin.PluginBinaryPath(repository);
        BuildCommand.RequireFile(committedBinary, "Committed Unity plugin");
        BuildCommand.RequireFile(plugin.PluginMetadataPath(repository), "Committed Unity plugin metadata");

        if (plugin.Platform != NativePlatform.Windows)
        {
            if (!BuildCommand.FilesEqual(builtBinary, committedBinary))
            {
                throw new ToolException(
                    $"Committed {plugin.BinaryName} does not match native build output. Rebuild it " +
                    "with `build --library link` or `package --library link`, and commit it.");
            }

            // Same bytes as the build that just passed the tests.
            Console.WriteLine($"Committed {plugin.BinaryName} matches the build.");
            return;
        }

        await CheckBinaryAsync(repository, plugin, committedBinary);
        await RunIntegrationAsync(repository, plugin, committedBinary);
        Console.WriteLine($"Committed {plugin.BinaryName} passed native integration.");
    }

    private static bool IsGlibcFloorHost()
    {
        const string osRelease = "/etc/os-release";
        if (!File.Exists(osRelease))
        {
            return false;
        }

        Dictionary<string, string> fields = File.ReadAllLines(osRelease)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim('"'));
        return fields.GetValueOrDefault("ID") == "ubuntu" &&
            fields.GetValueOrDefault("VERSION_ID") == "20.04";
    }

    private static string CargoHome()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("CARGO_HOME");
        return Path.GetFullPath(!string.IsNullOrEmpty(fromEnvironment)
            ? fromEnvironment
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo"));
    }

    private static async Task<CommandResult> CaptureCheckedAsync(
        string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        CommandResult result = await ProcessRunner.CaptureAsync(fileName, arguments, workingDirectory);
        if (result.ExitCode != 0)
        {
            throw new ToolException(
                $"{fileName} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result;
    }

    private static async Task<string> CaptureLineAsync(
        string fileName, IEnumerable<string> arguments, string workingDirectory) =>
        (await CaptureCheckedAsync(fileName, arguments, workingDirectory))
            .StandardOutput.Trim();
}
