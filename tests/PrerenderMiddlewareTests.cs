using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Xunit;

namespace Prerender.AspNetCore.Tests;

public class PrerenderMiddlewareTests
{
    private const string BotUserAgent = "Mozilla/5.0 (compatible; Googlebot/2.1)";
    private const string BrowserUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36";
    private const string PrerenderedHtml = "<html><body>prerendered</body></html>";

    private static TestServer CreateServer(
        HttpResponseMessage? fakeResponse = null,
        Action<PrerenderOptions>? configureOptions = null)
    {
        var response = fakeResponse ?? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PrerenderedHtml)
        };

        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPrerender();
                services.AddHttpClient("prerender")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(response));
                if (configureOptions is not null)
                    services.Configure(configureOptions);
            })
            .Configure(app =>
            {
                app.UsePrerender();
                app.Run(ctx => ctx.Response.WriteAsync("normal response"));
            });

        return new TestServer(builder);
    }

    [Fact]
    public async Task BrowserRequest_PassesThrough()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BrowserUserAgent);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("normal response", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BotRequest_ReceivesPrerenderedResponse()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BotUserAgent);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(PrerenderedHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BotRequest_StaticAsset_PassesThrough()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BotUserAgent);

        var response = await client.GetAsync("/styles.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("normal response", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EscapedFragment_TriggersPrerender()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BrowserUserAgent);

        var response = await client.GetAsync("/?_escaped_fragment_=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(PrerenderedHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task XBufferbot_TriggersPrerender()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BrowserUserAgent);
        client.DefaultRequestHeaders.Add("X-Bufferbot", "true");

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(PrerenderedHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostRequest_BotUa_PassesThrough()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BotUserAgent);

        var response = await client.PostAsync("/", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("normal response", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NetworkError_FallsBackToNormalResponse()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPrerender();
                services.AddHttpClient("prerender")
                    .ConfigurePrimaryHttpMessageHandler(() => new FailingHttpMessageHandler());
            })
            .Configure(app =>
            {
                app.UsePrerender();
                app.Run(ctx => ctx.Response.WriteAsync("normal response"));
            });

        using var server = new TestServer(builder);
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", BotUserAgent);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("normal response", await response.Content.ReadAsStringAsync());
    }
}

internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public FakeHttpMessageHandler(HttpResponseMessage response) => _response = response;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_response);
}

internal class FailingHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("simulated network failure");
}
