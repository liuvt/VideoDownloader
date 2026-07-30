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

            try
            {
                await _downloader.DownloadAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not download media for job {JobId}", job.Id);
                job.Status = DownloadJobStatus.Failed;
                job.ErrorMessage = ToFriendlyMessage(ex);
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private static string ToFriendlyMessage(Exception ex)
    {
        var message = ex.Message.Trim();
        return message.Length <= 800 ? message : message[..800];
    }
}
