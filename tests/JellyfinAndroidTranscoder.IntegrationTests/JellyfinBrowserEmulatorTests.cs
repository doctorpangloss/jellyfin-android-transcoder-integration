using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Playwright;

namespace JellyfinAndroidTranscoder.IntegrationTests;

public sealed class JellyfinBrowserEmulatorTests : IAsyncLifetime
{
    private const string AndroidToken = "test-token";
    private const int AndroidForwardPort = 18098;
    private const int AndroidBridgePort = 18099;
    private const long LargeFixtureMinimumBytes = 1L * 1024L * 1024L * 1024L;
    private const long StartupReadCeilingBytes = 128L * 1024L * 1024L;
    private const string ShimPath = "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/jfat-ffmpeg";

    private readonly string _repoRoot = FindRepoRoot();
    private readonly string _workDir;
    private readonly string _configDir;
    private readonly string _mediaPath;
    private readonly IContainer _jellyfin;
    private Process? _emulator;
    private TcpBridge? _androidBridge;

    public JellyfinBrowserEmulatorTests()
    {
        _workDir = Path.Combine(_repoRoot, ".work", "browser-emulator", Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(_workDir, "config");
        _configDir = configDir;
        var cacheDir = Path.Combine(_workDir, "cache");
        var mediaDir = Path.Combine(_workDir, "media");
        var androidTestDir = Path.Combine(configDir, "android-test");
        var pluginDir = Path.Combine(configDir, "plugins", "Android Transcoder_1.0.0");
        var pluginConfigurationsDir = Path.Combine(configDir, "plugins", "configurations");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(mediaDir);
        Directory.CreateDirectory(androidTestDir);
        Directory.CreateDirectory(pluginConfigurationsDir);

        _mediaPath = Path.Combine(mediaDir, "browser-emulator-large-hevc.mp4");
        CreateLargeHevcFixture(_mediaPath);
        WriteFailingFfmpeg(Path.Combine(androidTestDir, "fail-ffmpeg.sh"));
        AssemblePlugin(pluginDir);
        WritePluginConfiguration(
            Path.Combine(pluginConfigurationsDir, "Jellyfin.Plugin.AndroidTranscoder.xml"),
            $"http://host.docker.internal:{AndroidBridgePort}",
            AndroidToken);

        _jellyfin = new ContainerBuilder()
            .WithImage("jellyfin/jellyfin:10.11.6")
            .WithEnvironment("JELLYFIN_FFMPEG", "/usr/lib/jellyfin-ffmpeg/ffmpeg")
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithBindMount(configDir, "/config")
            .WithBindMount(cacheDir, "/cache")
            .WithBindMount(mediaDir, "/media")
            .WithPortBinding(8096, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(8096).ForPath("/health")))
            .Build();
    }

