namespace VideoDownloader.Blazor.Options;

public sealed class SiteOptions
{
    public const string SectionName = "Site";

    public string Name { get; set; } = "Clip2Down";
    public string BaseUrl { get; set; } = "https://clip2down.store";
    public string Description { get; set; } = "Download accessible YouTube, TikTok, Facebook, Instagram Reels, X, Reddit and Threads videos in MP4 up to 1080p, or extract MP3 with Clip2Down.";
    public string Language { get; set; } = "en-US";
}
