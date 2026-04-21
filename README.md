# prerender-aspnetcore

ASP.NET Core middleware for [Prerender.io](https://prerender.io). Intercepts requests from bots and crawlers and serves prerendered HTML, so your JavaScript-rendered app is fully indexable by search engines and social media scrapers.

Compatible with **ASP.NET Core 8+** and **.NET 8+**.

## Installation

```bash
dotnet add package Prerender.AspNetCore
```

## Setup

Register the middleware in `Program.cs`:

```csharp
builder.Services.AddPrerender();

var app = builder.Build();
app.UsePrerender(); // place before routing middleware
```

Add your token to `appsettings.json`:

```json
{
  "Prerender": {
    "Token": "YOUR_PRERENDER_TOKEN"
  }
}
```

The middleware must be placed **before** routing to intercept bot requests early.

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Prerender:Token` | `null` | Your Prerender.io token |
| `Prerender:ServiceUrl` | `https://service.prerender.io/` | Prerender service URL (override for self-hosted Prerender) |

## Self-hosted Prerender

```json
{
  "Prerender": {
    "ServiceUrl": "http://your-prerender-server:3000"
  }
}
```

## How it works

Requests are prerendered when **all** of the following are true:

- The HTTP method is `GET`
- The `User-Agent` matches a known bot/crawler (Googlebot, Bingbot, Twitterbot, GPTBot, ClaudeBot, etc.)  
  — OR the URL contains `_escaped_fragment_`  
  — OR the `X-Bufferbot` header is present
- The URL does not end with a static asset extension (`.js`, `.css`, `.png`, etc.)

Everything else passes through to your normal ASP.NET Core pipeline.

If the Prerender service is unreachable, the middleware falls back gracefully and serves the normal response.

## License

MIT