    public async Task InitializeAsync()
    {
        _emulator = await StartEmulatorAndApp();
        _androidBridge = TcpBridge.Start(AndroidBridgePort, IPAddress.Loopback, AndroidForwardPort);
        await _jellyfin.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _jellyfin.DisposeAsync();
        _androidBridge?.Dispose();
        RunAdb(["-s", "emulator-5554", "emu", "kill"], allowFailure: true);
        if (_emulator is { HasExited: false })
        {
            _emulator.Kill(entireProcessTree: true);
        }
        _emulator?.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task BrowserPlaybackTranscodesLargeHevcFileThroughAndroidEmulator()
    {
        var fixtureSize = new FileInfo(_mediaPath).Length;
        Assert.True(
            fixtureSize >= LargeFixtureMinimumBytes,
            $"Expected a large enough HEVC fixture for streaming validation, got {fixtureSize} bytes.");

        var baseUrl = new Uri($"http://127.0.0.1:{_jellyfin.GetMappedPublicPort(8096)}");
        using var client = new HttpClient { BaseAddress = baseUrl };
        await WaitForLogAsync("Core startup complete", TimeSpan.FromSeconds(90));
        var auth = await ConfigureJellyfin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{auth.AccessToken}\"");

        var before = await GetAndroidAcceptedJobs();
        var beforeInputBytes = await GetAndroidInputBytes();
        var item = await WaitForMovie(client, auth.User.Id);
        var playback = await GetPlaybackInfo(client, item.Id, auth.User.Id);
        var transcodingUrl = playback.MediaSources[0].TranscodingUrl
            ?? throw new InvalidOperationException("Jellyfin did not return a transcoding URL.");
        var playlistUrl = new Uri(baseUrl, transcodingUrl + (transcodingUrl.Contains('?') ? "&" : "?") + "api_key=" + auth.AccessToken);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        var startup = Stopwatch.StartNew();
        var playlist = await BrowserFetchText(page, playlistUrl);
        Assert.Contains("#EXTM3U", playlist);

        var segmentUrl = await ResolveFirstSegment(page, playlistUrl, playlist);
        var segment = await BrowserFetchBytes(page, segmentUrl);
        startup.Stop();
        Assert.True(segment.Length > 0, "The browser should receive an HLS segment from Jellyfin.");
        Assert.Equal(0x47, segment[0]);
        Assert.True(
            startup.Elapsed < TimeSpan.FromSeconds(10),
            $"Expected browser-visible HLS media to start within 10s, took {startup.Elapsed}.");
        var startupInputBytes = await GetAndroidInputBytes();
        Assert.True(
            startupInputBytes - beforeInputBytes < StartupReadCeilingBytes,
            $"Expected HLS startup before a full upload; Android consumed {startupInputBytes - beforeInputBytes} bytes before the first segment from a {fixtureSize} byte fixture.");

        var logs = await WaitForFileLogAsync("jfat: routing", TimeSpan.FromSeconds(30));
        Assert.DoesNotContain("jfat: fallback", logs);
        Assert.DoesNotContain("local ffmpeg fallback is forbidden", logs);

        var after = await WaitForAndroidAcceptedJobs(before);
        Assert.True(after > before, $"Expected Android acceptedJobs to increase, before={before}, after={after}");

        var afterInputBytes = await GetAndroidInputBytes();
        Assert.True(
            afterInputBytes - beforeInputBytes < StartupReadCeilingBytes,
            $"Expected the remote process path to avoid eagerly uploading the whole 1 GiB fixture, before={beforeInputBytes}, after={afterInputBytes}, fixture={fixtureSize}.");
    }

