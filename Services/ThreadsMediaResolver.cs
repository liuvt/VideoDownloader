using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Options;

namespace VideoDownloader.Blazor.Services;

public sealed partial class ThreadsMediaResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ThreadsMediaResolver> _logger;
    private readonly DownloaderOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ConcurrentDictionary<string, ThreadsCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private readonly object _cooldownLock = new();
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public ThreadsMediaResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<ThreadsMediaResolver> logger,
        IOptions<DownloaderOptions> options,
        IWebHostEnvironment environment)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
        _environment = environment;
    }

    public bool IsThreadsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        return IsHostOrSubdomain(host, "threads.com") ||
               IsHostOrSubdomain(host, "threads.net");
    }

    public async Task<ThreadsMedia?> TryResolveAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (!TryParseThreadsPost(url, out var username, out var postId))
        {
            _logger.LogWarning("Threads URL did not contain a supported post path: {Url}", url);
            return null;
        }

        var canonicalUrl = $"https://www.threads.com/@{username}/post/{postId}/";
        if (TryGetCached(canonicalUrl, out var cached))
        {
            return cached;
        }

        await _resolveGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(canonicalUrl, out cached))
            {
                return cached;
            }

            if (IsCooldownActive(out var remaining))
            {
                _logger.LogWarning(
                    "Threads resolver is in cooldown for another {Seconds}s after a 403/429 response.",
                    Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)));
                return null;
            }

            var cookieHeader = LoadThreadsCookieHeader();
            var usedCookie = !string.IsNullOrWhiteSpace(cookieHeader);

            // A configured browser session is preferred. This avoids making an
            // unnecessary logged-out request before every authenticated request.
            var first = await FetchAndExtractAsync(
                canonicalUrl,
                postId,
                usedCookie ? cookieHeader : null,
                cancellationToken);

            if (first.Media is not null)
            {
                Cache(canonicalUrl, first.Media);
                return first.Media;
            }

            ResolveAttempt? second = null;

            if (usedCookie && first.StatusCode != 429)
            {
                // A stale cookie can be worse than no cookie for public posts.
                second = await FetchAndExtractAsync(
                    canonicalUrl,
                    postId,
                    cookieHeader: null,
                    cancellationToken);
            }
            if (second?.Media is not null)
            {
                Cache(canonicalUrl, second.Media);
                return second.Media;
            }

            var finalAttempt = second ?? first;
            if (finalAttempt.StatusCode is 403 or 429)
            {
                StartCooldown();
            }

            return null;
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    private async Task<ResolveAttempt> FetchAndExtractAsync(
        string canonicalUrl,
        string postId,
        string? cookieHeader,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ThreadsResolver");
            using var request = new HttpRequestMessage(HttpMethod.Get, canonicalUrl);
            AddBrowserHeaders(request);

            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var statusCode = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                _logger.LogWarning("Threads post is unavailable: {Url}", canonicalUrl);
                return new ResolveAttempt(null, statusCode);
            }

            if (statusCode is 403 or 429)
            {
                _logger.LogWarning(
                    "Threads rate-limited or blocked resolver request ({StatusCode}) for {Url}",
                    statusCode,
                    canonicalUrl);
                return new ResolveAttempt(null, statusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Threads resolver returned HTTP {StatusCode} for {Url}",
                    statusCode,
                    canonicalUrl);
                return new ResolveAttempt(null, statusCode);
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (html.Length > 12_000_000)
            {
                html = html[..12_000_000];
            }

            var post = FindPostData(html, postId);
            string? mediaUrl = null;
            string? title = null;
            string? thumbnail = null;

            if (post is not null)
            {
                mediaUrl = NormalizeDirectVideoUrl(ExtractVideoUrl(post.Value));
                title = ExtractCaption(post.Value);
                thumbnail = NormalizeImageUrl(ExtractThumbnailUrl(post.Value));
            }

            mediaUrl ??= FindAnyDirectVideoUrl(html);

            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                _logger.LogWarning(
                    "Threads post {PostId} returned HTML but no direct Meta CDN video URL was found.",
                    postId);
                return new ResolveAttempt(null, statusCode);
            }

            title ??= FindMetaContent(html, "og:title") ?? $"Threads {postId}";
            thumbnail ??= NormalizeImageUrl(
                FindMetaContent(html, "og:image")
                ?? FindMetaContent(html, "twitter:image"));

            return new ResolveAttempt(
                new ThreadsMedia(mediaUrl, title, thumbnail, canonicalUrl),
                statusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not resolve Threads media from {Url}", canonicalUrl);
            return new ResolveAttempt(null, null);
        }
    }

    private string? LoadThreadsCookieHeader()
    {
        var configured = _options.ThreadsCookiesFile;
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
            return BuildNetscapeCookieHeader(path, "www.threads.com");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read Threads cookies file {Path}", path);
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

    private bool TryGetCached(string canonicalUrl, out ThreadsMedia media)
    {
        media = default!;
        if (_options.ThreadsResolveCacheMinutes <= 0 || !_cache.TryGetValue(canonicalUrl, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _cache.TryRemove(canonicalUrl, out _);
            return false;
        }

        media = entry.Media;
        return true;
    }

    private void Cache(string canonicalUrl, ThreadsMedia media)
    {
        if (_options.ThreadsResolveCacheMinutes <= 0)
        {
            return;
        }

        var minutes = Math.Clamp(_options.ThreadsResolveCacheMinutes, 1, 120);
        _cache[canonicalUrl] = new ThreadsCacheEntry(
            media,
            DateTimeOffset.UtcNow.AddMinutes(minutes));
    }

    private void StartCooldown()
    {
        var seconds = Math.Clamp(_options.ThreadsRateLimitCooldownSeconds, 0, 3600);
        if (seconds <= 0)
        {
            return;
        }

        lock (_cooldownLock)
        {
            _cooldownUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
    }

    private bool IsCooldownActive(out TimeSpan remaining)
    {
        lock (_cooldownLock)
        {
            remaining = _cooldownUntil - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero;
        }
    }

    private static bool TryParseThreadsPost(string url, out string username, out string postId)
    {
        username = string.Empty;
        postId = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (!IsHostOrSubdomain(host, "threads.com") &&
            !IsHostOrSubdomain(host, "threads.net"))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // /@user/post/SHORTCODE[/media]
        if (segments.Length < 3 ||
            !segments[0].StartsWith('@') ||
            !segments[1].Equals("post", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        username = segments[0][1..];
        postId = segments[2];

        return username.Length is > 0 and <= 64 &&
               postId.Length is > 0 and <= 32 &&
               UsernameRegex().IsMatch(username) &&
               PostIdRegex().IsMatch(postId);
    }

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "max-age=0");
        request.Headers.TryAddWithoutValidation("DNT", "1");
        request.Headers.TryAddWithoutValidation("Priority", "u=0, i");
        request.Headers.TryAddWithoutValidation("Sec-CH-UA", "\"Chromium\";v=\"151\", \"Google Chrome\";v=\"151\", \"Not_A Brand\";v=\"99\"");
        request.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile", "?0");
        request.Headers.TryAddWithoutValidation("Sec-CH-UA-Platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
    }

    private static JsonElement? FindPostData(string html, string postId)
    {
        foreach (Match match in DataSjsRegex().Matches(html))
        {
            var json = WebUtility.HtmlDecode(match.Groups["json"].Value);
            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryFindPost(document.RootElement, postId, out var post))
                {
                    // Clone before JsonDocument is disposed.
                    return post.Clone();
                }
            }
            catch (JsonException)
            {
                // Threads can include unrelated/non-JSON script blocks; skip them.
            }
        }

        return null;
    }

    private static bool TryFindPost(JsonElement node, string postId, out JsonElement post)
    {
        post = default;

        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("thread_items", out var threadItems) &&
                threadItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in threadItems.EnumerateArray())
                {
                    if (!item.TryGetProperty("post", out var candidate) ||
                        candidate.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (candidate.TryGetProperty("code", out var code) &&
                        code.ValueKind == JsonValueKind.String &&
                        string.Equals(code.GetString(), postId, StringComparison.Ordinal))
                    {
                        post = candidate;
                        return true;
                    }
                }
            }

            foreach (var property in node.EnumerateObject())
            {
                if (TryFindPost(property.Value, postId, out post))
                {
                    return true;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (TryFindPost(item, postId, out post))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ExtractVideoUrl(JsonElement post)
    {
        var direct = PickVideoVersion(post);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        // Carousel: return the first video item. The existing downloader model handles
        // one media output per job, so this preserves current behavior safely.
        if (post.TryGetProperty("carousel_media", out var carousel) &&
            carousel.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in carousel.EnumerateArray())
            {
                var video = PickVideoVersion(item);
                if (!string.IsNullOrWhiteSpace(video))
                {
                    return video;
                }
            }
        }

        // Reshares/quoted posts wrap the original media inside text_post_app_info.
        if (post.TryGetProperty("text_post_app_info", out var textPostInfo) &&
            textPostInfo.ValueKind == JsonValueKind.Object)
        {
            if (textPostInfo.TryGetProperty("share_info", out var shareInfo) &&
                shareInfo.ValueKind == JsonValueKind.Object &&
                shareInfo.TryGetProperty("quoted_attachment_post", out var quoted) &&
                quoted.ValueKind == JsonValueKind.Object)
            {
                var nested = ExtractVideoUrl(quoted);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }

            if (textPostInfo.TryGetProperty("linked_inline_media", out var linked) &&
                linked.ValueKind == JsonValueKind.Object)
            {
                var nested = ExtractVideoUrl(linked);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? PickVideoVersion(JsonElement item)
    {
        if (!item.TryGetProperty("video_versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var version in versions.EnumerateArray())
        {
            if (version.ValueKind == JsonValueKind.Object &&
                version.TryGetProperty("url", out var url) &&
                url.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(url.GetString()))
            {
                return url.GetString();
            }
        }

        return null;
    }

    private static string? ExtractThumbnailUrl(JsonElement post)
    {
        if (!post.TryGetProperty("image_versions2", out var imageVersions) ||
            imageVersions.ValueKind != JsonValueKind.Object ||
            !imageVersions.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.Object &&
                candidate.TryGetProperty("url", out var url) &&
                url.ValueKind == JsonValueKind.String)
            {
                return url.GetString();
            }
        }

        return null;
    }

    private static string? ExtractCaption(JsonElement post)
    {
        // Caption location can vary; recursively find the first useful "text" string.
        if (TryFindString(post, "text", out var text) && !string.IsNullOrWhiteSpace(text))
        {
            text = WebUtility.HtmlDecode(text).Trim();
            return text.Length <= 120 ? text : text[..120];
        }

        return null;
    }

    private static bool TryFindString(JsonElement node, string propertyName, out string? value)
    {
        value = null;
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty(propertyName, out var candidate) &&
                candidate.ValueKind == JsonValueKind.String)
            {
                value = candidate.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }

            foreach (var property in node.EnumerateObject())
            {
                if (TryFindString(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (TryFindString(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? FindAnyDirectVideoUrl(string html)
    {
        foreach (Match match in DataSjsRegex().Matches(html))
        {
            var json = WebUtility.HtmlDecode(match.Groups["json"].Value);
            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryFindAnyDirectVideoUrl(document.RootElement, out var mediaUrl))
                {
                    return mediaUrl;
                }
            }
            catch (JsonException)
            {
                // Ignore unrelated or partially escaped JSON blocks.
            }
        }

        // Metadata is only accepted if it resolves to a real Meta CDN URL.
        // In particular, og:video may be a Threads /media HTML endpoint and is
        // intentionally rejected by NormalizeDirectVideoUrl.
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

        // Last-resort scan for escaped/unescaped Meta CDN URLs. This covers
        // rollout variants where the application/json marker changes.
        foreach (Match match in MetaCdnUrlRegex().Matches(html))
        {
            var normalized = NormalizeDirectVideoUrl(match.Groups["url"].Value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static bool TryFindAnyDirectVideoUrl(JsonElement node, out string? mediaUrl)
    {
        mediaUrl = null;

        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("video_versions", out var versions) &&
                versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versions.EnumerateArray())
                {
                    if (version.ValueKind != JsonValueKind.Object ||
                        !version.TryGetProperty("url", out var url) ||
                        url.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    mediaUrl = NormalizeDirectVideoUrl(url.GetString());
                    if (!string.IsNullOrWhiteSpace(mediaUrl))
                    {
                        return true;
                    }
                }
            }

            foreach (var name in new[]
                     {
                         "playable_url_quality_hd",
                         "playable_url",
                         "video_url",
                         "videoUrl"
                     })
            {
                if (node.TryGetProperty(name, out var candidate) &&
                    candidate.ValueKind == JsonValueKind.String)
                {
                    mediaUrl = NormalizeDirectVideoUrl(candidate.GetString());
                    if (!string.IsNullOrWhiteSpace(mediaUrl))
                    {
                        return true;
                    }
                }
            }

            foreach (var property in node.EnumerateObject())
            {
                if (TryFindAnyDirectVideoUrl(property.Value, out mediaUrl))
                {
                    return true;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (TryFindAnyDirectVideoUrl(item, out mediaUrl))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? NormalizeDirectVideoUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = WebUtility.HtmlDecode(value)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Trim();

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

        var normalized = WebUtility.HtmlDecode(value)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Trim();

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }

    private static string? FindMetaContent(string html, string property)
    {
        foreach (Match tagMatch in MetaTagRegex().Matches(html))
        {
            var tag = tagMatch.Value;
            var propertyMatch = MetaPropertyRegex().Match(tag);
            if (!propertyMatch.Success ||
                !propertyMatch.Groups["value"].Value.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var contentMatch = MetaContentRegex().Match(tag);
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

    private sealed record ResolveAttempt(ThreadsMedia? Media, int? StatusCode);

    private sealed record ThreadsCacheEntry(ThreadsMedia Media, DateTimeOffset ExpiresAt);

    [GeneratedRegex("<script\\b(?=[^>]*\\btype=[\\\"']application/json[\\\"'])(?=[^>]*\\bdata-sjs(?:\\s|=|>|/))[^>]*>(?<json>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DataSjsRegex();

    [GeneratedRegex("^[A-Za-z0-9._]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PostIdRegex();

    [GeneratedRegex("<meta\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex("(?:property|name)\\s*=\\s*[\"'](?<value>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaPropertyRegex();

    [GeneratedRegex("content\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaContentRegex();

    [GeneratedRegex("(?<url>https?(?:\\\\u003A|:)(?:\\\\/|/){2}[^\\\"'<>\\\\s]*(?:fbcdn\\\\.net|cdninstagram\\\\.com)[^\\\"'<>\\\\s]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCdnUrlRegex();
}

public sealed record ThreadsMedia(
    string MediaUrl,
    string Title,
    string? ThumbnailUrl,
    string Referer);
