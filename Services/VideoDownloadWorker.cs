using VideoDownloader.Blazor.Models;

namespace VideoDownloader.Blazor.Services;

public sealed class VideoDownloadWorker : BackgroundService
{
    private readonly IVideoDownloadQueue _queue;
    private readonly DownloadJobStore _store;
    private readonly YtDlpService _downloader;
    private readonly ILogger<VideoDownloadWorker> _logger;

    public VideoDownloadWorker(
        IVideoDownloadQueue queue,
        DownloadJobStore store,
        YtDlpService downloader,
        ILogger<VideoDownloadWorker> logger)
    {
        _queue = queue;
        _store = store;
        _downloader = downloader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.ReadAllAsync(stoppingToken))
        {
            var job = _store.Get(jobId);
            if (job is null)
            {
                continue;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                _store.GetCancellationToken(job.Id));

            try
            {
                await _downloader.DownloadAsync(job, linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // The browser session was closed. yt-dlp is killed through the
                // linked token and the cleanup service removes partial files.
                job.Status = DownloadJobStatus.Cancelled;
                job.ErrorMessage = null;
                job.TechnicalError = null;
                job.Speed = null;
                job.Eta = null;
                job.CompletedAt = DateTimeOffset.UtcNow;

                _logger.LogInformation(
                    "Download job {JobId} was cancelled because session {SessionId} closed.",
                    job.Id,
                    job.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not download media for job {JobId}", job.Id);
                job.Status = DownloadJobStatus.Failed;
                job.ErrorMessage = "Unable to download this video. Please try another video or try again later.";
                job.TechnicalError = ToTechnicalMessage(ex);
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private static string ToTechnicalMessage(Exception ex)
    {
        var message = ex.Message.Trim();
        return message.Length <= 8000 ? message : message[..8000];
    }
}