    private async Task<AuthResult> ConfigureJellyfin(HttpClient client)
    {
        await PostJson(client, "/Startup/Configuration", new
        {
            ServerName = "jfat-test",
            UICulture = "en-US",
            MetadataCountryCode = "US",
            PreferredMetadataLanguage = "en"
        });
        using (var firstUser = await client.GetAsync("/Startup/User"))
        {
            firstUser.EnsureSuccessStatusCode();
        }
        await PostJson(client, "/Startup/User", new { Name = "admin", Password = "password" });
        await PostJson(client, "/Library/VirtualFolders?name=Movies&collectionType=movies&paths=/media&refreshLibrary=true", new { });
        await PostJson(client, "/Startup/Complete", new { });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "MediaBrowser",
            "Client=\"jfat-test\", Device=\"playwright\", DeviceId=\"playwright\", Version=\"1\"");
        request.Content = JsonContent.Create(new { Username = "admin", Pw = "password" });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResult>();
        return auth ?? throw new InvalidOperationException("Missing auth response.");
    }

    private static async Task PostJson(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{path} failed with {(int)response.StatusCode}: {text}");
        }
    }

    private static async Task<MovieItem> WaitForMovie(HttpClient client, string userId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/Users/{userId}/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = document.RootElement.GetProperty("Items");
            if (items.GetArrayLength() > 0)
            {
                var item = items[0];
                return new MovieItem(
                    item.GetProperty("Id").GetString() ?? throw new InvalidOperationException("Missing item id"));
            }
            await Task.Delay(1000);
        }

        throw new TimeoutException("Timed out waiting for Jellyfin to scan the HEVC movie fixture.");
    }

    private static async Task<PlaybackInfo> GetPlaybackInfo(HttpClient client, string itemId, string userId)
    {
        using var response = await client.PostAsJsonAsync($"/Items/{itemId}/PlaybackInfo", new
        {
            UserId = userId,
            MaxStreamingBitrate = 600000,
            EnableDirectPlay = false,
            EnableDirectStream = false,
            EnableTranscoding = true,
            AllowVideoStreamCopy = false,
            AllowAudioStreamCopy = false,
            DeviceProfile = new
            {
                MaxStreamingBitrate = 600000,
                TranscodingProfiles = new[]
                {
                    new
                    {
                        Type = "Video",
                        Container = "ts",
                        Protocol = "hls",
                        VideoCodec = "h264",
                        AudioCodec = "aac",
                        Context = "Streaming"
                    }
                }
            }
        });
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<PlaybackInfo>();
        return info ?? throw new InvalidOperationException("Missing playback info.");
    }

    private static async Task<string> BrowserFetchText(IPage page, Uri uri) =>
        await page.EvaluateAsync<string>(
            @"async url => {
                const response = await fetch(url);
                if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
                return await response.text();
            }",
            uri.ToString());

    private static async Task<byte[]> BrowserFetchBytes(IPage page, Uri uri)
    {
        var base64 = await page.EvaluateAsync<string>(
            @"async url => {
                const response = await fetch(url);
                if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
                const bytes = new Uint8Array(await response.arrayBuffer());
                let binary = '';
                for (const b of bytes) binary += String.fromCharCode(b);
                return btoa(binary);
            }",
            uri.ToString());
        return Convert.FromBase64String(base64);
    }

    private static async Task<Uri> ResolveFirstSegment(IPage page, Uri playlistUrl, string playlist)
    {
        for (var i = 0; i < 4; i++)
        {
            var line = playlist.Split('\n')
                .Select(i => i.Trim())
                .First(i => i.Length > 0 && !i.StartsWith('#'));
            var uri = new Uri(playlistUrl, line);
            if (!uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return uri;
            }

            playlistUrl = uri;
            playlist = await BrowserFetchText(page, uri);
        }

        throw new InvalidOperationException("Timed out resolving HLS segment from nested playlists.");
    }

    private static async Task<Process> StartEmulatorAndApp()
    {
        EnsureAvd();
        var emulator = StartProcess(
            Path.Combine(AndroidHome(), "emulator", "emulator"),
            ["-avd", "jfat_api35", "-no-window", "-no-audio", "-no-boot-anim", "-gpu", "swiftshader_indirect", "-no-snapshot"]);
        RunAdb(["-s", "emulator-5554", "wait-for-device"]);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            var booted = RunAdb(["-s", "emulator-5554", "shell", "getprop", "sys.boot_completed"], allowFailure: true).Trim();
            if (booted == "1")
            {
                break;
            }
            await Task.Delay(2000);
        }

        BuildAndInstallAndroidApp();
        RunAdb(["-s", "emulator-5554", "shell", "pm", "grant", "com.hiddenswitch.androidtranscoder", "android.permission.POST_NOTIFICATIONS"], allowFailure: true);
        RunAdb(["-s", "emulator-5554", "shell", "am", "force-stop", "com.hiddenswitch.androidtranscoder"], allowFailure: true);
        RunAdb(["-s", "emulator-5554", "shell", "am", "start", "-n", "com.hiddenswitch.androidtranscoder/.MainActivity", "--es", "token", AndroidToken, "--ez", "startService", "true"]);
        RunAdb(["-s", "emulator-5554", "forward", "--remove", $"tcp:{AndroidForwardPort}"], allowFailure: true);
        RunAdb(["-s", "emulator-5554", "forward", $"tcp:{AndroidForwardPort}", "tcp:8098"]);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{AndroidForwardPort}") };
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/api/v1/status");
                if (response.IsSuccessStatusCode)
                {
                    return emulator;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(1000);
        }

        throw new TimeoutException("Timed out waiting for Android transcoder status endpoint.");
    }

    private static async Task<int> GetAndroidAcceptedJobs()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{AndroidForwardPort}") };
        using var response = await client.GetAsync("/api/v1/status");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("acceptedJobs").GetInt32();
    }

    private static async Task<long> GetAndroidInputBytes()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{AndroidForwardPort}") };
        using var response = await client.GetAsync("/api/v1/status");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("inputBytes").GetInt64();
    }

    private static async Task<int> WaitForAndroidAcceptedJobs(int before)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var accepted = await GetAndroidAcceptedJobs();
            if (accepted > before)
            {
                return accepted;
            }

            await Task.Delay(1000);
        }

        return await GetAndroidAcceptedJobs();
    }

    private static void BuildAndInstallAndroidApp()
    {
        var appRoot = Path.Combine(FindRepoRoot(), "third_party", "jellyfin-android-transcoder", "android-transcoder");
        RunProcess(Path.Combine(appRoot, "gradlew"), [":app:bundleVanilla"], appRoot);
        var bundle = Path.Combine(appRoot, "app", "build", "outputs", "bundle", "vanilla", "app-vanilla.aab");
        var apks = Path.Combine(FindRepoRoot(), ".work", "android-emulator", "android-transcoder.apks");
        Directory.CreateDirectory(Path.GetDirectoryName(apks)!);
        RunProcess("java", ["-jar", Bundletool(), "build-apks", "--bundle", bundle, "--output", apks, "--overwrite", "--mode", "universal"], FindRepoRoot());
        RunProcess("java", ["-jar", Bundletool(), "install-apks", "--apks", apks, "--device-id", "emulator-5554"], FindRepoRoot());
    }

    private static void EnsureAvd()
    {
        var list = RunProcess(Path.Combine(AndroidHome(), "cmdline-tools", "latest", "bin", "avdmanager"), ["list", "avd"], FindRepoRoot());
        if (list.Contains("Name: jfat_api35", StringComparison.Ordinal))
        {
            return;
        }

        RunProcess(
            Path.Combine(AndroidHome(), "cmdline-tools", "latest", "bin", "avdmanager"),
            ["create", "avd", "--force", "--name", "jfat_api35", "--package", "system-images;android-35;google_apis;x86_64", "--device", "pixel_6"],
            FindRepoRoot(),
            "no\n");
    }

    private static void CreateLargeHevcFixture(string path)
    {
        RunProcess("ffmpeg",
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=960x540:rate=24",
                "-t", "12",
                "-c:v", "libx265", "-preset", "ultrafast",
                "-b:v", "6M", "-maxrate", "6M", "-bufsize", "12M",
                "-x265-params", "keyint=48:min-keyint=48:scenecut=0",
                "-pix_fmt", "yuv420p",
                "-an",
                "-movflags", "+faststart",
                "-tag:v", "hvc1",
                "-f", "mp4", path, "-y"
            ],
            FindRepoRoot());
        AppendTrailingPadding(path, LargeFixtureMinimumBytes);
        var size = new FileInfo(path).Length;
        if (size < LargeFixtureMinimumBytes)
        {
            throw new InvalidOperationException($"Generated HEVC fixture is too small: {size} bytes.");
        }
    }

    private static void AppendTrailingPadding(string path, long targetSize)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length >= targetSize)
        {
            return;
        }

        stream.SetLength(targetSize);
    }

    private static void WriteFailingFfmpeg(string path)
    {
        File.WriteAllText(path, """
#!/usr/bin/env sh
case " $* " in
  *" -version "*|*" -encoders "*|*" -decoders "*|*" -filters "*|*" -hwaccels "*|*" -protocols "*|*" -pix_fmts "*|*" -layouts "*)
    exec /usr/lib/jellyfin-ffmpeg/ffmpeg "$@"
    ;;
esac

echo 'local ffmpeg fallback is forbidden in this test' >&2
exit 42
""");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private async Task<string> WaitForLogAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string logs = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var containerLogs = await _jellyfin.GetLogsAsync(DateTime.UnixEpoch, DateTime.UtcNow, false, CancellationToken.None);
            logs = containerLogs.Stdout + containerLogs.Stderr;
            if (logs.Contains(marker, StringComparison.Ordinal))
            {
                return logs;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Timed out waiting for Jellyfin log marker `{marker}`.\n{logs}");
    }

    private async Task<string> WaitForFileLogAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var logDir = Path.Combine(_configDir, "log");
        string logs = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            logs = Directory.Exists(logDir)
                ? string.Join('\n', Directory.EnumerateFiles(logDir, "*.log").Select(File.ReadAllText))
                : string.Empty;
            if (logs.Contains(marker, StringComparison.Ordinal))
            {
                return logs;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Timed out waiting for Jellyfin file log marker `{marker}`.\n{logs}");
    }

    private static void AssemblePlugin(string pluginDir)
    {
        var componentRoot = Path.Combine(FindRepoRoot(), "third_party", "jellyfin-android-transcoder", "jellyfin-android-transcoder");
        var shimProject = Path.Combine(componentRoot, "src", "JellyfinAndroidTranscoder.Shim", "JellyfinAndroidTranscoder.Shim.csproj");
        var pluginProject = Path.Combine(componentRoot, "src", "Jellyfin.Plugin.AndroidTranscoder", "Jellyfin.Plugin.AndroidTranscoder.csproj");
        var publishRoot = Path.Combine(FindRepoRoot(), ".work", "publish", Guid.NewGuid().ToString("N"));
        var shimOut = Path.Combine(publishRoot, "shim");
        var pluginOut = Path.Combine(publishRoot, "plugin");
        try
        {
            RunProcess("dotnet", ["publish", shimProject, "-c", "Release", "-o", shimOut], FindRepoRoot());
            RunProcess("dotnet", ["publish", pluginProject, "-c", "Release", "-o", pluginOut], FindRepoRoot());
            Directory.CreateDirectory(pluginDir);
            File.Copy(Path.Combine(pluginOut, "Jellyfin.Plugin.AndroidTranscoder.dll"), Path.Combine(pluginDir, "Jellyfin.Plugin.AndroidTranscoder.dll"), true);
            var shimPayloadDir = Path.Combine(pluginDir, "shim-payload");
            Directory.CreateDirectory(shimPayloadDir);
            File.Copy(Path.Combine(shimOut, "jfat-ffmpeg"), Path.Combine(shimPayloadDir, "jfat-ffmpeg"), true);
        }
        finally
        {
            if (Directory.Exists(publishRoot))
            {
                Directory.Delete(publishRoot, true);
            }
        }
    }

    private static void WritePluginConfiguration(string path, string androidBaseUrl, string token)
    {
        File.WriteAllText(path, $$"""
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <AndroidBaseUrl>{{androidBaseUrl}}</AndroidBaseUrl>
  <Token>{{token}}</Token>
  <RealFfmpegPath>/config/android-test/fail-ffmpeg.sh</RealFfmpegPath>
  <RealFfprobePath>/usr/lib/jellyfin-ffmpeg/ffprobe</RealFfprobePath>
  <ShimPath>{{ShimPath}}</ShimPath>
  <MaxBitrate>600000</MaxBitrate>
</PluginConfiguration>
""");
    }

    private static Process StartProcess(string fileName, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["ANDROID_HOME"] = AndroidHome();
        start.Environment["PATH"] = AndroidHome() + "/platform-tools:" + Environment.GetEnvironmentVariable("PATH");
        return Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {fileName}");
    }

    private static string RunAdb(IReadOnlyList<string> args, bool allowFailure = false) =>
        RunProcess(Path.Combine(AndroidHome(), "platform-tools", "adb"), args, FindRepoRoot(), allowFailure: allowFailure);

    private static string RunProcess(string fileName, IReadOnlyList<string> args, string workDir, string? stdin = null, bool allowFailure = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false
            }
        };
        process.StartInfo.Environment["ANDROID_HOME"] = AndroidHome();
        process.StartInfo.Environment["PATH"] = AndroidHome() + "/platform-tools:" + Environment.GetEnvironmentVariable("PATH");
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.Start();
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed with {process.ExitCode}\n{stdout}\n{stderr}");
        }
        return stdout;
    }

    private static string AndroidHome() => Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Android", "Sdk");

    private static string Bundletool() => "/home/administrator/Documents/tools/bundletool/bundletool-all-1.18.3.jar";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "third_party", "jellyfin-android-transcoder")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find integration repository root.");
    }

    private sealed record AuthResult(string AccessToken, AuthUser User);
    private sealed record AuthUser(string Id);
    private sealed record MovieItem(string Id);
    private sealed record PlaybackInfo(PlaybackMediaSource[] MediaSources);
    private sealed record PlaybackMediaSource(string? TranscodingUrl);

    private sealed class TcpBridge : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly IPAddress _targetAddress;
        private readonly int _targetPort;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        private TcpBridge(int listenPort, IPAddress targetAddress, int targetPort)
        {
            _targetAddress = targetAddress;
            _targetPort = targetPort;
            _listener = new TcpListener(IPAddress.Any, listenPort);
            _listener.Start();
            _acceptLoop = AcceptLoop();
        }

        public static TcpBridge Start(int listenPort, IPAddress targetAddress, int targetPort) =>
            new(listenPort, targetAddress, targetPort);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
            _cts.Dispose();
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var inbound = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => Proxy(inbound, _cts.Token), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private async Task Proxy(TcpClient inbound, CancellationToken cancellationToken)
        {
            using var inboundClient = inbound;
            using var outbound = new TcpClient();
            await outbound.ConnectAsync(_targetAddress, _targetPort, cancellationToken);

            await using var inboundStream = inboundClient.GetStream();
            await using var outboundStream = outbound.GetStream();
            var inboundToOutbound = inboundStream.CopyToAsync(outboundStream, cancellationToken);
            var outboundToInbound = outboundStream.CopyToAsync(inboundStream, cancellationToken);
            await Task.WhenAny(inboundToOutbound, outboundToInbound);
        }
    }
}
