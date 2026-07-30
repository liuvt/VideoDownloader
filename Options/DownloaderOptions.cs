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
}
