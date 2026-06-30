using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace JellyfinAndroidTranscoder.IntegrationTests;

public sealed class LiveJellyfinPlaybackTests
{
    [Fact]
    public async Task LiveManualJellyfinPlaysRemoteAndroidHlsPastSegmentWindow()
    {
        var baseUrlValue = Environment.GetEnvironmentVariable("JFAT_LIVE_JELLYFIN_URL");
        if (string.IsNullOrWhiteSpace(baseUrlValue))
        {
            return;
        }

        var baseUrl = new Uri(baseUrlValue);
        var username = Environment.GetEnvironmentVariable("JFAT_LIVE_JELLYFIN_USER") ?? "admin";
        var password = Environment.GetEnvironmentVariable("JFAT_LIVE_JELLYFIN_PASSWORD") ?? "password";
        var itemName = Environment.GetEnvironmentVariable("JFAT_LIVE_JELLYFIN_ITEM") ?? "JFAT Large HEVC Browser Test";
        var deviceId = "jfat-live-test-" + Guid.NewGuid().ToString("N");

        using var client = new HttpClient { BaseAddress = baseUrl };
        client.DefaultRequestHeaders.Add("X-Emby-Authorization", $"MediaBrowser Client=\"jfat-live-test\", Device=\"playwright\", DeviceId=\"{deviceId}\", Version=\"1\"");
        var auth = await PostJson<AuthResult>(client, "/Users/AuthenticateByName", new { Username = username, Pw = password });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{auth.AccessToken}\"");

        var items = await GetJson<ItemsResult>(client, $"/Users/{auth.User.Id}/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path");
        var item = items.Items.FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Could not find Jellyfin item '{itemName}'. Available: {string.Join(", ", items.Items.Select(i => i.Name))}");

        var playback = await PostJson<PlaybackInfo>(client, $"/Items/{item.Id}/PlaybackInfo?UserId={auth.User.Id}", new
        {
            DeviceProfile = new
            {
                MaxStreamingBitrate = 3_000_000,
                TranscodingProfiles = new[]
                {
                    new { Container = "ts", Type = "Video", VideoCodec = "h264", AudioCodec = "aac", Protocol = "hls" }
                }
            }
        });
        var transcodingUrl = playback.MediaSources[0].TranscodingUrl
            ?? throw new InvalidOperationException("Jellyfin did not return a transcoding URL.");
        var playlistUrl = new Uri(baseUrl, transcodingUrl + (transcodingUrl.Contains('?') ? "&" : "?") + "api_key=" + auth.AccessToken);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            FirefoxUserPrefs = new Dictionary<string, object>
            {
                ["media.hardware-video-decoding.enabled"] = true,
                ["media.ffmpeg.vaapi.enabled"] = true,
                ["media.mediasource.enabled"] = true
            }
        });
        var page = await browser.NewPageAsync();

        var playlist = await BrowserFetchText(page, playlistUrl);
        Assert.Contains("#EXTM3U", playlist);

        var beforeSeekJobs = await GetAndroidAcceptedJobs();
        var seekedSeconds = await PlayHlsSeekInBrowser(page, playlistUrl, TimeSpan.FromSeconds(90), seekToSeconds: 30, targetSeconds: 34);
        Assert.True(seekedSeconds >= 34, $"Expected live browser playback to continue after seek, got {seekedSeconds:0.0}s.");
        var afterSeekJobs = await GetAndroidAcceptedJobs();
        Assert.True(afterSeekJobs > beforeSeekJobs, $"Expected browser seek to create an Android transcode job, before={beforeSeekJobs}, after={afterSeekJobs}.");

        var playedSeconds = await PlayHlsInBrowser(page, playlistUrl, TimeSpan.FromSeconds(120), targetSeconds: 69);
        Assert.True(playedSeconds >= 69, $"Expected live browser playback to advance through segment 22, got {playedSeconds:0.0}s.");

        var mediaPlaylist = await ResolveMediaPlaylist(page, playlistUrl);
        var segments = SegmentUris(mediaPlaylist.Uri, mediaPlaylist.Playlist).ToArray();
        Assert.True(segments.Length > 22, $"Expected Jellyfin media playlist to expose segment 22 after playback. Playlist:\n{mediaPlaylist.Playlist}");
        var segment22 = await BrowserFetchBytes(page, segments[22]);
        Assert.True(segment22.Length > 0, $"Segment 22 was empty: {segments[22]}");
        Assert.Equal(0x47, segment22[0]);

        await CleanupAndroidJobs();
    }

    private static async Task<T> GetJson<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException($"Missing JSON response for {path}");
    }

    private static async Task<T> PostJson<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException($"Missing JSON response for {path}");
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
                        fragLoadingTimeOut: 10000
                    });
                    hls.on(window.Hls.Events.ERROR, (_event, data) => {
                        if (data && data.fatal) fail(new Error(`${data.type || 'hls'} ${data.details || 'fatal'}`));
                    });
                    hls.on(window.Hls.Events.MANIFEST_PARSED, () => video.play().catch(fail));
                    hls.loadSource(url);
                    hls.attachMedia(video);
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
                        if (data && data.fatal) fail(new Error(`${data.type || 'hls'} ${data.details || 'fatal'}`));
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

    private static async Task<int> GetAndroidAcceptedJobs()
    {
        var statusUrl = Environment.GetEnvironmentVariable("JFAT_ANDROID_STATUS_URL");
        if (string.IsNullOrWhiteSpace(statusUrl))
        {
            return 0;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var token = Environment.GetEnvironmentVariable("JFAT_ANDROID_TOKEN") ?? "1234";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync(statusUrl);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("acceptedJobs").GetInt32();
    }

    private static async Task CleanupAndroidJobs()
    {
        var statusUrl = Environment.GetEnvironmentVariable("JFAT_ANDROID_STATUS_URL");
        if (string.IsNullOrWhiteSpace(statusUrl))
        {
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var token = Environment.GetEnvironmentVariable("JFAT_ANDROID_TOKEN") ?? "1234";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var statusResponse = await client.GetAsync(statusUrl);
        if (!statusResponse.IsSuccessStatusCode)
        {
            return;
        }

        using var document = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        if (!document.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var job in jobs.EnumerateArray())
        {
            if (job.TryGetProperty("id", out var idElement))
            {
                var id = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    var deleteUrl = statusUrl.TrimEnd('/');
                    deleteUrl = deleteUrl.EndsWith("/api/v1/status", StringComparison.Ordinal)
                        ? deleteUrl[..^"/api/v1/status".Length] + "/api/v1/remoteprocesses/" + id
                        : deleteUrl + "/api/v1/remoteprocesses/" + id;
                    using var _ = await client.DeleteAsync(deleteUrl);
                }
            }
        }
    }

    private sealed record AuthResult(string AccessToken, User User);
    private sealed record User(string Id);
    private sealed record ItemsResult(Item[] Items);
    private sealed record Item(string Id, string Name);
    private sealed record PlaybackInfo(MediaSource[] MediaSources);
    private sealed record MediaSource(string? TranscodingUrl);
}
