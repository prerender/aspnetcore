using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Prerender.AspNetCore;

public static class PrerenderServiceExtensions
{
    public static IServiceCollection AddPrerender(this IServiceCollection services)
    {
        services.AddOptions<PrerenderOptions>().BindConfiguration("Prerender");
        services.AddHttpClient("prerender");
        services.AddTransient<PrerenderMiddleware>();
        return services;
    }

    public static IApplicationBuilder UsePrerender(this IApplicationBuilder app)
        => app.UseMiddleware<PrerenderMiddleware>();
}
