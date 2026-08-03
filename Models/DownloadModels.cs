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

    public DownloadKind Kind { get; set; } = DownloadKind.Video;

    public string Quality { get; set; } = "720";
}

public sealed class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public required string Url { get; init; }
    public required DownloadKind Kind { get; init; }
    public required string Quality { get; init; }

    public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;
    public double Progress { get; set; }
    public string? Title { get; set; }
    public string? ThumbnailUrl { get; set; }
    public long? DurationSeconds { get; set; }
    public string? Speed { get; set; }
    public string? Eta { get; set; }
    public string? ErrorMessage { get; set; }
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
