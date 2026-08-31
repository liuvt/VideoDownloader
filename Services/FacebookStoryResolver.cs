using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Options;

namespace VideoDownloader.Blazor.Services;

public sealed class FacebookStoryResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FacebookStoryResolver> _logger;
    private readonly DownloaderOptions _options;
    private readonly IWebHostEnvironment _environment;

    private static readonly string[] PreferredVideoKeys =
    {
        "browser_native_hd_url",
        "playable_url_quality_hd",
        "hd_src_no_ratelimit",
        "hd_src",
        "browser_native_sd_url",
        "playable_url",
        "sd_src_no_ratelimit",
        "sd_src",
        "video_url",
        "videoUrl"
    };

    private static readonly Regex ScriptJsonRegex = new(
        "<script\\b(?=[^>]*\\btype=[\\\"']application/json[\\\"'])[^>]*>(?<json>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex KeyedUrlRegex = new(
        "[\\\"](?<key>browser_native_hd_url|playable_url_quality_hd|hd_src_no_ratelimit|hd_src|browser_native_sd_url|playable_url|sd_src_no_ratelimit|sd_src|video_url|videoUrl)[\\\"]\\s*:\\s*[\\\"](?<url>(?:\\\\.|[^\\\"])*)[\\\"]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MetaTagRegex = new(
        "<meta\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MetaPropertyRegex = new(
        "(?:property|name)\\s*=\\s*[\"'](?<value>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MetaContentRegex = new(
        "content\\s*=\\s*[\"'](?<value>[^\"']*)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MetaCdnUrlRegex = new(
        "(?<url>https?(?:\\\\u003A|:)(?:\\\\/|/){2}[^\\\"'<>\\s]*(?:fbcdn\\.net|cdninstagram\\.com)[^\\\"'<>\\s]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public FacebookStoryResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<FacebookStoryResolver> logger,
        IOptions<DownloaderOptions> options,
        IWebHostEnvironment environment)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
        _environment = environment;
    }

    public bool IsFacebookStoryUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (!IsHostOrSubdomain(host, "facebook.com"))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.Contains("/stories/", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/stories", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/share/s/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FacebookStoryMedia?> TryResolveAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (!IsFacebookStoryUrl(url))
        {
            return null;
        }

        var cookieHeader = LoadFacebookCookieHeader();

        // Facebook Stories are commonly login-gated even when the viewer can
        // open them normally in a browser. Prefer the configured browser session.
        var first = await FetchAndExtractAsync(
            url,
            string.IsNullOrWhiteSpace(cookieHeader) ? null : cookieHeader,
            cancellationToken);

        if (first is not null)
        {
            return first;
        }

        // A stale cookie can occasionally cause a redirect that a logged-out
        // request does not. One anonymous fallback is safe; do not retry-loop.
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            return await FetchAndExtractAsync(url, null, cancellationToken);
        }

        return null;
    }

    private async Task<FacebookStoryMedia?> FetchAndExtractAsync(
        string url,
        string? cookieHeader,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FacebookStoryResolver");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request);

            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null &&
                finalUri.AbsolutePath.Contains("login.php", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Facebook Story redirected to login. Refresh cookies/facebook.txt if the story opens in your browser: {Url}",
                    url);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Facebook Story resolver returned HTTP {StatusCode} for {Url}",
                    (int)response.StatusCode,
                    url);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (html.Length > 15_000_000)
            {
                html = html[..15_000_000];
            }

            var mediaUrl = FindBestVideoUrl(html);
            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                _logger.LogWarning(
                    "Facebook Story page returned HTML but no direct story video URL was found: {Url}",
                    url);
                return null;
            }

            var title = FindMetaContent(html, "og:title") ?? "Facebook Story";
            var thumbnail = NormalizeImageUrl(
                FindMetaContent(html, "og:image") ??
                FindMetaContent(html, "twitter:image"));

            return new FacebookStoryMedia(
                mediaUrl,
                WebUtility.HtmlDecode(title).Trim(),
                thumbnail,
                finalUri?.AbsoluteUri ?? url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not resolve Facebook Story media from {Url}", url);
            return null;
        }
    }

    private static string? FindBestVideoUrl(string html)
    {
        // Facebook embeds the hydrated story model in application/json script
        // blocks. Parse those first so key names remain intact.
        foreach (Match match in ScriptJsonRegex.Matches(html))
        {
            var json = WebUtility.HtmlDecode(match.Groups["json"].Value);
            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryFindPreferredVideoUrl(document.RootElement, out var mediaUrl))
                {
                    return mediaUrl;
                }
            }
            catch (JsonException)
            {
                // Facebook also emits non-JSON/partially escaped script blocks.
            }
        }

        // Some rollouts place the fields in a larger JS payload rather than a
        // clean application/json block. Keep the field names as a strong filter.
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in KeyedUrlRegex.Matches(html))
        {
            var normalized = NormalizeDirectVideoUrl(DecodeJsonString(match.Groups["url"].Value));
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                byKey.TryAdd(match.Groups["key"].Value, normalized);
            }
        }

        foreach (var key in PreferredVideoKeys)
        {
            if (byKey.TryGetValue(key, out var candidate))
            {
                return candidate;
            }
        }

        foreach (var property in new[]
                 {
                     "og:video:secure_url",
                     "og:video:url",
                     "og:video",
                     "twitter:player:stream"
                 })
        {
            var normalized = NormalizeDirectVideoUrl(FindMetaContent(html, property));
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        // Last resort: accept only Meta CDN URLs, never arbitrary links from the page.
        foreach (Match match in MetaCdnUrlRegex.Matches(html))
        {
            var normalized = NormalizeDirectVideoUrl(match.Groups["url"].Value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static bool TryFindPreferredVideoUrl(JsonElement node, out string? mediaUrl)
    {
        mediaUrl = null;

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in PreferredVideoKeys)
            {
                if (node.TryGetProperty(key, out var candidate) &&
                    candidate.ValueKind == JsonValueKind.String)
                {
                    mediaUrl = NormalizeDirectVideoUrl(candidate.GetString());
                    if (!string.IsNullOrWhiteSpace(mediaUrl))
                    {
                        return true;
                    }
                }
            }

            // Some Meta models expose an array of video versions instead of the
            // legacy playable_url fields.
            if (node.TryGetProperty("video_versions", out var versions) &&
                versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versions.EnumerateArray())
                {
                    if (version.ValueKind == JsonValueKind.Object &&
                        version.TryGetProperty("url", out var url) &&
                        url.ValueKind == JsonValueKind.String)
                    {
                        mediaUrl = NormalizeDirectVideoUrl(url.GetString());
                        if (!string.IsNullOrWhiteSpace(mediaUrl))
                        {
                            return true;
                        }
                    }
                }
            }

            foreach (var property in node.EnumerateObject())
            {
                if (TryFindPreferredVideoUrl(property.Value, out mediaUrl))
                {
                    return true;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (TryFindPreferredVideoUrl(item, out mediaUrl))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string? LoadFacebookCookieHeader()
    {
        var configured = _options.FacebookCookiesFile;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var path = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_environment.ContentRootPath, configured);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return BuildNetscapeCookieHeader(path, "www.facebook.com");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read Facebook cookies file {Path}", path);
            return null;
        }
    }

    private static string? BuildNetscapeCookieHeader(string path, string host)
    {
        var pairs = new List<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                line = line["#HttpOnly_".Length..];
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 7)
            {
                continue;
            }

            var domain = fields[0].Trim().TrimStart('.');
            if (domain.Length == 0 ||
                (!host.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
                 !host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (long.TryParse(fields[4], out var expires) && expires > 0 && expires <= now)
            {
                continue;
            }

            var name = fields[5].Trim();
            var value = fields[6].Trim();
            if (name.Length > 0)
            {
                pairs.Add($"{name}={value}");
            }
        }

        return pairs.Count == 0 ? null : string.Join("; ", pairs);
    }

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
    }

    private static string DecodeJsonString(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{value}\"") ?? value;
        }
        catch (JsonException)
        {
            return value
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("\\u0025", "%", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u003A", ":", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u003D", "=", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? NormalizeDirectVideoUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = DecodeJsonString(WebUtility.HtmlDecode(value)).Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.IdnHost.TrimEnd('.');
        var isMetaCdn = IsHostOrSubdomain(host, "fbcdn.net") ||
                        IsHostOrSubdomain(host, "cdninstagram.com");

        return isMetaCdn ? uri.AbsoluteUri : null;
    }

    private static string? NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = DecodeJsonString(WebUtility.HtmlDecode(value)).Trim();
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }

    private static string? FindMetaContent(string html, string property)
    {
        foreach (Match tagMatch in MetaTagRegex.Matches(html))
        {
            var tag = tagMatch.Value;
            var propertyMatch = MetaPropertyRegex.Match(tag);
            if (!propertyMatch.Success ||
                !propertyMatch.Groups["value"].Value.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var contentMatch = MetaContentRegex.Match(tag);
            if (contentMatch.Success)
            {
                return WebUtility.HtmlDecode(contentMatch.Groups["value"].Value);
            }
        }

        return null;
    }

    private static bool IsHostOrSubdomain(string host, string rootDomain) =>
        host.Equals(rootDomain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{rootDomain}", StringComparison.OrdinalIgnoreCase);
}

public sealed record FacebookStoryMedia(
    string MediaUrl,
    string Title,
    string? ThumbnailUrl,
    string Referer);
