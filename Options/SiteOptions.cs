namespace VideoDownloader.Blazor.Options;

public sealed class SiteOptions
{
    public const string SectionName = "Site";

    public string Name { get; set; } = "ClipCurrent";
    public string BaseUrl { get; set; } = string.Empty;
    public string Description { get; set; } = "Download public YouTube and Facebook videos as MP4 or MP3 with a fast online video downloader for US users.";
    public string Language { get; set; } = "en-US";
}
