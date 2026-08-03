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
        var interval = TimeSpan.FromSeconds(
            Math.Clamp(_options.CleanupIntervalSeconds, 10, 300));

        Cleanup();

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Cleanup();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Cleanup()
    {
        var root = GetDownloadRoot();
        Directory.CreateDirectory(root);

        var retention = TimeSpan.FromHours(Math.Clamp(_options.RetentionHours, 1, 168));
        var retentionThreshold = DateTimeOffset.UtcNow - retention;
        var now = DateTimeOffset.UtcNow;

        foreach (var job in _store.GetLatest(10_000))
        {
            var sessionDeletionDue =
                job.DeleteAfter is not null && job.DeleteAfter <= now;

            var terminal = job.Status is
                DownloadJobStatus.Completed or
                DownloadJobStatus.Failed or
                DownloadJobStatus.Cancelled;

            var retentionDeletionDue =
                terminal && (job.CompletedAt ?? job.CreatedAt) <= retentionThreshold;

            if (!sessionDeletionDue && !retentionDeletionDue)
            {
                continue;
            }

            TryDeleteJob(root, job);
        }

        // The in-memory job store is empty after an application restart. Sweep
        // abandoned directories as a final safety net once normal retention has
        // elapsed.
        SweepOrphanDirectories(root, retentionThreshold);
    }

    private void TryDeleteJob(string root, DownloadJob job)
    {
        try
        {
            var directory = Path.GetFullPath(Path.Combine(root, job.Id.ToString("N")));
            EnsureUnderRoot(root, directory);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            _store.Remove(job.Id);

            _logger.LogInformation(
                "Deleted download data for job {JobId} from session {SessionId}.",
                job.Id,
                job.SessionId);
        }
        catch (IOException ex)
        {
            // A response may still be reading the file. The next cleanup pass
            // will retry after the file handle is released.
            _logger.LogDebug(ex, "Download job {JobId} is still in use; cleanup will retry.", job.Id);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "No permission to clean up download job {JobId}.", job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up download job {JobId}.", job.Id);
        }
    }

    private void SweepOrphanDirectories(string root, DateTimeOffset threshold)
    {
        HashSet<string> activeDirectories = _store
            .GetLatest(10_000)
            .Select(job => job.Id.ToString("N"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                var name = Path.GetFileName(directory);
                if (activeDirectories.Contains(name))
                {
                    continue;
                }

                var lastWrite = new DateTimeOffset(
                    Directory.GetLastWriteTimeUtc(directory),
                    TimeSpan.Zero);

                if (lastWrite > threshold)
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(directory);
                EnsureUnderRoot(root, fullPath);
                Directory.Delete(fullPath, recursive: true);

                _logger.LogInformation("Deleted orphaned download directory {Directory}.", fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not remove orphaned download directory {Directory}.", directory);
            }
        }
    }

    private string GetDownloadRoot() => Path.GetFullPath(Path.Combine(
        _environment.ContentRootPath,
        _options.DownloadRoot));

    private static void EnsureUnderRoot(string root, string candidate)
    {
        var normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The cleanup path is outside the download root.");
        }
    }
}
