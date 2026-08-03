namespace VideoDownloader.Blazor.Options;

public sealed class DownloaderOptions
{
    public const string SectionName = "Downloader";

    public string YtDlpPath { get; set; } = "yt-dlp";
    public string DownloadRoot { get; set; } = "App_Data/downloads";
    public string CookiesFile { get; set; } = string.Empty;
    public int RetentionHours { get; set; } = 6;
    public int MaxQueueLength { get; set; } = 20;
    public string MaxVideoSize { get; set; } = "2G";

    // Cancel active downloads and schedule their files for deletion when the
    // user closes or leaves the downloader page.
    public bool DeleteOnSessionClose { get; set; } = true;

    // Allows a download response that was just started to finish before its
    // backing file is removed. Recommended range: 30-300 seconds.
    public int SessionCloseGraceSeconds { get; set; } = 60;

    // Frequency of disk cleanup. Recommended range: 10-300 seconds.
    public int CleanupIntervalSeconds { get; set; } = 30;
}
