using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace JellyfinAndroidTranscoder.IntegrationTests;

public sealed class JellyfinContainerTests : IAsyncLifetime
{
    private const string ShimPath = "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/jfat-ffmpeg";
    private const string ContainerFfmpegPath = "/config/android-test/fake-ffmpeg.sh";
    private const string ContainerProbePath = "/config/android-test/ffprobe-hevc.sh";
    private const string ContainerFallbackLogPath = "/config/android-test/fallback-ffmpeg.log";
    private const string ContainerInputPath = "/config/android-test/movie.mkv";
    private const string ContainerOutputDir = "/cache/transcodes/android-transcoder-test";
    private const string ContainerOutputPath = ContainerOutputDir + "/playlist.m3u8";
    private const string ContainerSegmentPath = ContainerOutputDir + "/segment0.ts";
    private const string SourceSecret = "integration-source-secret";

    private readonly string _workDir;
    private readonly IContainer _jellyfin;
    private readonly MockAndroidTranscoder _android;

    public JellyfinContainerTests()
    {
        var repoRoot = FindRepoRoot();
        _workDir = Path.Combine(repoRoot, ".work", "integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        var configDir = Path.Combine(_workDir, "config");
        var cacheDir = Path.Combine(_workDir, "cache");
        var androidTestDir = Path.Combine(configDir, "android-test");
        var pluginDir = Path.Combine(configDir, "plugins", "Android Transcoder_1.0.0");
        var pluginConfigurationsDir = Path.Combine(configDir, "plugins", "configurations");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(androidTestDir);
        Directory.CreateDirectory(pluginConfigurationsDir);

        _android = MockAndroidTranscoder.StartAsync().GetAwaiter().GetResult();

        AssemblePlugin(pluginDir);
        WriteProbeScript(Path.Combine(androidTestDir, "ffprobe-hevc.sh"));
        WriteFakeFfmpegScript(Path.Combine(androidTestDir, "fake-ffmpeg.sh"));
        File.WriteAllText(Path.Combine(androidTestDir, "fallback-ffmpeg.log"), "");
        File.WriteAllText(Path.Combine(androidTestDir, "movie.mkv"), string.Concat(Enumerable.Repeat("container-input-", 4096)));
        WritePluginConfiguration(
            Path.Combine(pluginConfigurationsDir, "Jellyfin.Plugin.AndroidTranscoder.xml"),
            "http://host.docker.internal:" + _android.Port,
            _android.Token);

        _jellyfin = new ContainerBuilder()
            .WithImage("jellyfin/jellyfin:10.11.6")
            .WithEnvironment("JELLYFIN_FFMPEG", "/usr/lib/jellyfin-ffmpeg/ffmpeg")
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithBindMount(configDir, "/config")
            .WithBindMount(cacheDir, "/cache")
            .WithPortBinding(8096, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request
                    .ForPort(8096)
                    .ForPath("/health")))
            .Build();
    }

    public Task InitializeAsync() => _jellyfin.StartAsync();

    public async Task DisposeAsync()
    {
        await _jellyfin.DisposeAsync();
        await _android.DisposeAsync();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task InstalledPluginConfiguresShimBeforeJellyfinValidatesFfmpeg()
    {
        using HttpClient client = new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_jellyfin.GetMappedPublicPort(8096)}")
        };

        using HttpResponseMessage response = await client.GetAsync("/System/Info/Public", CancellationToken.None);
        response.EnsureSuccessStatusCode();

        JellyfinPublicInfo? info = await response.Content.ReadFromJsonAsync<JellyfinPublicInfo>(
            cancellationToken: CancellationToken.None);

        Assert.Equal("10.11.6", info?.Version);

