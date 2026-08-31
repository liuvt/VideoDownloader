using System.ComponentModel.DataAnnotations;

namespace VideoDownloader.Blazor.Models;

public enum DownloadKind
{
    Video,
    Audio
}

public enum DownloadJobStatus
{
    Queued,
    ReadingMetadata,
    Downloading,
    Processing,
    Completed,
    Failed,
    Cancelled
}

public sealed class DownloadRequest
{
    [Required(ErrorMessage = "Enter a supported video URL.")]
    [StringLength(2048, ErrorMessage = "The URL is too long.")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// One concrete output that yt-dlp reported for the pasted URL.
/// The browser only renders entries returned by the analyzer, so users cannot
/// request a made-up 720p/1080p format that the source does not expose.
/// </summary>
public sealed record MediaFormatOption(
    string Key,
    DownloadKind Kind,
    string Label,
    string Detail,
    string QualityLabel,
    string FormatSelector,
    int? Height = null,
    long? ApproximateBytes = null);

public sealed record MediaAnalysisResult(
    string SourceUrl,
    string Title,
    string? ThumbnailUrl,
    long? DurationSeconds,
    IReadOnlyList<MediaFormatOption> Formats);

public sealed class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public required string Url { get; init; }
    public required DownloadKind Kind { get; init; }

    // Human-readable value shown in Download activity, e.g. "1080p" or "MP3".
    public required string Quality { get; init; }

    // Stable yt-dlp selector derived from the resolution shown by the analyzer.
    // Avoid storing transient platform format IDs that can change between requests.
    public required string FormatSelector { get; init; }
    public bool MetadataResolved { get; init; }

    public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;
    public double Progress { get; set; }
    public string? Title { get; set; }
    public string? ThumbnailUrl { get; set; }
    public long? DurationSeconds { get; set; }
    public string? Speed { get; set; }
    public string? Eta { get; set; }
    public string? ErrorMessage { get; set; }
    // Technical details are kept server-side and are only forwarded to the browser console.
    // Never render this value into the page UI.
    public string? TechnicalError { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public bool UsedDirectFallback { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    // Set when the browser session is closed. The cleanup worker removes the
    // job directory after a short grace period so an in-progress file response
    // can finish cleanly.
    public DateTimeOffset? DeleteAfter { get; set; }
}
