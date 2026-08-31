namespace VideoDownloader.Blazor.Options;

public sealed class DownloaderOptions
{
    public const string SectionName = "Downloader";

    public string YtDlpPath { get; set; } = "yt-dlp";
    public string DownloadRoot { get; set; } = "App_Data/downloads";
    public string CacheRoot { get; set; } = "App_Data/cache";
    public string CookiesFile { get; set; } = string.Empty;
    public string InstagramCookiesFile { get; set; } = string.Empty;
    public string FacebookCookiesFile { get; set; } = string.Empty;
    public string ThreadsCookiesFile { get; set; } = string.Empty;
    public bool InstagramPreferCookies { get; set; } = true;
    public bool FacebookPreferCookies { get; set; } = true;
    public string InstagramProxy { get; set; } = string.Empty;
    public string FacebookProxy { get; set; } = string.Empty;
    public bool TikTokForceIPv4 { get; set; } = true;
    public string TikTokImpersonate { get; set; } = "chrome-136:macos-15";
    public int AnalysisCacheMinutes { get; set; } = 15;
    public int InstagramRateLimitCooldownSeconds { get; set; } = 600;
    public int ThreadsResolveCacheMinutes { get; set; } = 10;
    public int ThreadsRateLimitCooldownSeconds { get; set; } = 300;
    public int RetentionHours { get; set; } = 6;
    public int MaxQueueLength { get; set; } = 20;
    public string MaxVideoSize { get; set; } = "2G";

    // Use the normal yt-dlp YouTube extractor and enable EJS challenge solving.
    // Do not force web_safari/HLS: that client can expose a different set of
    // formats and caused analysis to fail with "Requested format is not available".
    public bool YouTubeCompatibilityMode { get; set; } = true;
    public string YouTubePlayerClient { get; set; } = string.Empty;

    // Kept for backward-compatible configuration; format analysis no longer
    // filters YouTube to HLS-only streams.
    public bool YouTubeSafeFormatsOnly { get; set; } = false;
    public bool YouTubeEnableRemoteEjs { get; set; } = true;

    // Deno is available on the current Linux deployment and is detected by
    // yt-dlp as a working JS challenge runtime.
    public string YouTubeJavaScriptRuntime { get; set; } = "deno";

    // Cancel active downloads and schedule their files for deletion when the
    // user closes or leaves the downloader page.
    public bool DeleteOnSessionClose { get; set; } = true;

    // Allows a download response that was just started to finish before its
    // backing file is removed. Recommended range: 30-300 seconds.
    public int SessionCloseGraceSeconds { get; set; } = 60;

    // Frequency of disk cleanup. Recommended range: 10-300 seconds.
    public int CleanupIntervalSeconds { get; set; } = 30;
}
