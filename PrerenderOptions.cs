namespace Prerender.AspNetCore;

public class PrerenderOptions
{
    public string? Token { get; set; }
    public string ServiceUrl { get; set; } = "https://service.prerender.io/";
}
