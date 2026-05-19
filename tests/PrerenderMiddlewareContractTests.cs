using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Prerender.AspNetCore.Tests;

// Contract tests against the shared mock server.
// Spec: https://github.com/prerender/integration-contract
// CI fetches mock-server.mjs into the repo root; locally:
//   curl -fsSL -o mock-server.mjs https://raw.githubusercontent.com/prerender/integration-contract/main/mock-server.mjs

public class MockServerFixture : IAsyncLifetime
{
    private const string DefaultMockPath = "mock-server.mjs";
    private Process? _process;

    public string Url { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var mockPath = Environment.GetEnvironmentVariable("MOCK_SERVER_PATH") ?? DefaultMockPath;
        var resolved = Path.IsPathRooted(mockPath) ? mockPath : Path.Combine(FindRepoRoot(), mockPath);
        if (!File.Exists(resolved))
        {
            throw new InvalidOperationException(
                $"mock-server.mjs not found at {resolved}; fetch from prerender/integration-contract");
        }

        int port;
        using (var listener = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { resolved },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PORT"] = port.ToString();
        _process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start mock server");
        Url = $"http://127.0.0.1:{port}";

        using var client = new HttpClient();
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var r = await client.GetAsync($"{Url}/__health");
                if (r.IsSuccessStatusCode) return;
            }
            catch { /* not ready yet */ }
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"mock server at {Url} did not become ready");
    }

    public Task DisposeAsync()
    {
        try { _process?.Kill(true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Prerender.AspNetCore.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

public class PrerenderMiddlewareContractTests : IClassFixture<MockServerFixture>
{
    private const string BotUserAgent = "Mozilla/5.0 (compatible; Googlebot/2.1)";
    private const string BrowserUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36";
    private const string Token = "test-token-abc123";
    private static readonly Regex UuidV4 = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase);

    private readonly MockServerFixture _mock;
    private readonly HttpClient _bare = new();

    public PrerenderMiddlewareContractTests(MockServerFixture mock)
    {
        _mock = mock;
    }

    private async Task ResetAsync() =>
        await _bare.PostAsync($"{_mock.Url}/__reset", null);

    private async Task<JsonElement> RecordedAsync()
    {
        var r = await _bare.GetAsync($"{_mock.Url}/__requests");
        var body = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement;
    }

    private TestServer CreateServer(string? token = Token)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPrerender();
                services.Configure<PrerenderOptions>(opt =>
                {
                    opt.ServiceUrl = _mock.Url + "/";
                    opt.Token = token;
                });
            })
            .Configure(app =>
            {
                app.UsePrerender();
                app.Run(ctx => ctx.Response.WriteAsync("original"));
            });
        return new TestServer(builder);
    }

    [Fact]
    public async Task BotRequest_EmitsOutgoingRequestWithRequiredHeaders()
    {
        await ResetAsync();
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BotUserAgent);

        await client.GetAsync("/blog/post-1?ref=twitter");

        var recorded = await RecordedAsync();
        Assert.Equal(1, recorded.GetArrayLength());
        var r = recorded[0];
        Assert.Equal("GET", r.GetProperty("method").GetString());
        Assert.EndsWith("/blog/post-1?ref=twitter", r.GetProperty("url").GetString());
        var headers = r.GetProperty("headers");
        Assert.Equal(BotUserAgent, headers.GetProperty("user-agent").GetString());
        Assert.Equal(Token, headers.GetProperty("x-prerender-token").GetString());
        Assert.Equal("AspNetCore", headers.GetProperty("x-prerender-int-type").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+", headers.GetProperty("x-prerender-int-version").GetString()!);
        Assert.Matches(UuidV4, headers.GetProperty("x-prerender-request-id").GetString()!);
    }

    [Fact]
    public async Task BrowserRequest_EmitsNoOutgoingRequest()
    {
        await ResetAsync();
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BrowserUserAgent);

        await client.GetAsync("/");

        var recorded = await RecordedAsync();
        Assert.Equal(0, recorded.GetArrayLength());
    }

    [Fact]
    public async Task StaticAsset_EmitsNoOutgoingRequest()
    {
        await ResetAsync();
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BotUserAgent);

        await client.GetAsync("/styles.css");

        var recorded = await RecordedAsync();
        Assert.Equal(0, recorded.GetArrayLength());
    }

    [Fact]
    public async Task TokenOmitted_WhenUnconfigured()
    {
        await ResetAsync();
        using var server = CreateServer(token: null);
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BotUserAgent);

        await client.GetAsync("/");

        var recorded = await RecordedAsync();
        Assert.Equal(1, recorded.GetArrayLength());
        Assert.False(
            recorded[0].GetProperty("headers").TryGetProperty("x-prerender-token", out _),
            "X-Prerender-Token must not be sent when unconfigured");
    }

    [Fact]
    public async Task RequestId_IsUniquePerOutgoingRequest()
    {
        await ResetAsync();
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BotUserAgent);

        await client.GetAsync("/");
        await client.GetAsync("/");

        var recorded = await RecordedAsync();
        Assert.Equal(2, recorded.GetArrayLength());
        Assert.NotEqual(
            recorded[0].GetProperty("headers").GetProperty("x-prerender-request-id").GetString(),
            recorded[1].GetProperty("headers").GetProperty("x-prerender-request-id").GetString());
    }
}
