namespace VideoDownloader.Blazor.Options;

public sealed class AudioToolsOptions
{
    public const string SectionName = "AudioTools";

    // Use the FFmpeg already installed on the host. "ffmpeg" also works when
    // the binary is available in PATH.
    public string FfmpegPath { get; set; } = "ffmpeg";

    // Kept separate from video downloads because the video cleanup worker only
    // knows about yt-dlp jobs.
    public string WorkRoot { get; set; } = "App_Data/audio-tools";

    // Maximum size of each uploaded MP3 file.
    public int MaxUploadSizeMb { get; set; } = 250;

    // Maximum number of MP3 parts accepted by the merge tool.
    public int MaxMergeFiles { get; set; } = 10;

    // Prevent one merge request from filling the VPS disk with many large files.
    public int MaxTotalUploadSizeMb { get; set; } = 500;

    // Limit simultaneous FFmpeg processes to protect CPU/RAM on the VPS.
    public int MaxConcurrentJobs { get; set; } = 2;

    // Finished audio files are temporary and are deleted automatically.
    public int RetentionHours { get; set; } = 2;

    // Hard timeout for one FFmpeg operation.
    public int ProcessTimeoutMinutes { get; set; } = 15;
}
