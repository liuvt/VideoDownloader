using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Models;
using VideoDownloader.Blazor.Options;

namespace VideoDownloader.Blazor.Services;

public sealed partial class YtDlpService
{
    private readonly DownloaderOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<YtDlpService> _logger;
    private readonly ThreadsMediaResolver _threadsResolver;
    private readonly FacebookStoryResolver _facebookStoryResolver;
    private readonly ConcurrentDictionary<string, AnalysisCacheEntry> _analysisCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _instagramAnalyzeGate = new(1, 1);
    private readonly object _instagramCooldownLock = new();
    private DateTimeOffset _instagramCooldownUntil = DateTimeOffset.MinValue;

    public YtDlpService(
        IOptions<DownloaderOptions> options,
        IWebHostEnvironment environment,
        ILogger<YtDlpService> logger,
        ThreadsMediaResolver threadsResolver,
        FacebookStoryResolver facebookStoryResolver)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _threadsResolver = threadsResolver;
        _facebookStoryResolver = facebookStoryResolver;
    }

    public async Task<MediaAnalysisResult> AnalyzeAsync(
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeSourceUrl(sourceUrl);

        if (IsFacebookUrl(normalizedUrl))
        {
            var hasCookie = HasPlatformCookie(normalizedUrl);
            var preferCookie = _options.FacebookPreferCookies &&
                               hasCookie &&
                               IsFacebookCookiePreferredUrl(normalizedUrl);

            try
            {
                return await AnalyzeCoreAsync(
                    normalizedUrl,
                    usePlatformCookie: preferCookie,
                    cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException ex) when (
                !preferCookie &&
                hasCookie &&
                ShouldRetryFacebookWithCookie(ex.Message))
            {
                _logger.LogWarning(
                    "Facebook anonymous analysis failed for {Url}; retrying once with the configured Facebook cookies. Error: {Error}",
                    normalizedUrl,
                    ex.Message);

                return await AnalyzeCoreAsync(
                    normalizedUrl,
                    usePlatformCookie: true,
                    cancellationToken: cancellationToken);
            }
        }

        if (!IsInstagramUrl(normalizedUrl))
        {
            return await AnalyzeCoreAsync(
                normalizedUrl,
                usePlatformCookie: true,
                cancellationToken: cancellationToken);
        }

        if (TryGetCachedAnalysis(normalizedUrl, out var cached))
        {
            return cached;
        }

        await _instagramAnalyzeGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedAnalysis(normalizedUrl, out cached))
            {
                return cached;
            }

            if (IsInstagramCooldownActive(out var remaining))
            {
                throw new InvalidOperationException(
                    $"Instagram is temporarily rate-limiting this server. Retry after about {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} seconds.");
            }

            var hasCookie = HasPlatformCookie(normalizedUrl);
            var preferCookie = _options.InstagramPreferCookies && hasCookie;

            if (preferCookie)
            {
                try
                {
                    // On a public downloader, repeatedly probing Instagram anonymously
                    // before every authenticated request accelerates 429 rate limiting.
                    // If an operator provided cookies, use that browser session first.
                    var authenticatedResult = await AnalyzeCoreAsync(
                        normalizedUrl,
                        usePlatformCookie: true,
                        cancellationToken: cancellationToken);

                    CacheAnalysis(normalizedUrl, authenticatedResult);
                    return authenticatedResult;
                }
                catch (InvalidOperationException ex)
                {
                    if (IsRateLimited(ex.Message))
                    {
                        StartInstagramCooldown();
                    }

                    throw;
                }
            }

            try
            {
                var anonymousResult = await AnalyzeCoreAsync(
                    normalizedUrl,
                    usePlatformCookie: false,
                    cancellationToken: cancellationToken);

                CacheAnalysis(normalizedUrl, anonymousResult);
                return anonymousResult;
            }
            catch (InvalidOperationException ex) when (
                ShouldRetryInstagramWithCookie(ex.Message) && hasCookie)
            {
                _logger.LogWarning(
                    "Instagram anonymous analysis failed for {Url}; retrying once with the configured Instagram cookies. Error: {Error}",
                    normalizedUrl,
                    ex.Message);

                try
                {
                    var authenticatedResult = await AnalyzeCoreAsync(
                        normalizedUrl,
                        usePlatformCookie: true,
                        cancellationToken: cancellationToken);

                    CacheAnalysis(normalizedUrl, authenticatedResult);
                    return authenticatedResult;
                }
                catch (InvalidOperationException authenticatedException)
                {
                    if (IsRateLimited(authenticatedException.Message))
                    {
                        StartInstagramCooldown();
                    }

                    throw;
                }
            }
            catch (InvalidOperationException ex)
            {
                if (IsRateLimited(ex.Message))
                {
                    StartInstagramCooldown();
                }

                throw;
            }
        }
        finally
        {
            _instagramAnalyzeGate.Release();
        }
    }

    private async Task<MediaAnalysisResult> AnalyzeCoreAsync(
        string sourceUrl,
        bool usePlatformCookie,
        CancellationToken cancellationToken)
    {
        var effectiveUrl = ResolveFacebookExtractorInput(sourceUrl) ?? sourceUrl;
        string? referer = null;
        ThreadsMedia? threadsMedia = null;
        FacebookStoryMedia? facebookStoryMedia = null;

        if (!string.Equals(effectiveUrl, sourceUrl, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Resolved Facebook URL {SourceUrl} to extractor input {EffectiveUrl}.",
                sourceUrl,
                effectiveUrl);
        }

        if (_facebookStoryResolver.IsFacebookStoryUrl(sourceUrl))
        {
            facebookStoryMedia = await _facebookStoryResolver.TryResolveAsync(sourceUrl, cancellationToken);
            if (facebookStoryMedia is not null)
            {
                effectiveUrl = facebookStoryMedia.MediaUrl;
                referer = facebookStoryMedia.Referer;

                _logger.LogInformation(
                    "Resolved Facebook Story {SourceUrl} to a direct Meta CDN media URL.",
                    sourceUrl);
            }
        }

        if (_threadsResolver.IsThreadsUrl(sourceUrl))
        {
            threadsMedia = await _threadsResolver.TryResolveAsync(sourceUrl, cancellationToken);
            if (threadsMedia is null)
            {
                throw new InvalidOperationException(
                    "Could not extract video from this Threads post. The post may be private, unavailable, rate-limited, or Threads may have changed its page data.");
            }

            effectiveUrl = threadsMedia.MediaUrl;
            referer = threadsMedia.Referer;
        }

        var stdout = new StringBuilder();
        var errors = new Queue<string>();
        var arguments = new List<string>
        {
            "--no-config",
            "--dump-single-json",
            "--skip-download",
            "--no-playlist",
            "--no-warnings",
            "--ignore-no-formats-error",
            "--format", "all"
        };

        AddCacheArgument(arguments);
        AddCookiesArgument(arguments, sourceUrl, usePlatformCookie);
        AddRefererArgument(arguments, referer);
        AddPlatformCompatibilityArguments(arguments, sourceUrl);
        AddPlatformNetworkArguments(arguments, sourceUrl);
        arguments.Add(effectiveUrl);

        var exitCode = await RunProcessAsync(
            arguments,
            line => stdout.AppendLine(line),
            line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    errors.Enqueue(line.Trim());
                    while (errors.Count > 12)
                    {
                        errors.Dequeue();
                    }
                }
            },
            cancellationToken);

        if (exitCode != 0)
        {
            if (facebookStoryMedia is not null)
            {
                return new MediaAnalysisResult(
                    sourceUrl,
                    facebookStoryMedia.Title,
                    facebookStoryMedia.ThumbnailUrl,
                    null,
                    new[]
                    {
                        new MediaFormatOption(
                            "video-original",
                            DownloadKind.Video,
                            "Original MP4",
                            "Story source quality",
                            "Original",
                            "best")
                    });
            }

            if (threadsMedia is not null)
            {
                return new MediaAnalysisResult(
                    sourceUrl,
                    threadsMedia.Title,
                    threadsMedia.ThumbnailUrl,
                    null,
                    new[]
                    {
                        new MediaFormatOption(
                            "video-original",
                            DownloadKind.Video,
                            "Original MP4",
                            "Source quality",
                            "Original",
                            "best")
                    });
            }

            throw new InvalidOperationException(
                errors.Count == 0
                    ? "The available video formats could not be read."
                    : string.Join(Environment.NewLine, errors));
        }

        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;
        var formats = BuildAvailableFormats(root, IsYouTubeUrl(sourceUrl));

        if (formats.Count == 0)
        {
            throw new InvalidOperationException("No downloadable video or audio formats were reported for this URL.");
        }

        return new MediaAnalysisResult(
            sourceUrl,
            facebookStoryMedia?.Title ?? threadsMedia?.Title ?? GetString(root, "title") ?? GetFallbackTitle(sourceUrl),
            facebookStoryMedia?.ThumbnailUrl ?? threadsMedia?.ThumbnailUrl ?? GetString(root, "thumbnail"),
            GetInt64(root, "duration"),
            formats);
    }

    public async Task DownloadAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath,
            _options.DownloadRoot));
        var jobDirectory = Path.Combine(root, job.Id.ToString("N"));
        Directory.CreateDirectory(jobDirectory);

        var useDirectFallback = false;
        var effectiveUrl = ResolveFacebookExtractorInput(job.Url) ?? job.Url;
        string? referer = null;
        FacebookStoryMedia? facebookStoryMedia = null;

        if (!string.Equals(effectiveUrl, job.Url, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Resolved Facebook URL {SourceUrl} to extractor input {EffectiveUrl} for job {JobId}.",
                job.Url,
                effectiveUrl,
                job.Id);
        }

        if (_facebookStoryResolver.IsFacebookStoryUrl(job.Url))
        {
            facebookStoryMedia = await _facebookStoryResolver.TryResolveAsync(job.Url, cancellationToken);
            if (facebookStoryMedia is not null)
            {
                effectiveUrl = facebookStoryMedia.MediaUrl;
                referer = facebookStoryMedia.Referer;
                job.Title ??= facebookStoryMedia.Title;
                job.ThumbnailUrl ??= facebookStoryMedia.ThumbnailUrl;
                useDirectFallback = true;
                job.UsedDirectFallback = true;

                _logger.LogInformation(
                    "Resolved Facebook Story {SourceUrl} to a direct Meta CDN media URL for job {JobId}.",
                    job.Url,
                    job.Id);
            }
        }

        if (_threadsResolver.IsThreadsUrl(job.Url))
        {
            var threadsMedia = await _threadsResolver.TryResolveAsync(job.Url, cancellationToken);
            if (threadsMedia is null)
            {
                // yt-dlp has no native Threads extractor. Never fall through with the
                // original Threads page URL, otherwise GenericIE produces Unsupported URL.
                throw new InvalidOperationException(
                    "Could not extract video from this Threads post. The post may be private, unavailable, rate-limited, or Threads may have changed its page data.");
            }

            effectiveUrl = threadsMedia.MediaUrl;
            referer = threadsMedia.Referer;
            job.Title = threadsMedia.Title;
            job.ThumbnailUrl = threadsMedia.ThumbnailUrl;
            useDirectFallback = true;
            job.UsedDirectFallback = true;
        }

        if (!job.MetadataResolved)
        {
            job.Status = DownloadJobStatus.ReadingMetadata;
            try
            {
                await ReadMetadataAsync(job, effectiveUrl, referer, cancellationToken);
            }
            catch (Exception ex) when (CanUseDirectFallback(job.Url, ex))
            {
                useDirectFallback = true;
                job.UsedDirectFallback = true;
                job.Title ??= GetFallbackTitle(job.Url);

                _logger.LogWarning(
                    ex,
                    "Metadata extraction failed for job {JobId}. Retrying with direct {Kind} fallback.",
                    job.Id,
                    job.Kind);
            }
        }

        job.Status = DownloadJobStatus.Downloading;
        job.Progress = 0;
        job.Speed = null;
        job.Eta = null;

        var outputTemplate = Path.Combine(jobDirectory, "media.%(ext)s");
        var primaryArguments = useDirectFallback
            ? BuildDirectFallbackArguments(job, outputTemplate, effectiveUrl, referer)
            : BuildDownloadArguments(job, outputTemplate, effectiveUrl, referer);

        var result = await RunDownloadAttemptAsync(
            job,
            primaryArguments,
            cancellationToken);

        if (result.ExitCode != 0 &&
            !useDirectFallback &&
            !job.MetadataResolved &&
            IsDirectFallbackPlatform(job.Url))
        {
            _logger.LogWarning(
                "Primary yt-dlp attempt failed for job {JobId}. Running direct high-quality fallback. Error: {Error}",
                job.Id,
                result.ErrorText);

            CleanupAttemptFiles(jobDirectory);

            job.UsedDirectFallback = true;
            job.Status = DownloadJobStatus.Downloading;
            job.Progress = 0;
            job.Speed = null;
            job.Eta = null;

            var fallbackArguments = BuildDirectFallbackArguments(job, outputTemplate, effectiveUrl, referer);
            result = await RunDownloadAttemptAsync(
                job,
                fallbackArguments,
                cancellationToken);
        }

        if (result.ExitCode != 0 &&
            IsYouTubeUrl(job.Url) &&
            (IsHttp403(result.ErrorText) || IsRequestedFormatUnavailable(result.ErrorText)))
        {
            _logger.LogWarning(
                "YouTube download failed for job {JobId}. Re-extracting with a fresh selector for the requested quality. Error: {Error}",
                job.Id,
                result.ErrorText);

            CleanupAttemptFiles(jobDirectory);
            job.Status = DownloadJobStatus.Downloading;
            job.Progress = 0;
            job.Speed = null;
            job.Eta = null;

            var youtubeRetryArguments = BuildYouTubeRetryArguments(
                job,
                outputTemplate,
                effectiveUrl,
                referer);

            result = await RunDownloadAttemptAsync(
                job,
                youtubeRetryArguments,
                cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.ErrorText)
                    ? $"yt-dlp exited with code {result.ExitCode}."
                    : result.ErrorText);
        }

        job.Status = DownloadJobStatus.Processing;

        var finalPath = ResolveFinalPath(result.FinalPath, jobDirectory);
        if (finalPath is null)
        {
            throw new FileNotFoundException("The downloaded file could not be found.");
        }

        var safeTitle = SanitizeFileName(job.Title ?? GetFallbackTitle(job.Url));
        var extension = Path.GetExtension(finalPath);
        var destination = Path.Combine(jobDirectory, safeTitle + extension);

        if (!Path.GetFullPath(finalPath).Equals(
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            destination = GetUniquePath(destination);
            File.Move(finalPath, destination);
        }
        else
        {
            destination = finalPath;
        }

        job.FilePath = destination;
        job.FileName = Path.GetFileName(destination);
        job.Progress = 100;
        job.Speed = null;
        job.Eta = null;
        job.Status = DownloadJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
    }

    private async Task<DownloadAttemptResult> RunDownloadAttemptAsync(
        DownloadJob job,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        string? finalPath = null;
        var errors = new Queue<string>();

        var exitCode = await RunProcessAsync(
            arguments,
            line =>
            {
                if (line.StartsWith("FILEPATH=", StringComparison.Ordinal))
                {
                    finalPath = line["FILEPATH=".Length..].Trim();
                }

                ParseProgress(job, line);
            },
            line =>
            {
                ParseProgress(job, line);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    errors.Enqueue(line.Trim());
                    while (errors.Count > 16)
                    {
                        errors.Dequeue();
                    }
                }
            },
            cancellationToken);

        return new DownloadAttemptResult(
            exitCode,
            finalPath,
            string.Join(Environment.NewLine, errors));
    }

    private async Task ReadMetadataAsync(DownloadJob job, string inputUrl, string? referer, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var errors = new Queue<string>();
        var arguments = new List<string>
        {
            "--no-config",
            "--dump-single-json",
            "--skip-download",
            "--no-playlist",
            "--no-warnings",
            "--ignore-no-formats-error",
            "--format", "all"
        };

        AddCacheArgument(arguments);
        AddCookiesArgument(arguments, job.Url, usePlatformCookie: true);
        AddRefererArgument(arguments, referer);
        AddPlatformCompatibilityArguments(arguments, job.Url);
        AddPlatformNetworkArguments(arguments, job.Url);
        arguments.Add(inputUrl);

        var exitCode = await RunProcessAsync(
            arguments,
            line => stdout.AppendLine(line),
            line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    errors.Enqueue(line.Trim());
                    while (errors.Count > 8)
                    {
                        errors.Dequeue();
                    }
                }
            },
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                errors.Count == 0
                    ? "The video details could not be read."
                    : string.Join(Environment.NewLine, errors));
        }

        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;

        job.Title ??= GetString(root, "title") ?? "video";
        job.ThumbnailUrl ??= GetString(root, "thumbnail");
        job.DurationSeconds = GetInt64(root, "duration");
    }

    private List<string> BuildDownloadArguments(DownloadJob job, string outputTemplate, string inputUrl, string? referer)
    {
        var arguments = BuildCommonDownloadArguments(outputTemplate, referer, job.Url);
        AddPlatformCompatibilityArguments(arguments, job.Url);

        if (job.Kind == DownloadKind.Audio)
        {
            arguments.AddRange(new[]
            {
                "--format", "bestaudio/best",
                "--extract-audio",
                "--audio-format", "mp3",
                "--audio-quality", "0"
            });
        }
        else
        {
            var selector = BuildStableVideoSelector(job);

            arguments.AddRange(new[]
            {
                "--format", selector,
                "--merge-output-format", "mp4",
                "--remux-video", "mp4"
            });
        }

        arguments.Add(inputUrl);
        return arguments;
    }

    private List<string> BuildDirectFallbackArguments(
        DownloadJob job,
        string outputTemplate,
        string inputUrl,
        string? referer)
    {
        var arguments = BuildCommonDownloadArguments(outputTemplate, referer, job.Url);
        AddPlatformCompatibilityArguments(arguments, job.Url);

        // Keep the exact format chosen from the analyzer. A fallback must never
        // silently replace a requested 720p format with some other resolution.
        if (job.Kind == DownloadKind.Audio)
        {
            arguments.AddRange(new[]
            {
                "--format", "bestaudio/best",
                "--extract-audio",
                "--audio-format", "mp3",
                "--audio-quality", "0"
            });
        }
        else
        {
            arguments.AddRange(new[]
            {
                "--format", BuildStableVideoSelector(job),
                "--merge-output-format", "mp4",
                "--remux-video", "mp4"
            });
        }

        arguments.Add(inputUrl);
        return arguments;
    }

    private List<string> BuildYouTubeRetryArguments(
        DownloadJob job,
        string outputTemplate,
        string inputUrl,
        string? referer)
    {
        var arguments = BuildCommonDownloadArguments(outputTemplate, referer, job.Url);
        AddPlatformCompatibilityArguments(arguments, job.Url);

        if (job.Kind == DownloadKind.Audio)
        {
            arguments.AddRange(new[]
            {
                "--format", "bestaudio/best",
                "--extract-audio",
                "--audio-format", "mp3",
                "--audio-quality", "0"
            });
        }
        else
        {
            arguments.AddRange(new[]
            {
                "--format", BuildStableVideoSelector(job),
                "--merge-output-format", "mp4",
                "--remux-video", "mp4"
            });
        }

        arguments.Add(inputUrl);
        return arguments;
    }

    private static string BuildStableVideoSelector(DownloadJob job)
    {
        var match = Regex.Match(job.Quality, @"^(?<height>\d+)p$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return "bestvideo*+bestaudio/best";
        }

        var height = match.Groups["height"].Value;
        return $"bv*[height={height}]+ba/b[height={height}]/bv*[height<={height}]+ba/b[height<={height}]/best";
    }

    private List<string> BuildCommonDownloadArguments(string outputTemplate, string? referer, string sourceUrl)
    {
        var arguments = new List<string>
        {
            "--no-config",
            "--no-playlist",
            "--newline",
            "--max-filesize", _options.MaxVideoSize,
            "--progress-template",
            "download:PROGRESS=%(progress._percent_str)s|SPEED=%(progress._speed_str)s|ETA=%(progress._eta_str)s",
            "--print", "after_move:FILEPATH=%(filepath)s",
            "--output", outputTemplate
        };

        AddCacheArgument(arguments);
        AddCookiesArgument(arguments, sourceUrl, usePlatformCookie: true);
        AddRefererArgument(arguments, referer);
        AddPlatformNetworkArguments(arguments, sourceUrl);
        return arguments;
    }

    private void AddPlatformCompatibilityArguments(List<string> arguments, string sourceUrl)
    {
        if (IsTikTokUrl(sourceUrl))
        {
            var impersonate = _options.TikTokImpersonate?.Trim();
            if (!string.IsNullOrWhiteSpace(impersonate))
            {
                arguments.Add("--impersonate");
                arguments.Add(impersonate);
            }

            return;
        }

        if (!_options.YouTubeCompatibilityMode || !IsYouTubeUrl(sourceUrl))
        {
            return;
        }

        if (_options.YouTubeEnableRemoteEjs)
        {
            // Allows yt-dlp to fetch matching EJS challenge scripts when the
            // installed distribution does not already bundle them.
            arguments.Add("--remote-components");
            arguments.Add("ejs:github");
        }

        var jsRuntime = _options.YouTubeJavaScriptRuntime?.Trim();
        if (string.IsNullOrWhiteSpace(jsRuntime) && IsExecutableOnPath("node"))
        {
            // Deno is auto-discovered by yt-dlp. Node must be explicitly enabled.
            jsRuntime = "node";
        }

        if (!string.IsNullOrWhiteSpace(jsRuntime))
        {
            arguments.Add("--js-runtimes");
            arguments.Add(jsRuntime);
        }

        if (!string.IsNullOrWhiteSpace(_options.YouTubePlayerClient))
        {
            arguments.Add("--extractor-args");
            arguments.Add($"youtube:player_client={_options.YouTubePlayerClient.Trim()}");
        }
    }

    private void AddPlatformNetworkArguments(List<string> arguments, string sourceUrl)
    {
        if (IsTikTokUrl(sourceUrl) && _options.TikTokForceIPv4)
        {
            arguments.Add("-4");
        }

        string? proxy = null;

        if (IsInstagramUrl(sourceUrl))
        {
            proxy = _options.InstagramProxy;
        }
        else if (IsFacebookUrl(sourceUrl))
        {
            proxy = _options.FacebookProxy;
        }

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            arguments.Add("--proxy");
            arguments.Add(proxy.Trim());
        }
    }

    private static bool IsExecutableOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var fileNames = OperatingSystem.IsWindows()
            ? new[] { executableName + ".exe", executableName + ".cmd", executableName + ".bat", executableName }
            : new[] { executableName };

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in fileNames)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, fileName)))
                    {
                        return true;
                    }
                }
                catch (Exception) when (directory.Length > 0)
                {
                    // Ignore malformed or inaccessible PATH entries.
                }
            }
        }

        return false;
    }

    private static void AddRefererArgument(List<string> arguments, string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer))
        {
            return;
        }

        arguments.Add("--referer");
        arguments.Add(referer);
        arguments.Add("--user-agent");
        arguments.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
    }

    private void AddCacheArgument(List<string> arguments)
    {
        var cachePath = ResolveCachePath();
        if (cachePath is null)
        {
            arguments.Add("--no-cache-dir");
            return;
        }

        arguments.Add("--cache-dir");
        arguments.Add(cachePath);
    }

    private string? ResolveCachePath()
    {
        try
        {
            var configured = string.IsNullOrWhiteSpace(_options.CacheRoot)
                ? "App_Data/cache"
                : _options.CacheRoot;
            var path = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(_environment.ContentRootPath, configured);
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "yt-dlp cache directory is not writable. Running without a persistent cache.");
            return null;
        }
    }

    private void AddCookiesArgument(
        List<string> arguments,
        string sourceUrl,
        bool usePlatformCookie)
    {
        var configuredFile = usePlatformCookie
            ? GetPlatformCookiesFile(sourceUrl)
            : null;

        if (string.IsNullOrWhiteSpace(configuredFile))
        {
            configuredFile = _options.CookiesFile;
        }

        var cookiePath = ResolveOptionalPath(configuredFile);
        if (cookiePath is null)
        {
            return;
        }

        arguments.Add("--cookies");
        arguments.Add(cookiePath);
    }

    private string? GetPlatformCookiesFile(string sourceUrl)
    {
        if (IsInstagramUrl(sourceUrl) && !string.IsNullOrWhiteSpace(_options.InstagramCookiesFile))
        {
            return _options.InstagramCookiesFile;
        }

        if (IsFacebookUrl(sourceUrl) && !string.IsNullOrWhiteSpace(_options.FacebookCookiesFile))
        {
            return _options.FacebookCookiesFile;
        }

        return null;
    }

    private bool HasPlatformCookie(string sourceUrl) =>
        ResolveOptionalPath(GetPlatformCookiesFile(sourceUrl)) is not null;

    private string? ResolveOptionalPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_environment.ContentRootPath, configuredPath);

        if (!File.Exists(path))
        {
            _logger.LogDebug("Configured cookies file was not found: {Path}", path);
            return null;
        }

        return path;
    }

    private bool TryGetCachedAnalysis(string sourceUrl, out MediaAnalysisResult result)
    {
        result = default!;
        if (_options.AnalysisCacheMinutes <= 0 || !_analysisCache.TryGetValue(sourceUrl, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _analysisCache.TryRemove(sourceUrl, out _);
            return false;
        }

        result = entry.Result;
        return true;
    }

    private void CacheAnalysis(string sourceUrl, MediaAnalysisResult result)
    {
        if (_options.AnalysisCacheMinutes <= 0)
        {
            return;
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(
            Math.Clamp(_options.AnalysisCacheMinutes, 1, 120));
        _analysisCache[sourceUrl] = new AnalysisCacheEntry(result, expiresAt);
    }

    private void StartInstagramCooldown()
    {
        var seconds = Math.Clamp(_options.InstagramRateLimitCooldownSeconds, 0, 3600);
        if (seconds <= 0)
        {
            return;
        }

        lock (_instagramCooldownLock)
        {
            _instagramCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
    }

    private bool IsInstagramCooldownActive(out TimeSpan remaining)
    {
        lock (_instagramCooldownLock)
        {
            remaining = _instagramCooldownUntil - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero;
        }
    }

    private static bool ShouldRetryFacebookWithCookie(string errorText) =>
        !string.IsNullOrWhiteSpace(errorText) &&
        (errorText.Contains("login.php", StringComparison.OrdinalIgnoreCase) ||
         errorText.Contains("login required", StringComparison.OrdinalIgnoreCase) ||
         errorText.Contains("log in", StringComparison.OrdinalIgnoreCase) ||
         errorText.Contains("registered users", StringComparison.OrdinalIgnoreCase) ||
         errorText.Contains("cookies", StringComparison.OrdinalIgnoreCase) ||
         errorText.Contains("not available", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldRetryInstagramWithCookie(string errorText) =>
        IsRateLimited(errorText) ||
        errorText.Contains("isn't available to everyone", StringComparison.OrdinalIgnoreCase) ||
        errorText.Contains("not available to everyone", StringComparison.OrdinalIgnoreCase) ||
        errorText.Contains("login required", StringComparison.OrdinalIgnoreCase) ||
        errorText.Contains("log in", StringComparison.OrdinalIgnoreCase) ||
        errorText.Contains("registered users", StringComparison.OrdinalIgnoreCase) ||
        errorText.Contains("requested content is not available", StringComparison.OrdinalIgnoreCase);

    private static bool IsRateLimited(string errorText) =>
        !string.IsNullOrWhiteSpace(errorText) &&
        (errorText.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase) ||
         errorText.Contains("429: Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
         errorText.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeSourceUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return sourceUrl.Trim();
        }

        // Tracking query parameters make the same public post look like different
        // cache keys. Instagram/Threads post identity is fully represented by path.
        if (IsInstagramUrl(sourceUrl) ||
            IsHostOrSubdomain(uri.Host, "threads.com") ||
            IsHostOrSubdomain(uri.Host, "threads.net"))
        {
            return new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri.AbsoluteUri;
        }

        return uri.AbsoluteUri;
    }

    private async Task<int> RunProcessAsync(
        IReadOnlyCollection<string> arguments,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveYtDlpPath(),
            WorkingDirectory = _environment.ContentRootPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var runtimeCache = ResolveCachePath();
        if (!string.IsNullOrWhiteSpace(runtimeCache))
        {
            startInfo.Environment["XDG_CACHE_HOME"] = runtimeCache;
            var denoDirectory = Path.Combine(runtimeCache, "deno");
            Directory.CreateDirectory(denoDirectory);
            startInfo.Environment["DENO_DIR"] = denoDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("yt-dlp could not be started.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"yt-dlp was not found at '{ResolveYtDlpPath()}'. Check Downloader:YtDlpPath in appsettings.json.", ex);
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The yt-dlp process could not be stopped.");
            }
        });

        var outputTask = ReadLinesAsync(process.StandardOutput, onOutput);
        var errorTask = ReadLinesAsync(process.StandardError, onError);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);
        return process.ExitCode;
    }

    private string ResolveYtDlpPath()
    {
        var configured = _options.YtDlpPath?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathRooted(configured) && File.Exists(configured))
            {
                return configured;
            }

            if (!Path.IsPathRooted(configured))
            {
                var normalized = configured
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                var localPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, normalized));
                if (File.Exists(localPath))
                {
                    return localPath;
                }
            }
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(_environment.ContentRootPath, "Tools", "yt-dlp.exe"),
                "yt-dlp.exe",
                "yt-dlp"
            }
            : new[]
            {
                "/usr/local/bin/yt-dlp",
                "/usr/bin/yt-dlp",
                Path.Combine(_environment.ContentRootPath, "Tools", "yt-dlp"),
                "yt-dlp"
            };

        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }

            if (!Path.IsPathRooted(candidate) && IsExecutableOnPath(candidate))
            {
                return candidate;
            }
        }

        return string.IsNullOrWhiteSpace(configured) ? "yt-dlp" : configured;
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> callback)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            callback(line);
        }
    }

    private static void ParseProgress(DownloadJob job, string line)
    {
        var match = ProgressRegex().Match(line);
        if (!match.Success)
        {
            if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[ExtractAudio]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[VideoConvertor]", StringComparison.OrdinalIgnoreCase))
            {
                job.Status = DownloadJobStatus.Processing;
            }

            return;
        }

        if (double.TryParse(
                match.Groups["percent"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percent))
        {
            job.Progress = Math.Clamp(percent, 0, 100);
        }

        job.Speed = NormalizeProgressValue(match.Groups["speed"].Value);
        job.Eta = NormalizeProgressValue(match.Groups["eta"].Value);
    }

    private static string? NormalizeProgressValue(string value)
    {
        var normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized == "NA"
            ? null
            : normalized;
    }

    private static bool CanUseDirectFallback(string url, Exception exception)
    {
        if (!IsDirectFallbackPlatform(url) || exception is OperationCanceledException)
        {
            return false;
        }

        // Retrying cannot fix a missing executable or an invalid server setup.
        return !exception.Message.Contains(
            "yt-dlp was not found",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYouTubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return IsHostOrSubdomain(host, "youtube.com") ||
               host == "youtu.be" ||
               IsHostOrSubdomain(host, "youtube-nocookie.com");
    }

    private static bool IsTikTokUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return IsHostOrSubdomain(host, "tiktok.com") ||
               host == "vm.tiktok.com" ||
               host == "vt.tiktok.com";
    }

    private static string? ResolveFacebookExtractorInput(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (!IsHostOrSubdomain(host, "facebook.com"))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Avoid the Facebook website login redirect when a Reel exposes its media id.
        // The Facebook extractor accepts the pseudo input facebook:<id>.
        if (segments.Length >= 2 &&
            (segments[0].Equals("reel", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("reels", StringComparison.OrdinalIgnoreCase)) &&
            long.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return $"facebook:{segments[1]}";
        }

        // Some story share URLs include the actual story/video id in the query.
        foreach (var queryName in new[] { "story_fbid", "story_id", "video_id" })
        {
            var queryValue = GetQueryParameter(uri, queryName);
            if (!string.IsNullOrWhiteSpace(queryValue) &&
                long.TryParse(queryValue, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return $"facebook:{queryValue}";
            }
        }

        var storiesIndex = Array.FindIndex(
            segments,
            segment => segment.Equals("stories", StringComparison.OrdinalIgnoreCase));

        if (storiesIndex < 0)
        {
            return null;
        }

        // Legacy Facebook story links can be /stories/<story-id>. yt-dlp does not
        // register the /stories route, but its Facebook extractor accepts facebook:<id>.
        if (storiesIndex + 2 >= segments.Length)
        {
            if (storiesIndex + 1 < segments.Length &&
                long.TryParse(
                    Uri.UnescapeDataString(segments[storiesIndex + 1]),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                return $"facebook:{Uri.UnescapeDataString(segments[storiesIndex + 1])}";
            }

            return null;
        }

        // Newer shared story links are commonly /stories/<owner>/<opaque-token>.
        // The opaque token can be base64/base64url and often ends in the media id.
        var storyToken = Uri.UnescapeDataString(segments[storiesIndex + 2]);

        if (long.TryParse(storyToken, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return $"facebook:{storyToken}";
        }

        var decoded = TryDecodeFacebookStoryToken(storyToken);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return null;
        }

        var idMatches = Regex.Matches(
            decoded,
            @"(?<id>\d{8,})",
            RegexOptions.CultureInvariant);

        return idMatches.Count > 0
            ? $"facebook:{idMatches[idMatches.Count - 1].Groups["id"].Value}"
            : null;
    }

    private static string? GetQueryParameter(Uri uri, string name)
    {
        if (string.IsNullOrWhiteSpace(uri.Query))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator >= 0 ? pair[..separator] : pair;
            if (!Uri.UnescapeDataString(key).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return null;
    }

    private static string? TryDecodeFacebookStoryToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var normalized = token.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsInstagramUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return IsHostOrSubdomain(host, "instagram.com") || host == "instagr.am";
    }

    private static bool IsFacebookCookiePreferredUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.Contains("/stories/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/reel/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/reels/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/share/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFacebookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return IsHostOrSubdomain(host, "facebook.com") || host == "fb.watch";
    }

    private static bool IsHttp403(string errorText) =>
        !string.IsNullOrWhiteSpace(errorText) &&
        (errorText.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) ||
         errorText.Contains("403: Forbidden", StringComparison.OrdinalIgnoreCase));

    private static bool IsRequestedFormatUnavailable(string errorText) =>
        !string.IsNullOrWhiteSpace(errorText) &&
        errorText.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectFallbackPlatform(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return IsYouTubeUrl(url) ||
               IsHostOrSubdomain(host, "facebook.com") ||
               host == "fb.watch" ||
               IsHostOrSubdomain(host, "tiktok.com") ||
               IsHostOrSubdomain(host, "instagram.com") ||
               host == "instagr.am" ||
               IsHostOrSubdomain(host, "x.com") ||
               IsHostOrSubdomain(host, "twitter.com") ||
               IsHostOrSubdomain(host, "reddit.com") ||
               host == "redd.it" ||
               IsHostOrSubdomain(host, "threads.com") ||
               IsHostOrSubdomain(host, "threads.net");
    }

    private static bool IsHostOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);

    private static string GetFallbackTitle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "downloaded-media";
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("youtube", StringComparison.Ordinal) || host == "youtu.be")
        {
            return "YouTube video";
        }

        if (host.Contains("facebook", StringComparison.Ordinal) || host == "fb.watch")
        {
            return "Facebook video";
        }

        if (host.Contains("tiktok", StringComparison.Ordinal))
        {
            return "TikTok video";
        }

        if (host.Contains("instagram", StringComparison.Ordinal) || host == "instagr.am")
        {
            return "Instagram video";
        }

        if (host == "x.com" || host.EndsWith(".x.com", StringComparison.Ordinal) ||
            host.Contains("twitter", StringComparison.Ordinal))
        {
            return "X video";
        }

        if (host.Contains("reddit", StringComparison.Ordinal) || host == "redd.it")
        {
            return "Reddit video";
        }

        if (host.Contains("threads", StringComparison.Ordinal))
        {
            return "Threads video";
        }

        return "downloaded-media";
    }

    private void CleanupAttemptFiles(string jobDirectory)
    {
        if (!Directory.Exists(jobDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(jobDirectory))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not remove partial file {Path} before direct fallback.",
                    path);
            }
        }
    }

    private static IReadOnlyList<MediaFormatOption> BuildAvailableFormats(
        JsonElement root,
        bool isYouTube)
    {
        var candidates = new List<FormatCandidate>();
        CollectFormatCandidates(root, candidates);

        // Some extractors (including Instagram posts/carousels) can expose the
        // playable item inside `entries` instead of putting `formats` directly
        // on the root object. CollectFormatCandidates walks those objects too.
        // It also accepts video formats that expose dimensions/container but omit
        // vcodec/acodec, which happens on some direct social-media MP4 responses.
        var usableCandidates = candidates
            .GroupBy(x => $"{x.Id}|{x.Height}|{x.Ext}|{x.Protocol}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        var bestAudio = usableCandidates
            .Where(x => x.HasAudio && !x.HasVideo)
            .OrderByDescending(AudioScore)
            .FirstOrDefault();

        var muxedAudioFallback = usableCandidates
            .Where(x => x.HasAudio && x.HasVideo)
            .OrderByDescending(VideoScore)
            .FirstOrDefault();

        var videoOptions = usableCandidates
            .Where(x =>
                x.HasVideo &&
                x.Height is > 0 &&
                (!isYouTube || x.HasAudio || bestAudio is not null))
            .GroupBy(x => x.Height!.Value)
            .Select(group => group.OrderByDescending(VideoScore).First())
            .OrderByDescending(x => x.Height)
            .Select(video =>
            {
                // Do not persist a transient platform format id as the only
                // selector. Social platforms can return different ids between
                // analysis and the later download request.
                var selector = isYouTube
                    ? $"bv*[height={video.Height!.Value}]+ba/b[height={video.Height.Value}]/bv*[height<={video.Height.Value}]+ba/b[height<={video.Height.Value}]/best"
                    : $"bestvideo*[height={video.Height!.Value}]+bestaudio/best[height={video.Height.Value}]/bestvideo*[height<={video.Height.Value}]+bestaudio/best[height<={video.Height.Value}]/best";

                var approximateBytes = video.Bytes;
                if (!video.HasAudio && bestAudio?.Bytes is long audioBytes)
                {
                    approximateBytes = (approximateBytes ?? 0) + audioBytes;
                }

                var quality = $"{video.Height}p";
                return new MediaFormatOption(
                    $"video-{video.Height}-{video.Id}",
                    DownloadKind.Video,
                    $"{quality} MP4",
                    BuildFormatDetail(video, approximateBytes),
                    quality,
                    selector,
                    video.Height,
                    approximateBytes);
            })
            .ToList();

        // If the source has video but does not expose dimensions, offer one known
        // source-quality button rather than inventing a resolution. For non-YouTube
        // sources we do not require explicit audio codec metadata: Instagram,
        // Facebook and TikTok frequently expose a muxed MP4 without acodec/vcodec.
        if (videoOptions.Count == 0)
        {
            var sourceVideo = usableCandidates
                .Where(x => x.HasVideo && (!isYouTube || x.HasAudio || bestAudio is not null))
                .OrderByDescending(VideoScore)
                .FirstOrDefault();

            if (sourceVideo is not null)
            {
                videoOptions.Add(new MediaFormatOption(
                    $"video-source-{sourceVideo.Id}",
                    DownloadKind.Video,
                    "Original MP4",
                    BuildFormatDetail(sourceVideo, sourceVideo.Bytes),
                    "Original",
                    "bestvideo*+bestaudio/best",
                    null,
                    sourceVideo.Bytes));
            }
        }

        var result = new List<MediaFormatOption>(videoOptions);
        var audioSource = bestAudio ?? muxedAudioFallback;
        if (audioSource is not null)
        {
            var audioBytes = audioSource.Bytes;
            result.Add(new MediaFormatOption(
                "audio-mp3",
                DownloadKind.Audio,
                "MP3 audio",
                audioBytes is > 0 ? $"Best audio · {FormatBytes(audioBytes.Value)}" : "Best available audio",
                "MP3",
                "bestaudio/best",
                null,
                audioBytes));
        }

        return result;
    }

    private static void CollectFormatCandidates(JsonElement node, List<FormatCandidate> candidates)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (node.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formats.EnumerateArray())
            {
                AddFormatCandidate(format, candidates);
            }
        }

        // yt-dlp may place already-selected formats here even when the extractor
        // does not populate a conventional `formats` array.
        if (node.TryGetProperty("requested_formats", out var requestedFormats) && requestedFormats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in requestedFormats.EnumerateArray())
            {
                AddFormatCandidate(format, candidates);
            }
        }

        if (node.TryGetProperty("requested_downloads", out var requestedDownloads) && requestedDownloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in requestedDownloads.EnumerateArray())
            {
                AddFormatCandidate(format, candidates);
            }
        }

        // Instagram carousel/reel extraction may be represented as entries.
        if (node.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    CollectFormatCandidates(entry, candidates);
                    AddRootCandidate(entry, candidates);
                }
            }
        }

        AddRootCandidate(node, candidates);
    }

    private static void AddFormatCandidate(JsonElement format, List<FormatCandidate> candidates)
    {
        if (format.ValueKind != JsonValueKind.Object || IsDrm(format))
        {
            return;
        }

        var id = GetString(format, "format_id") ?? GetString(format, "format") ?? "best";
        var ext = GetString(format, "ext") ?? string.Empty;
        var videoCodec = GetString(format, "vcodec");
        var audioCodec = GetString(format, "acodec");
        var width = GetInt32(format, "width");
        var height = GetInt32(format, "height");

        var hasVideo = HasCodec(videoCodec) ||
                       HasMediaExtension(GetString(format, "video_ext"), VideoExtensions) ||
                       ((width is > 0 || height is > 0) && IsLikelyVideoExtension(ext));

        var hasAudio = HasCodec(audioCodec) ||
                       HasMediaExtension(GetString(format, "audio_ext"), AudioExtensions) ||
                       GetDouble(format, "abr") is > 0 ||
                       GetInt32(format, "asr") is > 0;

        if (!hasVideo && !hasAudio)
        {
            return;
        }

        candidates.Add(new FormatCandidate(
            id,
            ext,
            hasVideo,
            hasAudio,
            width,
            height,
            GetDouble(format, "fps"),
            GetDouble(format, "tbr"),
            GetDouble(format, "abr"),
            GetInt64(format, "filesize") ?? GetInt64(format, "filesize_approx"),
            videoCodec,
            audioCodec,
            GetString(format, "protocol") ?? string.Empty));
    }

    private static void AddRootCandidate(JsonElement node, List<FormatCandidate> candidates)
    {
        if (node.ValueKind != JsonValueKind.Object || IsDrm(node))
        {
            return;
        }

        // Only synthesize a root candidate when yt-dlp reports a concrete media
        // URL or media characteristics. This avoids treating playlist metadata as
        // a downloadable file.
        var mediaUrl = GetString(node, "url");
        var ext = GetString(node, "ext") ?? string.Empty;
        var width = GetInt32(node, "width");
        var height = GetInt32(node, "height");
        var videoCodec = GetString(node, "vcodec");
        var audioCodec = GetString(node, "acodec");

        if (string.IsNullOrWhiteSpace(mediaUrl) &&
            width is null && height is null &&
            !HasCodec(videoCodec) && !HasCodec(audioCodec))
        {
            return;
        }

        var hasVideo = HasCodec(videoCodec) ||
                       HasMediaExtension(GetString(node, "video_ext"), VideoExtensions) ||
                       width is > 0 || height is > 0 ||
                       IsLikelyVideoExtension(ext);

        var hasAudio = HasCodec(audioCodec) ||
                       HasMediaExtension(GetString(node, "audio_ext"), AudioExtensions) ||
                       GetDouble(node, "abr") is > 0 ||
                       GetInt32(node, "asr") is > 0;

        if (!hasVideo && !hasAudio)
        {
            return;
        }

        candidates.Add(new FormatCandidate(
            GetString(node, "format_id") ?? "best",
            ext,
            hasVideo,
            hasAudio,
            width,
            height,
            GetDouble(node, "fps"),
            GetDouble(node, "tbr"),
            GetDouble(node, "abr"),
            GetInt64(node, "filesize") ?? GetInt64(node, "filesize_approx"),
            videoCodec,
            audioCodec,
            GetString(node, "protocol") ?? string.Empty));
    }

    private static bool HasMediaExtension(string? extension, IReadOnlySet<string> knownExtensions) =>
        !string.IsNullOrWhiteSpace(extension) &&
        !extension.Equals("none", StringComparison.OrdinalIgnoreCase) &&
        knownExtensions.Contains(extension.TrimStart('.'));

    private static bool IsLikelyVideoExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && VideoExtensions.Contains(extension.TrimStart('.'));

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "webm", "mov", "mkv", "flv", "ts", "m2ts", "3gp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "m4a", "mp3", "aac", "opus", "ogg", "oga", "wav", "flac", "webm"
    };

    private static double VideoScore(FormatCandidate format)
    {
        var score = 0d;
        if (format.Ext.Equals("mp4", StringComparison.OrdinalIgnoreCase)) score += 1_000_000;
        if (format.VideoCodec?.StartsWith("avc", StringComparison.OrdinalIgnoreCase) == true ||
            format.VideoCodec?.StartsWith("h264", StringComparison.OrdinalIgnoreCase) == true) score += 200_000;
        score += (format.Fps ?? 0) * 1_000;
        score += format.TotalBitrate ?? 0;
        if (format.HasAudio) score += 10;
        return score;
    }

    private static double AudioScore(FormatCandidate format)
    {
        var score = format.AudioBitrate ?? format.TotalBitrate ?? 0;
        if (format.Ext.Equals("m4a", StringComparison.OrdinalIgnoreCase)) score += 100_000;
        if (format.AudioCodec?.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) == true) score += 50_000;
        return score;
    }

    private static string BuildFormatDetail(FormatCandidate format, long? bytes)
    {
        var parts = new List<string>();
        if (format.Width is > 0 && format.Height is > 0)
        {
            parts.Add($"{format.Width}×{format.Height}");
        }

        if (format.Fps is >= 1)
        {
            parts.Add($"{Math.Round(format.Fps.Value):0} fps");
        }

        if (!string.IsNullOrWhiteSpace(format.Ext))
        {
            parts.Add(format.Ext.ToUpperInvariant());
        }

        if (bytes is > 0)
        {
            parts.Add($"~{FormatBytes(bytes.Value)}");
        }

        return parts.Count == 0 ? "Source format" : string.Join(" · ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static bool HasCodec(string? codec) =>
        !string.IsNullOrWhiteSpace(codec) &&
        !codec.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static bool IsDrm(JsonElement format) =>
        format.TryGetProperty("has_drm", out var property) &&
        property.ValueKind == JsonValueKind.True;

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        return null;
    }

    private sealed record FormatCandidate(
        string Id,
        string Ext,
        bool HasVideo,
        bool HasAudio,
        int? Width,
        int? Height,
        double? Fps,
        double? TotalBitrate,
        double? AudioBitrate,
        long? Bytes,
        string? VideoCodec,
        string? AudioCodec,
        string Protocol);

    private static string? ResolveFinalPath(string? reportedPath, string jobDirectory)
    {
        if (!string.IsNullOrWhiteSpace(reportedPath) && File.Exists(reportedPath))
        {
            var fullReported = Path.GetFullPath(reportedPath);
            var fullDirectory = Path.GetFullPath(jobDirectory) + Path.DirectorySeparatorChar;
            if (fullReported.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return fullReported;
            }
        }

        return Directory
            .EnumerateFiles(jobDirectory)
            .Where(path =>
                !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch)
            .ToArray();
        var result = new string(chars).Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(result))
        {
            result = "video";
        }

        return result.Length <= 120 ? result : result[..120].Trim();
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return null;
    }

    private sealed record AnalysisCacheEntry(
        MediaAnalysisResult Result,
        DateTimeOffset ExpiresAt);

    private sealed record DownloadAttemptResult(
        int ExitCode,
        string? FinalPath,
        string ErrorText);

    [GeneratedRegex(@"PROGRESS=\s*(?<percent>\d+(?:\.\d+)?)%\|SPEED=(?<speed>[^|]*)\|ETA=(?<eta>.*)$")]
    private static partial Regex ProgressRegex();
}
