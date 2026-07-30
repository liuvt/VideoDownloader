using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Models;
using VideoDownloader.Blazor.Options;

namespace VideoDownloader.Blazor.Services;

public sealed class DownloadCleanupService : BackgroundService
{
    private readonly DownloadJobStore _store;
    private readonly DownloaderOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DownloadCleanupService> _logger;

    public DownloadCleanupService(
        DownloadJobStore store,
        IOptions<DownloaderOptions> options,
        IWebHostEnvironment environment,
        ILogger<DownloadCleanupService> logger)
    {
        _store = store;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));

        do
        {
            Cleanup();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Cleanup()
    {
        var retention = TimeSpan.FromHours(Math.Clamp(_options.RetentionHours, 1, 168));
        var threshold = DateTimeOffset.UtcNow - retention;

        foreach (var job in _store.GetLatest(10_000))
        {
            if (job.Status is DownloadJobStatus.Downloading or
                DownloadJobStatus.Processing or
                DownloadJobStatus.ReadingMetadata or
                DownloadJobStatus.Queued)
            {
                continue;
            }

            if ((job.CompletedAt ?? job.CreatedAt) > threshold)
            {
                continue;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(job.FilePath))
                {
                    var directory = Path.GetDirectoryName(job.FilePath);
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }

                _store.Remove(job.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clean up download job {JobId}", job.Id);
            }
        }

        var root = Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath,
            _options.DownloadRoot));
        Directory.CreateDirectory(root);
    }
}
