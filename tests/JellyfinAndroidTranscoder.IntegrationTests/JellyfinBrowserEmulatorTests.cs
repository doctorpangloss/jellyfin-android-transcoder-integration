using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Playwright;

namespace JellyfinAndroidTranscoder.IntegrationTests;

public sealed class JellyfinBrowserEmulatorTests : IAsyncLifetime
{
    private const string AndroidToken = "1234";
    private const int AndroidForwardPort = 18098;
    private const int AndroidBridgePort = 18099;
    private const long LargeFixtureMinimumBytes = 1L * 1024L * 1024L * 1024L;
    private const long StartupReadCeilingBytes = 128L * 1024L * 1024L;
    private const string ShimPath = "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/jfat-ffmpeg";
    private const string SourceSecret = "browser-emulator-source-secret";

    private readonly string _repoRoot = FindRepoRoot();
    private readonly string _workDir;
    private readonly string _configDir;
    private readonly string _mediaPath;
    private readonly IContainer _jellyfin;
    private readonly AndroidTarget _androidTarget = AndroidTarget.FromEnvironment();
    private Process? _emulator;
    private TcpBridge? _androidBridge;
    private string? _adbSerial;

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
        var existingFixture = Environment.GetEnvironmentVariable("JFAT_BROWSER_FIXTURE");
        if (!string.IsNullOrWhiteSpace(existingFixture))
        {
            File.Copy(existingFixture, _mediaPath, overwrite: true);
        }
        else
        {
            CreateLargeHevcFixture(_mediaPath);
        }
        WriteFailingFfmpeg(Path.Combine(androidTestDir, "fail-ffmpeg.sh"));
        AssemblePlugin(pluginDir);
        WritePluginConfiguration(
            Path.Combine(pluginConfigurationsDir, "Jellyfin.Plugin.AndroidTranscoder.xml"),
            $"http://host.docker.internal:{AndroidBridgePort}",
            AndroidToken,
            _androidTarget.UseHardwareCodecs);

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
        (_emulator, _adbSerial) = await StartAndroidAndApp(_androidTarget);
        _androidBridge = TcpBridge.Start(AndroidBridgePort, IPAddress.Loopback, AndroidForwardPort);
        await _jellyfin.StartAsync();
        await WaitForLogAsync("Core startup complete", TimeSpan.FromSeconds(90));
        await ConfigureShimSourceUrlForAndroid();
    }

    public async Task DisposeAsync()
    {
        await _jellyfin.DisposeAsync();
        _androidBridge?.Dispose();
        if (_adbSerial is not null)
        {
            RunAdb(["-s", _adbSerial, "forward", "--remove", $"tcp:{AndroidForwardPort}"], allowFailure: true);
        }
        if (_androidTarget.Kind == AndroidTargetKind.Emulator)
        {
            RunAdb(["-s", _adbSerial ?? "emulator-5554", "emu", "kill"], allowFailure: true);
        }
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
        await using var browser = await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        var startup = Stopwatch.StartNew();
        var playlist = await BrowserFetchText(page, playlistUrl);
        Assert.Contains("#EXTM3U", playlist);

        var segmentUrl = await ResolveFirstSegment(page, playlistUrl, playlist);
        byte[] segment;
        try
        {
            segment = await BrowserFetchBytes(page, segmentUrl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Browser failed to fetch first HLS segment {segmentUrl}. Android status: {await GetAndroidStatusText()}\nJellyfin transcode logs:\n{ReadJellyfinFileLogs()}",
                ex);
        }
        startup.Stop();
        Assert.True(segment.Length > 0, "The browser should receive an HLS segment from Jellyfin.");
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

        var seekedSeconds = await PlayHlsSeekInBrowser(page, playlistUrl, TimeSpan.FromSeconds(90), seekToSeconds: 30, targetSeconds: 34);
        Assert.True(
            seekedSeconds >= 34,
            $"Expected browser HLS playback to continue after seeking, got {seekedSeconds:0.0}s. Android status: {await GetAndroidStatusText()}\nJellyfin transcode logs:\n{ReadJellyfinFileLogs()}");
        var remoteArgLines = ReadJellyfinFileLogs().Split('\n')
            .Where(line => line.Contains("jfat: remote ffmpeg args", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(remoteArgLines, line =>
            line.Contains("http://", StringComparison.Ordinal) &&
            line.Contains("/AndroidTranscoder/Source/", StringComparison.Ordinal));
        Assert.DoesNotContain(remoteArgLines, line => line.Contains("https://", StringComparison.Ordinal));

        var playedSeconds = await PlayHlsInBrowser(page, playlistUrl, TimeSpan.FromSeconds(120), targetSeconds: 69);
        Assert.True(
            playedSeconds >= 69,
            $"Expected browser HLS playback to advance through segment 22, got {playedSeconds:0.0}s. Android status: {await GetAndroidStatusText()}\nJellyfin transcode logs:\n{ReadJellyfinFileLogs()}");

        var segment22 = await WaitForBrowserVisibleSegment(page, playlistUrl, 22, TimeSpan.FromSeconds(15));
        Assert.True(
            segment22.Bytes.Length > 0,
            $"Expected Jellyfin browser playback to make segment 22 available. Segment URL: {segment22.Uri}. Android status: {await GetAndroidStatusText()}");

        var afterInputBytes = await GetAndroidInputBytes();
        Assert.True(
            afterInputBytes - beforeInputBytes < StartupReadCeilingBytes,
            $"Expected the remote process path to avoid eagerly uploading the whole 1 GiB fixture, before={beforeInputBytes}, after={afterInputBytes}, fixture={fixtureSize}.");
    }

    [Fact]
    public async Task AndroidEmulatorStartsFmp4OutputBeforeLargeUploadCompletes()
    {
        var beforeInputBytes = await GetAndroidInputBytes();
        var beforeAccepted = await GetAndroidAcceptedJobs();
        var remoteArgs = EncodeRemoteArgs(BuildDirectRemoteFfmpegArgs(_androidTarget.UseHardwareCodecs));

        await using var input = File.OpenRead(_mediaPath);
        var startup = Stopwatch.StartNew();
        RawRemoteProcessExchange exchange;
        try
        {
            exchange = await RawRemoteProcessExchange.Start(
                AndroidForwardPort,
                AndroidToken,
                remoteArgs,
                new RateLimitedStream(input, 8L * 1024L * 1024L),
                TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Android closed the remote process request before response headers. Status: " + await GetAndroidStatusText(), ex);
        }

        await using (exchange)
        {
            byte[] firstMedia;
            try
            {
                firstMedia = await ReadUntilRemoteFile(
                    exchange.ResponseBody,
                    exchange.Boundary,
                    (path, bytes) => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && bytes.Length > 0,
                    TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Expected non-empty fMP4 output before upload completed. Status: " + await GetAndroidStatusText(), ex);
            }
            startup.Stop();
            Assert.True(firstMedia.Length > 0, "Expected fMP4 media bytes from Android before upload completed. Status: " + await GetAndroidStatusText());
            Assert.True(
                startup.Elapsed < TimeSpan.FromSeconds(10),
                $"Expected Android fMP4 output within 10s, took {startup.Elapsed}. Status: {await GetAndroidStatusText()}");
        }

        var afterAccepted = await GetAndroidAcceptedJobs();
        Assert.True(afterAccepted > beforeAccepted, $"Expected Android to accept a remote process, before={beforeAccepted}, after={afterAccepted}");
        var afterInputBytes = await GetAndroidInputBytes();
        Assert.True(
            afterInputBytes - beforeInputBytes < LargeFixtureMinimumBytes,
            $"The test must observe output before the full large upload completes; uploaded={afterInputBytes - beforeInputBytes}, fixture={LargeFixtureMinimumBytes}.");
    }

    [Fact]
    public async Task AndroidMediacodecMpegtsOutputProducesNoFramesAndShimMustAvoidIt()
    {
        var beforeInputBytes = await GetAndroidInputBytes();
        var beforeAccepted = await GetAndroidAcceptedJobs();
        var remoteArgs = EncodeRemoteArgs(BuildDirectRemoteFfmpegArgs(_androidTarget.UseHardwareCodecs, segmentType: "mpegts"));

        await using var input = File.OpenRead(_mediaPath);
        var startup = Stopwatch.StartNew();
        RawRemoteProcessExchange exchange;
        try
        {
            exchange = await RawRemoteProcessExchange.Start(
                AndroidForwardPort,
                AndroidToken,
                remoteArgs,
                new RateLimitedStream(input, 8L * 1024L * 1024L),
                TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Android closed the MPEG-TS remote process request before response headers. Status: " + await GetAndroidStatusText(), ex);
        }

        await using (exchange)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => ReadUntilRemoteFile(
                exchange.ResponseBody,
                exchange.Boundary,
                (path, bytes) => path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) && bytes.Length > 0,
                TimeSpan.FromSeconds(10)));
            startup.Stop();
        }

        var afterAccepted = await GetAndroidAcceptedJobs();
        Assert.True(afterAccepted > beforeAccepted, $"Expected Android to accept a remote process, before={beforeAccepted}, after={afterAccepted}");
        var status = await GetAndroidStatusText();
        Assert.Contains("Output file is empty", status);
        Assert.Contains("\"-hls_segment_type\",\"mpegts\"", Encoding.UTF8.GetString(Base64UrlDecode(remoteArgs)));
        var afterInputBytes = await GetAndroidInputBytes();
        Assert.True(
            afterInputBytes - beforeInputBytes < LargeFixtureMinimumBytes,
            $"The test must observe MPEG-TS output before the full large upload completes; uploaded={afterInputBytes - beforeInputBytes}, fixture={LargeFixtureMinimumBytes}.");
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

    private async Task<(Uri Uri, byte[] Bytes)> WaitForBrowserVisibleSegment(IPage page, Uri playlistUrl, int segmentIndex, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastPlaylist = "";
        Exception? lastFetchFailure = null;
        while (DateTime.UtcNow < deadline)
        {
            var mediaPlaylist = await ResolveMediaPlaylist(page, playlistUrl);
            lastPlaylist = mediaPlaylist.Playlist;
            var segments = SegmentUris(mediaPlaylist.Uri, lastPlaylist).ToArray();
            if (segments.Length > segmentIndex)
            {
                try
                {
                    var bytes = await BrowserFetchBytes(page, segments[segmentIndex]);
                    if (bytes.Length > 0)
                    {
                        return (segments[segmentIndex], bytes);
                    }
                }
                catch (Exception ex)
                {
                    lastFetchFailure = ex;
                }
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException(
            $"Timed out waiting for browser-visible HLS segment {segmentIndex}. Last playlist had {SegmentUris(playlistUrl, lastPlaylist).Count()} media segments. Last fetch failure: {lastFetchFailure?.Message}. Android status: {await GetAndroidStatusText()}\nPlaylist:\n{lastPlaylist}\nJellyfin transcode logs:\n{ReadJellyfinFileLogs()}");
    }

    private static async Task<(Uri Uri, string Playlist)> ResolveMediaPlaylist(IPage page, Uri playlistUrl)
    {
        for (var i = 0; i < 4; i++)
        {
            var playlist = await BrowserFetchText(page, playlistUrl);
            var firstMediaLine = playlist.Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
            if (firstMediaLine is null)
            {
                return (playlistUrl, playlist);
            }
            var uri = new Uri(playlistUrl, firstMediaLine);
            if (!uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return (playlistUrl, playlist);
            }

            playlistUrl = uri;
        }

        throw new InvalidOperationException("Timed out resolving nested HLS media playlist.");
    }

    private static IEnumerable<Uri> SegmentUris(Uri playlistUrl, string playlist)
    {
        foreach (var line in playlist.Split('\n').Select(line => line.Trim()))
        {
            if (line.Length == 0 || line.StartsWith('#') || line.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new Uri(playlistUrl, line);
        }
    }

    private static async Task<double> PlayHlsInBrowser(IPage page, Uri playlistUrl, TimeSpan timeout, int targetSeconds)
    {
        await page.SetContentAsync("""
<!doctype html>
<html>
<body>
<video id="video" muted playsinline autoplay controls></video>
</body>
</html>
""");
        return await page.EvaluateAsync<double>(
            @"async ({ url, timeoutMs, targetSeconds }) => {
                await new Promise((resolve, reject) => {
                    const script = document.createElement('script');
                    script.src = 'https://cdn.jsdelivr.net/npm/hls.js@1.6.5/dist/hls.min.js';
                    script.onload = resolve;
                    script.onerror = () => reject(new Error('failed to load hls.js'));
                    document.head.appendChild(script);
                });

                const video = document.getElementById('video');
                const started = performance.now();
                let lastTime = 0;
                return await new Promise((resolve, reject) => {
                    const timeout = setTimeout(() => {
                        resolve(lastTime);
                    }, timeoutMs);
                    const finish = value => {
                        clearTimeout(timeout);
                        resolve(value);
                    };
                    const fail = error => {
                        clearTimeout(timeout);
                        reject(error);
                    };
                    if (!window.Hls || !window.Hls.isSupported()) {
                        video.src = url;
                        video.play().catch(fail);
                    } else {
                        const hls = new window.Hls({
                            debug: false,
                            lowLatencyMode: false,
                            maxBufferLength: 9,
                            maxMaxBufferLength: 9,
                            backBufferLength: 0,
                            manifestLoadingTimeOut: 10000,
                            fragLoadingTimeOut: 10000
                        });
                        hls.on(window.Hls.Events.ERROR, (_event, data) => {
                            if (data && data.fatal) {
                                fail(new Error(`${data.type || 'hls'} ${data.details || 'fatal'}`));
                            }
                        });
                        hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
                            video.play().catch(fail);
                        });
                        hls.loadSource(url);
                        hls.attachMedia(video);
                    }
                    const poll = setInterval(() => {
                        lastTime = video.currentTime || lastTime;
                        if (lastTime >= targetSeconds) {
                            clearInterval(poll);
                            finish(lastTime);
                        }
                        if (performance.now() - started > timeoutMs) {
                            clearInterval(poll);
                            finish(lastTime);
                        }
                    }, 250);
                });
            }",
            new { url = playlistUrl.ToString(), timeoutMs = (int)timeout.TotalMilliseconds, targetSeconds });
    }

    private static async Task<double> PlayHlsSeekInBrowser(IPage page, Uri playlistUrl, TimeSpan timeout, int seekToSeconds, int targetSeconds)
    {
        await page.SetContentAsync("""
<!doctype html>
<html>
<body>
<video id="video" muted playsinline autoplay controls></video>
</body>
</html>
""");
        return await page.EvaluateAsync<double>(
            @"async ({ url, timeoutMs, seekToSeconds, targetSeconds }) => {
                await new Promise((resolve, reject) => {
                    if (window.Hls) {
                        resolve();
                        return;
                    }
                    const script = document.createElement('script');
                    script.src = 'https://cdn.jsdelivr.net/npm/hls.js@1.6.5/dist/hls.min.js';
                    script.onload = resolve;
                    script.onerror = () => reject(new Error('failed to load hls.js'));
                    document.head.appendChild(script);
                });

                const video = document.getElementById('video');
                const started = performance.now();
                let lastTime = 0;
                return await new Promise((resolve, reject) => {
                    const timeout = setTimeout(() => resolve(lastTime), timeoutMs);
                    const finish = value => {
                        clearTimeout(timeout);
                        resolve(value);
                    };
                    const fail = error => {
                        clearTimeout(timeout);
                        reject(error);
                    };
                    const hls = new window.Hls({
                        debug: false,
                        lowLatencyMode: false,
                        maxBufferLength: 9,
                        maxMaxBufferLength: 9,
                        backBufferLength: 0,
                        manifestLoadingTimeOut: 10000,
                        fragLoadingTimeOut: 15000
                    });
                    let didSeek = false;
                    hls.on(window.Hls.Events.ERROR, (_event, data) => {
                        if (data && data.fatal) {
                            fail(new Error(`${data.type || 'hls'} ${data.details || 'fatal'}`));
                        }
                    });
                    hls.on(window.Hls.Events.MANIFEST_PARSED, () => video.play().catch(fail));
                    hls.loadSource(url);
                    hls.attachMedia(video);
                    const poll = setInterval(() => {
                        lastTime = video.currentTime || lastTime;
                        if (!didSeek && lastTime >= 3) {
                            didSeek = true;
                            video.currentTime = seekToSeconds;
                        }
                        if (didSeek && lastTime >= targetSeconds) {
                            clearInterval(poll);
                            finish(lastTime);
                        }
                        if (performance.now() - started > timeoutMs) {
                            clearInterval(poll);
                            finish(lastTime);
                        }
                    }, 250);
                });
            }",
            new { url = playlistUrl.ToString(), timeoutMs = (int)timeout.TotalMilliseconds, seekToSeconds, targetSeconds });
    }

    private static async Task<(Process? Emulator, string AdbSerial)> StartAndroidAndApp(AndroidTarget target)
    {
        Process? emulator = null;
        var serial = target.Serial;
        if (target.ConnectEndpoint is { Length: > 0 })
        {
            RunAdb(["connect", target.ConnectEndpoint], allowFailure: true);
        }
        if (target.Kind == AndroidTargetKind.Emulator)
        {
            EnsureAvd();
            emulator = StartProcess(
                Path.Combine(AndroidHome(), "emulator", "emulator"),
                ["-avd", "jfat_api35", "-no-window", "-no-audio", "-no-boot-anim", "-gpu", "swiftshader_indirect", "-no-snapshot"]);
            serial = "emulator-5554";
        }
        else if (string.IsNullOrWhiteSpace(serial))
        {
            serial = ResolveRealDeviceSerial();
        }

        RunAdb(["-s", serial, "wait-for-device"]);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            var booted = RunAdb(["-s", serial, "shell", "getprop", "sys.boot_completed"], allowFailure: true).Trim();
            if (booted == "1")
            {
                break;
            }
            await Task.Delay(2000);
        }
        await WaitForAdbReady(serial);

        BuildAndInstallAndroidApp(serial);
        RunAdb(["-s", serial, "shell", "pm", "grant", "com.hiddenswitch.androidtranscoder", "android.permission.POST_NOTIFICATIONS"], allowFailure: true);
        RunAdb(["-s", serial, "shell", "am", "force-stop", "com.hiddenswitch.androidtranscoder"], allowFailure: true);
        RunAdb(["-s", serial, "shell", "am", "start-foreground-service", "-n", "com.hiddenswitch.androidtranscoder/.TranscoderService", "--es", "token", AndroidToken, "--ez", "startOnBoot", "true", "--ez", "keepAwake", "true"]);
        RunAdb(["-s", serial, "forward", "--remove", $"tcp:{AndroidForwardPort}"], allowFailure: true);
        RunAdb(["-s", serial, "forward", $"tcp:{AndroidForwardPort}", "tcp:8098"]);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{AndroidForwardPort}") };
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/api/v1/status");
                if (response.IsSuccessStatusCode)
                {
                    return (emulator, serial);
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(1000);
        }

        throw new TimeoutException("Timed out waiting for Android transcoder status endpoint.");
    }

    private static async Task WaitForAdbReady(string serial)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var state = RunAdb(["-s", serial, "get-state"], allowFailure: true).Trim();
            var packageManager = RunAdb(["-s", serial, "shell", "pm", "path", "android"], allowFailure: true).Trim();
            if (state == "device" && packageManager.StartsWith("package:", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException("Timed out waiting for emulator ADB/package manager readiness.");
    }

    private static string ResolveRealDeviceSerial()
    {
        var devices = RunAdb(["devices"], allowFailure: true)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(line => line.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[1] == "device" && !parts[0].StartsWith("emulator-", StringComparison.Ordinal))
            .Select(parts => parts[0])
            .ToArray();
        return devices.Length switch
        {
            1 => devices[0],
            0 => throw new InvalidOperationException("JFAT_ANDROID_TARGET=real requires one connected real ADB device. Set JFAT_ANDROID_CONNECT=host:port or JFAT_ANDROID_SERIAL=serial, then rerun."),
            _ => throw new InvalidOperationException("Multiple real ADB devices are connected. Set JFAT_ANDROID_SERIAL to choose one: " + string.Join(", ", devices))
        };
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

    private static async Task<string> GetAndroidStatusText()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{AndroidForwardPort}") };
        using var response = await client.GetAsync("/api/v1/status");
        return await response.Content.ReadAsStringAsync();
    }

    private static string EncodeRemoteArgs(IReadOnlyList<string> args)
    {
        var json = JsonSerializer.Serialize(args);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static IReadOnlyList<string> BuildDirectRemoteFfmpegArgs(bool useHardwareCodecs, string segmentType = "fmp4")
    {
        var args = new List<string>();
        AddRemoteFfmpegPreamble(args, useHardwareCodecs);
        args.AddRange(["-i", "{input}", "-t", "12", "-map", "0:v:0"]);
        AddVideoEncodeArgs(args, useHardwareCodecs);
        AddRateControlArgs(args, useHardwareCodecs);
        if (string.Equals(segmentType, "mpegts", StringComparison.OrdinalIgnoreCase))
        {
            AddMpegtsHlsOutputArgs(args);
        }
        else
        {
            AddFmp4HlsOutputArgs(args);
        }
        return args;
    }

    private static void AddRemoteFfmpegPreamble(List<string> args, bool useHardwareCodecs)
    {
        args.AddRange(["-hide_banner", "-loglevel", "info", "-stats_period", "1", "-progress", "pipe:2"]);
        if (!useHardwareCodecs)
        {
            return;
        }
        args.AddRange([
            "-init_hw_device", "mediacodec=mc,create_window=1,surface_processor=1",
            "-hwaccel", "mediacodec",
            "-hwaccel_device", "mc",
            "-hwaccel_output_format", "mediacodec"
        ]);
    }

    private static void AddVideoEncodeArgs(List<string> args, bool useHardwareCodecs)
    {
        if (useHardwareCodecs)
        {
            args.AddRange([
                "-c:v", "h264_mediacodec",
                "-pix_fmt", "mediacodec",
                "-output_width", "960",
                "-output_height", "540",
                "-surface_tonemap", "0"
            ]);
            return;
        }
        args.AddRange([
            "-vf", "scale=960:540:flags=fast_bilinear",
            "-c:v", "h264_mediacodec",
            "-pix_fmt", "yuv420p"
        ]);
    }

    private static void AddRateControlArgs(List<string> args, bool useHardwareCodecs)
    {
        args.AddRange(["-b:v", "600000", "-maxrate", "600000", "-bufsize", "1200000"]);
        if (useHardwareCodecs)
        {
            args.AddRange(["-bitrate_mode", "cbr"]);
        }
    }

    private static void AddFmp4HlsOutputArgs(List<string> args)
    {
        args.AddRange([
            "-g", "24",
            "-an",
            "-sn",
            "-dn",
            "-f", "hls",
            "-hls_time", "1",
            "-hls_flags", "temp_file",
            "-hls_segment_type", "fmp4",
            "-hls_segment_filename", "{outputRoot}/direct%d.mp4",
            "-start_number", "0",
            "-hls_fmp4_init_filename", "direct-1.mp4",
            "-hls_segment_options", "movflags=+frag_discont",
            "-hls_playlist_type", "vod",
            "-hls_list_size", "0",
            "-y", "{outputRoot}/direct.m3u8"
        ]);
    }

    private static void AddMpegtsHlsOutputArgs(List<string> args)
    {
        args.AddRange([
            "-g", "24",
            "-an",
            "-sn",
            "-dn",
            "-f", "hls",
            "-hls_time", "1",
            "-hls_segment_type", "mpegts",
            "-hls_segment_filename", "{outputRoot}/direct%d.ts",
            "-start_number", "0",
            "-hls_list_size", "0",
            "-y", "{outputRoot}/direct.m3u8"
        ]);
    }

    private static string Boundary(string contentType)
    {
        const string marker = "boundary=";
        var index = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            throw new InvalidOperationException("Multipart response is missing boundary: " + contentType);
        }

        var boundary = contentType[(index + marker.Length)..].Trim();
        if (boundary.Length >= 2 && boundary[0] == '"' && boundary[^1] == '"')
        {
            boundary = boundary[1..^1];
        }
        return boundary;
    }

    private static async Task<byte[]> ReadUntilRemoteFile(Stream body, string boundary, Func<string, byte[], bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var part in ReadMultipart(body, boundary, cts.Token))
        {
            if (predicate(part.Path, part.Body))
            {
                return part.Body;
            }
        }

        throw new TimeoutException("Multipart stream ended before matching remote file arrived.");
    }

    private static async IAsyncEnumerable<RemotePart> ReadMultipart(
        Stream stream,
        string boundary,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var boundaryLine = "--" + boundary;
        while (true)
        {
            var line = await ReadAsciiLine(stream, cancellationToken);
            if (line == null)
            {
                yield break;
            }
            if (line == boundaryLine)
            {
                break;
            }
        }

        while (true)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await ReadAsciiLine(stream, cancellationToken)))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            if (!headers.TryGetValue("Content-Length", out var lengthText) || !int.TryParse(lengthText, out var length))
            {
                throw new InvalidOperationException("Multipart part missing Content-Length.");
            }

            var body = new byte[length];
            await ReadExact(stream, body, cancellationToken);
            await ReadAsciiLine(stream, cancellationToken);

            var next = await ReadAsciiLine(stream, cancellationToken);
            var path = headers.TryGetValue("X-Remote-Path", out var remotePath) ? remotePath : "";
            yield return new RemotePart(path, body);

            if (next == boundaryLine + "--" || next == null)
            {
                yield break;
            }
            if (next != boundaryLine)
            {
                throw new InvalidOperationException("Unexpected multipart boundary: " + next);
            }
        }
    }

    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }

    private static async Task<string?> ReadAsciiLine(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }
            var b = buffer[0];
            if (b == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(b);
        }
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

    private static void BuildAndInstallAndroidApp(string serial)
    {
        var appRoot = Path.Combine(FindRepoRoot(), "third_party", "jellyfin-android-transcoder", "android-transcoder");
        RunProcess(Path.Combine(appRoot, "gradlew"), [":app:bundleVanilla"], appRoot);
        var bundle = Path.Combine(appRoot, "app", "build", "outputs", "bundle", "vanilla", "app-vanilla.aab");
        var safeSerial = string.Concat(serial.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        var apks = Path.Combine(FindRepoRoot(), ".work", "android-targets", safeSerial, "android-transcoder.apks");
        Directory.CreateDirectory(Path.GetDirectoryName(apks)!);
        RunProcess("java", ["-jar", Bundletool(), "build-apks", "--bundle", bundle, "--output", apks, "--overwrite", "--mode", "universal"], FindRepoRoot());
        RunProcess("java", ["-jar", Bundletool(), "install-apks", "--apks", apks, "--device-id", serial], FindRepoRoot());
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
                "-t", "75",
                "-c:v", "libx265", "-preset", "ultrafast",
                "-b:v", "6M", "-maxrate", "6M", "-bufsize", "12M",
                "-x265-params", "keyint=48:min-keyint=48:scenecut=0",
                "-pix_fmt", "yuv420p",
                "-an",
                "-movflags", "faststart",
                path, "-y"
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
args=" $* "
case "$args" in *" -f mpegts "*) from_mpegts=1;; *) from_mpegts=0;; esac
case "$args" in *" -i pipe:0 "*) from_pipe=1;; *) from_pipe=0;; esac
case "$args" in *" -codec:v:0 copy "*) video_copy=1;; *) video_copy=0;; esac
if [ "$from_mpegts" = 1 ] && [ "$from_pipe" = 1 ] && [ "$video_copy" = 1 ]; then
  exec /usr/lib/jellyfin-ffmpeg/ffmpeg "$@"
