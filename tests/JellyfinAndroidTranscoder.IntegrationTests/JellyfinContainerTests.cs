using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace JellyfinAndroidTranscoder.IntegrationTests;

public sealed class JellyfinContainerTests : IAsyncLifetime
{
    private readonly IContainer _jellyfin = new ContainerBuilder()
        .WithImage("jellyfin/jellyfin:10.11.0")
        .WithPortBinding(8096, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request
                .ForPort(8096)
                .ForPath("/System/Info/Public")))
        .Build();

    public Task InitializeAsync() => _jellyfin.StartAsync();

    public Task DisposeAsync() => _jellyfin.DisposeAsync().AsTask();

    [Fact]
    public async Task JellyfinContainerStartsAndExposesPublicInfo()
    {
        using HttpClient client = new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_jellyfin.GetMappedPublicPort(8096)}")
        };

        using HttpResponseMessage response = await client.GetAsync("/System/Info/Public", CancellationToken.None);
        response.EnsureSuccessStatusCode();

        JellyfinPublicInfo? info = await response.Content.ReadFromJsonAsync<JellyfinPublicInfo>(
            cancellationToken: CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(info?.ServerName));
        Assert.False(string.IsNullOrWhiteSpace(info?.Version));
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
        using Stream input = new MemoryStream(new byte[] { 0x47, 0x40, 0x00, 0x10 });
        using StreamContent content = new(input);

        content.Headers.ContentType = new("video/mp2t");
        using HttpRequestMessage request = new(HttpMethod.Post,
            transcoder.BaseUri + "api/v1/transcode?width=1920&height=1080&bitrate=6000000");
        request.Headers.Authorization = new("Bearer", transcoder.Token);
        request.Content = content;

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        byte[] output = await response.Content.ReadAsByteArrayAsync(CancellationToken.None);

        response.EnsureSuccessStatusCode();
        Assert.Equal("video/MP2T", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(MockAndroidTranscoder.ExpectedOutput, output);
        Assert.Equal("/api/v1/transcode", transcoder.LastPath);
        Assert.Equal("1920", transcoder.LastQuery["width"]);
        Assert.Equal("1080", transcoder.LastQuery["height"]);
        Assert.Equal("6000000", transcoder.LastQuery["bitrate"]);
        Assert.Equal(4, transcoder.LastBodyLength);
    }

    private sealed class MockAndroidTranscoder : IAsyncDisposable
    {
        public static readonly byte[] ExpectedOutput = { 0x47, 0x41, 0x00, 0x10 };

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private MockAndroidTranscoder(int port)
        {
            Token = "test-token";
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _loop = Task.Run(HandleAsync);
        }

        public Uri BaseUri { get; }

        public string Token { get; }

        public string? LastPath { get; private set; }

        public Dictionary<string, string> LastQuery { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long LastBodyLength { get; private set; }

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

            LastPath = context.Request.Url?.AbsolutePath;
            LastQuery.Clear();
            foreach (string? key in context.Request.QueryString.AllKeys)
            {
                if (key is not null)
                {
                    LastQuery[key] = context.Request.QueryString[key] ?? "";
                }
            }

            LastBodyLength = await DrainAsync(context.Request.InputStream);
            context.Response.ContentType = "video/MP2T";
            await context.Response.OutputStream.WriteAsync(ExpectedOutput);
            context.Response.Close();
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
    }
}