        var logs = await WaitForLogAsync("Core startup complete", TimeSpan.FromSeconds(60));
        Assert.Contains("Loaded plugin: Android Transcoder", logs);
        Assert.Contains($"Android Transcoder configured Jellyfin FFmpeg path to {ShimPath}", logs);
        Assert.Contains($"FFmpeg: {ShimPath}", logs);
        Assert.DoesNotContain("FFmpeg: /usr/lib/jellyfin-ffmpeg/ffmpeg", logs);
    }

    [Fact]
    public async Task InstalledPluginShimCanExecuteJellyfinHlsContractAgainstAndroidWorker()
    {
        await WaitForLogAsync("Core startup complete", TimeSpan.FromSeconds(60));

        await PairAndroidWorkerAsync();
        await AssertContainerSuccess(["sh", "-c", ": > " + ContainerFallbackLogPath]);
        await AssertContainerSuccess(["mkdir", "-p", ContainerOutputDir]);
        var shimConfig = await AssertContainerSuccess(["cat", "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/shim-config.json"]);
        Assert.Contains("host.docker.internal", shimConfig.Stdout);
        Assert.Contains(ContainerFfmpegPath, shimConfig.Stdout);
        Assert.Contains(ContainerProbePath, shimConfig.Stdout);

        var result = await _jellyfin.ExecAsync(JellyfinHlsArgs(), CancellationToken.None);

        Assert.True(result.ExitCode == 0, $"Shim failed with {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");

        var playlist = await AssertContainerSuccess(["cat", ContainerOutputPath]);
        var segment = await AssertContainerSuccess(["cat", ContainerSegmentPath]);
        var fallbackLog = await AssertContainerSuccess(["cat", ContainerFallbackLogPath]);

        Assert.Contains("#EXTM3U", playlist.Stdout);
        Assert.Contains("segment0.ts", playlist.Stdout);
        Assert.Equal(MockAndroidTranscoder.ExpectedOutputText, segment.Stdout);
        Assert.DoesNotContain("local ffmpeg fallback is forbidden", fallbackLog.Stdout);
        Assert.Equal("ffmpeg", _android.LastExecutable);
        Assert.Contains("/AndroidTranscoder/Source/", _android.LastRemoteArgs);
        var remoteArgs = JsonSerializer.Deserialize<string[]>(_android.LastRemoteArgs) ?? [];
        AssertOption(remoteArgs, "-analyzeduration", "200M");
        AssertOption(remoteArgs, "-probesize", "1G");
        AssertOptionPair(remoteArgs, "-map", "0:0");
        AssertOptionPair(remoteArgs, "-map", "0:1");
        AssertOption(remoteArgs, "-map_metadata", "-1");
        AssertOption(remoteArgs, "-map_chapters", "-1");
        AssertOption(remoteArgs, "-threads", "0");
        AssertOption(remoteArgs, "-profile:v:0", "high");
        AssertOption(remoteArgs, "-level", "51");
        AssertOption(remoteArgs, "-force_key_frames", "expr:gte(t,n_forced*3)");
        Assert.DoesNotContain("-t", remoteArgs);
        Assert.DoesNotContain("-sc_threshold", remoteArgs);
        Assert.DoesNotContain("-copyts", remoteArgs);
        Assert.DoesNotContain("-avoid_negative_ts", remoteArgs);
        Assert.DoesNotContain("-start_at_zero", remoteArgs);
        AssertOption(remoteArgs, "-max_muxing_queue_size", "2048");
        AssertOption(remoteArgs, "-hls_time", "3");
        AssertOption(remoteArgs, "-hls_segment_type", "fmp4");
        AssertOption(remoteArgs, "-hls_segment_filename", "{outputRoot}/segment%d.ts");
        Assert.DoesNotContain("0:a:0?", remoteArgs);
        Assert.DoesNotContain("-preset", remoteArgs);
        Assert.DoesNotContain("-crf", remoteArgs);
        Assert.Equal(0, _android.LastBodyLength);
    }

    [Fact]
    public async Task InstalledPluginHandlesJellyfinPassingWholeFfmpegCommandAsSingleArgument()
    {
        await WaitForLogAsync("Core startup complete", TimeSpan.FromSeconds(60));

        await PairAndroidWorkerAsync();
        await AssertContainerSuccess(["sh", "-c", ": > " + ContainerFallbackLogPath]);
        await AssertContainerSuccess(["mkdir", "-p", ContainerOutputDir]);

        var result = await _jellyfin.ExecAsync([ShimPath, JellyfinHlsSingleArgument()], CancellationToken.None);

        Assert.True(result.ExitCode == 0, $"Shim failed with {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
        var fallbackLog = await AssertContainerSuccess(["cat", ContainerFallbackLogPath]);
        Assert.DoesNotContain("local ffmpeg fallback is forbidden", fallbackLog.Stdout);
        Assert.Equal("ffmpeg", _android.LastExecutable);
        var remoteArgs = JsonSerializer.Deserialize<string[]>(_android.LastRemoteArgs) ?? [];
        AssertOption(remoteArgs, "-analyzeduration", "200M");
        AssertOption(remoteArgs, "-probesize", "1G");
        AssertOptionPair(remoteArgs, "-map", "0:0");
        AssertOptionPair(remoteArgs, "-map", "0:1");
        AssertOption(remoteArgs, "-force_key_frames", "expr:gte(t,n_forced*3)");
        Assert.DoesNotContain("-t", remoteArgs);
        Assert.DoesNotContain("-sc_threshold", remoteArgs);
        AssertOption(remoteArgs, "-hls_segment_filename", "{outputRoot}/segment%d.ts");
        Assert.DoesNotContain("0:a:0?", remoteArgs);
    }

    [Fact]
    public async Task InstalledPluginSeekedPlaybackUsesSignedSourceUrlWithoutDuplicatingInputSeek()
    {
        await WaitForLogAsync("Core startup complete", TimeSpan.FromSeconds(60));

        await PairAndroidWorkerAsync();
        await AssertContainerSuccess(["sh", "-c", ": > " + ContainerFallbackLogPath]);
        await AssertContainerSuccess(["mkdir", "-p", ContainerOutputDir]);

        var result = await _jellyfin.ExecAsync(JellyfinSeekedHlsArgs(), CancellationToken.None);

        Assert.True(result.ExitCode == 0, $"Shim failed with {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
        var fallbackLog = await AssertContainerSuccess(["cat", ContainerFallbackLogPath]);
        Assert.DoesNotContain("local ffmpeg fallback is forbidden", fallbackLog.Stdout);
        Assert.Contains("/AndroidTranscoder/Source/", _android.LastRemoteArgs);
        Assert.Contains("ss=00%3A01%3A30.000", _android.LastRemoteArgs);
        var remoteArgs = JsonSerializer.Deserialize<string[]>(_android.LastRemoteArgs) ?? [];
        AssertOption(remoteArgs, "-analyzeduration", "200M");
        AssertOption(remoteArgs, "-probesize", "1G");
        Assert.DoesNotContain("-ss", remoteArgs);
        Assert.DoesNotContain("-noaccurate_seek", remoteArgs);
        Assert.DoesNotContain("-t", remoteArgs);
        AssertOption(remoteArgs, "-start_number", "30");
        Assert.DoesNotContain("-copyts", remoteArgs);
        Assert.DoesNotContain("-avoid_negative_ts", remoteArgs);
        Assert.DoesNotContain("-start_at_zero", remoteArgs);
        Assert.Equal(0, _android.LastBodyLength);
    }

    [Fact]
    public async Task InstalledPluginCanPairAndroidWorkerThroughJellyfinEndpoint()
    {
        await WaitForLogAsync("Core startup complete", TimeSpan.FromSeconds(60));

        using var pairDocument = await PairAndroidWorkerAsync();
        Assert.True(pairDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("http://host.docker.internal:" + _android.Port, pairDocument.RootElement.GetProperty("androidBaseUrl").GetString());
        Assert.StartsWith("http://127.0.0.1:", pairDocument.RootElement.GetProperty("jellyfinBaseUrl").GetString());

        var shimConfig = await AssertContainerSuccess(["cat", "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/shim-config.json"]);
        Assert.Contains("host.docker.internal:" + _android.Port, shimConfig.Stdout);
        Assert.Contains(_android.Token, shimConfig.Stdout);
        Assert.Contains("127.0.0.1:", shimConfig.Stdout);
    }

    [Fact]
    public async Task SourceEndpointServesOnlySignedAllowedMediaRootsWithRangeSupport()
    {
        await WaitForLogAsync("Core startup complete", TimeSpan.FromSeconds(60));

        using HttpClient client = new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_jellyfin.GetMappedPublicPort(8096)}")
        };

        using HttpRequestMessage allowed = new(HttpMethod.Get, "/AndroidTranscoder/Source/" + SignSourceTicket(ContainerInputPath));
        allowed.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 15);
        using HttpResponseMessage allowedResponse = await client.SendAsync(allowed, CancellationToken.None);
        var body = await allowedResponse.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, allowedResponse.StatusCode);
        Assert.StartsWith("bytes 0-15/", allowedResponse.Content.Headers.ContentRange?.ToString());
        Assert.Equal("container-input-", body);

        using HttpResponseMessage forbidden = await client.GetAsync("/AndroidTranscoder/Source/" + SignSourceTicket("/etc/passwd"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private async Task<ExecResult> AssertContainerSuccess(IList<string> command)
    {
        var result = await _jellyfin.ExecAsync(command, CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"Command `{string.Join(' ', command)}` failed with {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
        return result;
    }

    private static void AssertOption(IReadOnlyList<string> args, string option, string? value)
    {
        var index = Array.IndexOf(args.ToArray(), option);
        Assert.True(index >= 0, $"Expected remote args to contain {option}. Args: {JsonSerializer.Serialize(args)}");
        if (value is not null)
        {
            Assert.True(index + 1 < args.Count, $"Expected {option} to have value {value}. Args: {JsonSerializer.Serialize(args)}");
            Assert.Equal(value, args[index + 1]);
        }
    }

    private static void AssertOptionPair(IReadOnlyList<string> args, string option, string value)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == option && args[i + 1] == value)
            {
                return;
            }
        }

        Assert.Fail($"Expected remote args to contain {option} {value}. Args: {JsonSerializer.Serialize(args)}");
    }

    private static string[] JellyfinHlsArgs() =>
    [
        ShimPath,
        "-analyzeduration", "200M", "-probesize", "1G", "-i", "file:" + ContainerInputPath,
        "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0",
        "-map", "0:0", "-map", "0:1", "-map", "-0:s",
        "-codec:v:0", "libx264", "-preset", "veryfast", "-crf", "23",
        "-maxrate", "8000000", "-bufsize", "12000000", "-profile:v:0", "high",
        "-level", "51", "-force_key_frames:0", "expr:gte(t,n_forced*3)",
        "-sc_threshold:v:0", "0", "-vf",
        @"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\,ih*a)\,1920)/2)*2:trunc(ow/a/2)*2,tonemapx=t=bt709",
        "-codec:a:0", "libfdk_aac", "-ac", "2", "-ab", "256000", "-af", "volume=2",
        "-copyts", "-avoid_negative_ts", "disabled", "-max_muxing_queue_size", "2048",
        "-f", "hls", "-hls_time", "3", "-hls_segment_type", "fmp4",
        "-hls_segment_filename", ContainerOutputDir + "/segment%d.ts",
        "-y", ContainerOutputPath
    ];

    private static string[] JellyfinSeekedHlsArgs() =>
    [
        ShimPath,
        "-analyzeduration", "200M", "-probesize", "1G", "-ss", "00:01:30.000", "-noaccurate_seek", "-i", "file:" + ContainerInputPath,
        "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0",
        "-map", "0:0", "-map", "0:1", "-map", "-0:s",
        "-codec:v:0", "libx264", "-preset", "veryfast", "-crf", "23",
        "-maxrate", "8000000", "-bufsize", "12000000", "-profile:v:0", "high",
        "-level", "51", "-force_key_frames:0", "expr:gte(t,n_forced*3)",
        "-sc_threshold:v:0", "0", "-vf",
        @"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\,ih*a)\,1920)/2)*2:trunc(ow/a/2)*2,tonemapx=t=bt709",
        "-codec:a:0", "libfdk_aac", "-ac", "2", "-ab", "256000", "-af", "volume=2",
        "-copyts", "-avoid_negative_ts", "disabled", "-start_at_zero", "-max_muxing_queue_size", "2048",
        "-f", "hls", "-hls_time", "3", "-hls_segment_type", "fmp4",
        "-start_number", "30", "-hls_segment_filename", ContainerOutputDir + "/segment%d.ts",
        "-hls_playlist_type", "vod", "-hls_list_size", "0",
        "-y", ContainerOutputPath
    ];

    private static string JellyfinHlsSingleArgument() =>
        string.Join(' ', JellyfinHlsArgs().Skip(1).Select(QuoteArgument));

    private static string QuoteArgument(string arg) =>
        arg.Any(char.IsWhiteSpace) || arg.Contains('"', StringComparison.Ordinal) ? "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : arg;

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

    private static void AssemblePlugin(string pluginDir)
    {
        var repoRoot = FindRepoRoot();
        var componentRoot = Path.Combine(repoRoot, "third_party", "jellyfin-android-transcoder", "jellyfin-android-transcoder");
        var shimProject = Path.Combine(componentRoot, "src", "JellyfinAndroidTranscoder.Shim", "JellyfinAndroidTranscoder.Shim.csproj");
        var pluginProject = Path.Combine(componentRoot, "src", "Jellyfin.Plugin.AndroidTranscoder", "Jellyfin.Plugin.AndroidTranscoder.csproj");
        var publishRoot = Path.Combine(repoRoot, ".work", "publish", Guid.NewGuid().ToString("N"));
        var shimOut = Path.Combine(publishRoot, "shim");
        var pluginOut = Path.Combine(publishRoot, "plugin");

        try
        {
            RunDotnet("publish", shimProject, "-c", "Release", "-o", shimOut);
            RunDotnet("publish", pluginProject, "-c", "Release", "-o", pluginOut);

            Directory.CreateDirectory(pluginDir);
            File.Copy(Path.Combine(pluginOut, "Jellyfin.Plugin.AndroidTranscoder.dll"),
                Path.Combine(pluginDir, "Jellyfin.Plugin.AndroidTranscoder.dll"),
                overwrite: true);

            var shimPayloadDir = Path.Combine(pluginDir, "shim-payload");
            Directory.CreateDirectory(shimPayloadDir);
            var shimTarget = Path.Combine(shimPayloadDir, "jfat-ffmpeg");
            File.Copy(Path.Combine(shimOut, "jfat-ffmpeg"), shimTarget, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(shimTarget,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        finally
        {
            if (Directory.Exists(publishRoot))
            {
                Directory.Delete(publishRoot, recursive: true);
            }
        }
    }

    private static void WriteProbeScript(string path)
    {
        File.WriteAllText(path, """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","color_space":"bt2020nc","color_transfer":"smpte2084","color_primaries":"bt2020"}]}
JSON
""".ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void WriteFakeFfmpegScript(string path)
    {
        File.WriteAllText(path, $$"""
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "-version" ]]; then
  cat <<'VERSION'
ffmpeg version 7.1.3-Jellyfin
libavutil      59. 39.100 / 59. 39.100
libavcodec     61. 19.101 / 61. 19.101
libavformat    61.  7.100 / 61.  7.100
libavdevice    61.  3.100 / 61.  3.100
libavfilter    10.  4.100 / 10.  4.100
libswscale      8.  3.100 /  8.  3.100
libswresample   5.  3.100 /  5.  3.100
libpostproc    58.  3.100 / 58.  3.100
VERSION
  exit 0
fi
printf '%s\n' "$*" >> "{{ContainerFallbackLogPath}}"
if [[ "$*" != *"pipe:0"* ]]; then
  exit 99
fi
args=("$@")
out="${args[$((${#args[@]} - 1))]}"
segment_pattern=""
for ((i=0; i<${#args[@]}; i++)); do
  if [[ "${args[$i]}" == "-hls_segment_filename" ]]; then
    segment_pattern="${args[$((i+1))]}"
  fi
done
cat >/dev/null
mkdir -p "$(dirname "$out")"
if [[ -z "$segment_pattern" ]]; then
  segment_pattern="${out%.m3u8}%d.ts"
fi
segment="${segment_pattern/\%d/0}"
mkdir -p "$(dirname "$segment")"
printf '{{MockAndroidTranscoder.ExpectedOutputText}}' > "$segment"
cat > "$out" <<EOF
#EXTM3U
#EXTINF:3.000,
$(basename "$segment")
#EXT-X-ENDLIST
EOF
exit 0
""".ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
  <RealFfmpegPath>{{ContainerFfmpegPath}}</RealFfmpegPath>
  <RealFfprobePath>{{ContainerProbePath}}</RealFfprobePath>
  <ShimPath>{{ShimPath}}</ShimPath>
  <MaxBitrate>6000000</MaxBitrate>
  <SourceSecret>{{SourceSecret}}</SourceSecret>
  <AllowedSourceRoots>
    <string>/config/android-test</string>
  </AllowedSourceRoots>
</PluginConfiguration>
""");
    }

    private async Task<JsonDocument> PairAndroidWorkerAsync()
    {
        using HttpClient client = new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_jellyfin.GetMappedPublicPort(8096)}")
        };

        using var pairingResponse = await client.PostAsync("/AndroidTranscoder/Pairing", null, CancellationToken.None);
        pairingResponse.EnsureSuccessStatusCode();
        using var pairingDocument = JsonDocument.Parse(await pairingResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var code = pairingDocument.RootElement.GetProperty("code").GetString();
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.Equal("/AndroidTranscoder/Pair/" + code, pairingDocument.RootElement.GetProperty("path").GetString());

        using var pageResponse = await client.GetAsync("/AndroidTranscoder/Page", CancellationToken.None);
        pageResponse.EnsureSuccessStatusCode();
        var page = await pageResponse.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("/AndroidTranscoder/Pair/", page);
        Assert.Contains("/AndroidTranscoder/PairingQr.svg?code=", page);

        using var invalidPairResponse = await client.PostAsJsonAsync(
            "/AndroidTranscoder/Pair/000000",
            new
            {
                baseUrl = "http://host.docker.internal:" + _android.Port,
                allBaseUrls = new[]
                {
                    "http://host.docker.internal:" + _android.Port
                },
                token = _android.Token,
                maxBitrate = 6000000
            },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidPairResponse.StatusCode);
        var invalidPairBody = await invalidPairResponse.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("pairing_code_invalid_or_expired", invalidPairBody);

        using var pairResponse = await client.PostAsJsonAsync(
            "/AndroidTranscoder/Pair/" + code,
            new
            {
                baseUrl = "http://host.docker.internal:" + _android.Port,
                allBaseUrls = new[]
                {
                    "http://192.0.2.1:8098",
                    "http://host.docker.internal:" + _android.Port
                },
                token = _android.Token,
                maxBitrate = 6000000
            },
            CancellationToken.None);
        pairResponse.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await pairResponse.Content.ReadAsStringAsync(CancellationToken.None));
    }

    private static string SignSourceTicket(string path)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = path,
            ["exp"] = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds()
        });
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(SourceSecret), payloadBytes);
        return Base64Url(payloadBytes) + "." + Base64Url(sig);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

    private static void RunDotnet(params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {string.Join(' ', args)} failed with exit code {process.ExitCode}\n{stdout}\n{stderr}");
        }
    }

    private sealed record JellyfinPublicInfo(string? ServerName, string? Version);
}

public sealed class AndroidTranscoderContractTests
{
    [Fact]
    public async Task JellyfinStyleRequestCanStreamThroughMockAndroidTranscoder()
    {
        await using MockAndroidTranscoder transcoder = await MockAndroidTranscoder.StartAsync();
        using HttpClient client = new();

        using HttpRequestMessage start = new(HttpMethod.Post, transcoder.BaseUri + "api/v1/remoteprocesses");
        start.Headers.Authorization = new("Bearer", transcoder.Token);
        start.Content = JsonContent.Create(new { executable = "ffmpeg", args = new[] { "-version" } });
        using HttpResponseMessage startResponse = await client.SendAsync(start, CancellationToken.None);
        startResponse.EnsureSuccessStatusCode();
        using var document = System.Text.Json.JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var stdinUrl = document.RootElement.GetProperty("stdinUrl").GetString();
        var filesUrl = document.RootElement.GetProperty("filesUrl").GetString();

        using Stream input = new MemoryStream(new byte[] { 0x47, 0x40, 0x00, 0x10 });
        using StreamContent content = new(input);
        content.Headers.ContentType = new("video/mp2t");
        using HttpRequestMessage stdin = new(HttpMethod.Put, new Uri(transcoder.BaseUri, stdinUrl));
        stdin.Headers.Authorization = new("Bearer", transcoder.Token);
        stdin.Content = content;
        var stdinTask = client.SendAsync(stdin, CancellationToken.None);

        using HttpRequestMessage files = new(HttpMethod.Get, new Uri(transcoder.BaseUri, filesUrl));
        files.Headers.Authorization = new("Bearer", transcoder.Token);
        using HttpResponseMessage response = await client.SendAsync(files, CancellationToken.None);
        string output = await response.Content.ReadAsStringAsync(CancellationToken.None);
        using var stdinResponse = await stdinTask;
        stdinResponse.EnsureSuccessStatusCode();

        response.EnsureSuccessStatusCode();
        Assert.Equal("multipart/mixed", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(MockAndroidTranscoder.ExpectedOutputText, output);
        Assert.Equal("/api/v1/remoteprocesses/job-1/files", transcoder.LastPath);
        Assert.Equal("ffmpeg", transcoder.LastExecutable);
        Assert.Equal(4, transcoder.LastBodyLength);
    }
}

internal sealed class MockAndroidTranscoder : IAsyncDisposable
{
    public const string ExpectedOutputText = "mpegts-from-android";
    public static readonly byte[] ExpectedOutput = System.Text.Encoding.UTF8.GetBytes(ExpectedOutputText);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private MockAndroidTranscoder(int port)
    {
        Port = port;
        Token = "test-token";
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add($"http://*:{port}/");
        _listener.Start();
        _loop = Task.Run(HandleAsync);
    }

    public int Port { get; }

    public Uri BaseUri { get; }

    public string Token { get; }

    public string? LastPath { get; private set; }

    public string LastExecutable { get; private set; } = "";

    public string LastRemoteArgs { get; private set; } = "";

    public Dictionary<string, string> LastQuery { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long LastBodyLength { get; private set; }
    public bool LastUsedSourceUrl { get; private set; }

    public static async Task<MockAndroidTranscoder> StartAsync()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        MockAndroidTranscoder transcoder = new(port);
        using HttpClient client = new();
        using HttpResponseMessage _ = await client.GetAsync(transcoder.BaseUri + "api/v1/status");
        return transcoder;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        _listener.Close();
        _cts.Dispose();
    }

    private async Task HandleAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                _ = Task.Run(() => RespondAsync(context));
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        if (context.Request.Url?.AbsolutePath == "/api/v1/status")
        {
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync("""{"name":"mock","activeJobs":0}"""u8.ToArray());
            context.Response.Close();
            return;
        }

        if (context.Request.Headers["Authorization"] != "Bearer " + Token)
        {
            context.Response.StatusCode = 401;
            context.Response.Close();
            return;
        }

        var path = context.Request.Url?.AbsolutePath ?? "";
        LastPath = path;
        if (!string.IsNullOrEmpty(context.Request.Headers["X-Remote-Executable"]))
        {
            LastExecutable = context.Request.Headers["X-Remote-Executable"] ?? "";
        }
        if (!string.IsNullOrEmpty(context.Request.Headers["X-Remote-Args"]))
        {
            LastRemoteArgs = DecodeArgs(context.Request.Headers["X-Remote-Args"] ?? "");
        }
        LastQuery.Clear();
        foreach (string? key in context.Request.QueryString.AllKeys)
        {
            if (key is not null)
            {
                LastQuery[key] = context.Request.QueryString[key] ?? "";
            }
        }

        if (path == "/api/v1/remoteprocesses" && context.Request.HttpMethod == "POST")
        {
            await CaptureRemoteProcessRequest(context.Request);
            LastUsedSourceUrl = LastRemoteArgs.Contains("/AndroidTranscoder/Source/", StringComparison.Ordinal);
            var body = Encoding.UTF8.GetBytes("""{"id":"job-1","stdinUrl":"/api/v1/remoteprocesses/job-1/stdin","filesUrl":"/api/v1/remoteprocesses/job-1/files","stdoutUrl":"/api/v1/remoteprocesses/job-1/stdout"}""");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
            return;
        }

        if (path == "/api/v1/remoteprocesses/job-1/stdin" && context.Request.HttpMethod == "PUT")
        {
            LastBodyLength = await DrainAsync(context.Request.InputStream);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync("{}"u8.ToArray());
            context.Response.Close();
            return;
        }

        if (path == "/api/v1/remoteprocesses/job-1/stdout" && context.Request.HttpMethod == "GET")
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.SendChunked = true;
            await context.Response.OutputStream.WriteAsync(ExpectedOutput);
            await context.Response.OutputStream.FlushAsync();
            context.Response.Close();
            return;
        }

        if (path != "/api/v1/remoteprocesses/job-1/files" || context.Request.HttpMethod != "GET")
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        var boundary = "mock-boundary";
        context.Response.ContentType = "multipart/mixed; boundary=" + boundary;
        context.Response.SendChunked = true;
        await WritePart(context.Response.OutputStream, boundary, "playlist.m3u8", Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:3.000,\nsegment0.ts\n#EXT-X-ENDLIST\n"));
        await WritePart(context.Response.OutputStream, boundary, "segment0.ts", ExpectedOutput);
        await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: application/json\r\nX-Remote-Event: exit\r\nContent-Length: 14\r\n\r\n{{\"exitCode\":0}}\r\n--{boundary}--\r\n"));
        await context.Response.OutputStream.FlushAsync();
        context.Response.Close();
    }

    private async Task CaptureRemoteProcessRequest(HttpListenerRequest request)
    {
        if (request.HasEntityBody)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("executable", out var executable))
                {
                    LastExecutable = executable.GetString() ?? "";
                }
                if (document.RootElement.TryGetProperty("args", out var args))
                {
                    LastRemoteArgs = args.GetRawText();
                }
                return;
            }
        }

        if (!string.IsNullOrEmpty(request.Headers["X-Remote-Executable"]))
        {
            LastExecutable = request.Headers["X-Remote-Executable"] ?? "";
        }
        if (!string.IsNullOrEmpty(request.Headers["X-Remote-Args"]))
        {
            LastRemoteArgs = DecodeArgs(request.Headers["X-Remote-Args"] ?? "");
        }
    }

    private static async Task WritePart(Stream output, string boundary, string path, byte[] body)
    {
        await output.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: application/octet-stream\r\nX-Remote-Event: upsert\r\nX-Remote-Path: {path}\r\nContent-Length: {body.Length}\r\n\r\n"));
        await output.WriteAsync(body);
        await output.WriteAsync("\r\n"u8.ToArray());
        await output.FlushAsync();
    }

    private static async Task<long> DrainAsync(Stream stream)
    {
        byte[] buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }
        return total;
    }

    private static string DecodeArgs(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        value = value.Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