fi

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

    private string ReadJellyfinFileLogs()
    {
        var logDir = Path.Combine(_configDir, "log");
        if (!Directory.Exists(logDir))
        {
            return "";
        }

        return string.Join('\n', Directory.EnumerateFiles(logDir, "*.log")
            .OrderBy(File.GetLastWriteTimeUtc)
            .Select(File.ReadAllText));
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

    private static void WritePluginConfiguration(string path, string androidBaseUrl, string token, bool useHardwareCodecs)
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
  <UseHardwareCodecs>{{useHardwareCodecs.ToString().ToLowerInvariant()}}</UseHardwareCodecs>
  <SourceSecret>{{SourceSecret}}</SourceSecret>
  <AllowedSourceRoots>
    <string>/media</string>
  </AllowedSourceRoots>
</PluginConfiguration>
""");
    }

    private async Task ConfigureShimSourceUrlForAndroid()
    {
        var shimConfigPath = Path.Combine(_configDir, "plugins", "Jellyfin.Plugin.AndroidTranscoder", "shim", "shim-config.json");
        const string containerShimConfigPath = "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/shim-config.json";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!File.Exists(shimConfigPath) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
        }

        if (!File.Exists(shimConfigPath))
        {
            throw new FileNotFoundException("Timed out waiting for plugin shim config.", shimConfigPath);
        }

        var chmod = await _jellyfin.ExecAsync(["chmod", "666", containerShimConfigPath], CancellationToken.None);
        if (chmod.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not make shim config writable.\nSTDOUT:\n{chmod.Stdout}\nSTDERR:\n{chmod.Stderr}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(shimConfigPath));
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when property.Value.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText()
            };
        }

        values["JellyfinBaseUrl"] = AndroidReachableJellyfinBaseUrl();
        values["SourceSecret"] = SourceSecret;
        File.WriteAllText(shimConfigPath, JsonSerializer.Serialize(values));
    }

    private string AndroidReachableJellyfinBaseUrl()
    {
        var overrideUrl = Environment.GetEnvironmentVariable("JFAT_JELLYFIN_BASE_URL_FOR_ANDROID");
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl.TrimEnd('/');
        }

        var port = _jellyfin.GetMappedPublicPort(8096);
        if (_androidTarget.Kind == AndroidTargetKind.Emulator)
        {
            return $"http://10.0.2.2:{port}";
        }

        throw new InvalidOperationException("Set JFAT_JELLYFIN_BASE_URL_FOR_ANDROID to a Jellyfin URL reachable by the real Android device.");
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
    private sealed record RemotePart(string Path, byte[] Body);

    private sealed class RawRemoteProcessExchange : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpResponseMessage _filesResponse;
        private readonly Task<HttpResponseMessage> _uploadTask;

        private RawRemoteProcessExchange(HttpClient client, HttpResponseMessage filesResponse, Stream responseBody, string boundary, Task<HttpResponseMessage> uploadTask)
        {
            _client = client;
            _filesResponse = filesResponse;
            ResponseBody = responseBody;
            Boundary = boundary;
            _uploadTask = uploadTask;
        }

        public Stream ResponseBody { get; }
        public string Boundary { get; }

        public static async Task<RawRemoteProcessExchange> Start(int port, string token, string remoteArgs, Stream requestBody, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };
            using var start = new HttpRequestMessage(HttpMethod.Post, "/api/v1/remoteprocesses");
            start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            start.Headers.TryAddWithoutValidation("X-Remote-Split", "1");
            start.Headers.TryAddWithoutValidation("X-Remote-Executable", "ffmpeg");
            start.Headers.TryAddWithoutValidation("X-Remote-Args", remoteArgs);
            using var startResponse = await client.SendAsync(start, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            startResponse.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync(cts.Token));
            var stdinUrl = document.RootElement.GetProperty("stdinUrl").GetString()
                ?? throw new InvalidOperationException("Missing stdinUrl");
            var filesUrl = document.RootElement.GetProperty("filesUrl").GetString()
                ?? throw new InvalidOperationException("Missing filesUrl");

            var stdin = new HttpRequestMessage(HttpMethod.Put, stdinUrl);
            stdin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            stdin.Content = new StreamContent(requestBody);
            stdin.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var uploadTask = client.SendAsync(stdin, HttpCompletionOption.ResponseHeadersRead);

            var files = new HttpRequestMessage(HttpMethod.Get, filesUrl);
            files.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var filesResponse = await client.SendAsync(files, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            filesResponse.EnsureSuccessStatusCode();
            var body = await filesResponse.Content.ReadAsStreamAsync(cts.Token);
            var boundary = Boundary(filesResponse.Content.Headers.ContentType?.ToString()
                ?? throw new InvalidOperationException("Missing multipart content type"));
            return new RawRemoteProcessExchange(client, filesResponse, body, boundary, uploadTask);
        }

        public async ValueTask DisposeAsync()
        {
            _filesResponse.Dispose();
            try
            {
                using var uploadResponse = await _uploadTask.WaitAsync(TimeSpan.FromSeconds(2));
                uploadResponse.EnsureSuccessStatusCode();
            }
            catch
            {
            }
            _client.Dispose();
        }
    }

    private sealed class RateLimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _bytesPerSecond;
        private long _bytesThisWindow;
        private long _windowStarted = Stopwatch.GetTimestamp();

        public RateLimitedStream(Stream inner, long bytesPerSecond)
        {
            _inner = inner;
            _bytesPerSecond = bytesPerSecond;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var allowed = (int)Math.Min(buffer.Length, 64 * 1024);
            await Throttle(allowed, cancellationToken);
            return await _inner.ReadAsync(buffer[..allowed], cancellationToken);
        }

        private async Task Throttle(int nextReadBytes, CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.GetElapsedTime(_windowStarted);
            if (elapsed >= TimeSpan.FromSeconds(1))
            {
                _windowStarted = Stopwatch.GetTimestamp();
                _bytesThisWindow = 0;
                elapsed = TimeSpan.Zero;
            }

            if (_bytesThisWindow + nextReadBytes > _bytesPerSecond)
            {
                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, cancellationToken);
                _windowStarted = Stopwatch.GetTimestamp();
                _bytesThisWindow = 0;
            }
            _bytesThisWindow += nextReadBytes;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private enum AndroidTargetKind
    {
        Emulator,
        Real
    }

    private sealed record AndroidTarget(AndroidTargetKind Kind, string? Serial, string? ConnectEndpoint)
    {
        public bool UseHardwareCodecs => Kind == AndroidTargetKind.Real;

        public static AndroidTarget FromEnvironment()
        {
            var target = Environment.GetEnvironmentVariable("JFAT_ANDROID_TARGET") ?? "emulator";
            var serial = Environment.GetEnvironmentVariable("JFAT_ANDROID_SERIAL");
            var connect = Environment.GetEnvironmentVariable("JFAT_ANDROID_CONNECT");
            if (target.Equals("real", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                return new AndroidTarget(AndroidTargetKind.Real, serial, connect);
            }
            if (!target.Equals("emulator", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("JFAT_ANDROID_TARGET must be `emulator` or `real`.");
            }
            return new AndroidTarget(AndroidTargetKind.Emulator, serial, connect);
        }
    }

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
