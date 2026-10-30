using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Windmill;

internal sealed record Release(Version Version, string Url);

/// <summary>Asks GitHub for the latest Windmill release.</summary>
internal static class Updater
{
    const string LatestUrl = "https://api.github.com/repos/Kataruna/Windmill/releases/latest";

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    // After Current: static fields initialise in order, and the client's User-Agent uses it.
    static readonly HttpClient Http = CreateClient();

    /// <summary>The latest release if it is newer than this build; null if not, or if GitHub can't be reached.</summary>
    public static async Task<Release?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestUrl));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var url = root.GetProperty("html_url").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !url.StartsWith("https://github.com/"))
                return null;
            latest = Normalize(latest);
            return latest > Current ? new Release(latest, url) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            return null; // offline, rate-limited or no release yet; try again next time
        }
    }

    /// <summary>1.1 and 1.1.0.0 compare as 1.1.0.</summary>
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Windmill", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
