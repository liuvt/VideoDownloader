using System.Collections.Concurrent;
using VideoDownloader.Blazor.Models;

namespace VideoDownloader.Blazor.Services;

public sealed class DownloadJobStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellation = new();

    public DownloadJob Create(
        Guid sessionId,
        string url,
        MediaFormatOption format,
        MediaAnalysisResult analysis)
    {
        var job = new DownloadJob
        {
            SessionId = sessionId,
            Url = url,
            Kind = format.Kind,
            Quality = format.QualityLabel,
            FormatSelector = format.FormatSelector,
            MetadataResolved = true,
            Title = analysis.Title,
            ThumbnailUrl = analysis.ThumbnailUrl,
            DurationSeconds = analysis.DurationSeconds
        };

        _jobs[job.Id] = job;
        _jobCancellation[job.Id] = new CancellationTokenSource();
        return job;
    }

    public DownloadJob? Get(Guid id) =>
        _jobs.TryGetValue(id, out var job) ? job : null;

    public CancellationToken GetCancellationToken(Guid id) =>
        _jobCancellation.TryGetValue(id, out var source)
            ? source.Token
            : new CancellationToken(canceled: true);

    public IReadOnlyList<DownloadJob> GetLatest(Guid sessionId, int count = 10) =>
        _jobs.Values
            .Where(x => x.SessionId == sessionId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .ToArray();

    public IReadOnlyList<DownloadJob> GetLatest(int count = 10) =>
        _jobs.Values
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .ToArray();

    public int CloseSession(Guid sessionId, TimeSpan deleteDelay)
    {
        var deleteAfter = DateTimeOffset.UtcNow + deleteDelay;
        var affected = 0;

        foreach (var job in _jobs.Values.Where(x => x.SessionId == sessionId))
        {
            affected++;

            // Keep the earliest requested deletion time when the close signal
            // arrives more than once (pagehide + circuit disposal).
            if (job.DeleteAfter is null || job.DeleteAfter > deleteAfter)
            {
                job.DeleteAfter = deleteAfter;
            }

            if (_jobCancellation.TryGetValue(job.Id, out var source))
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        return affected;
    }

    public bool Remove(Guid id)
    {
        var removed = _jobs.TryRemove(id, out _);

        if (_jobCancellation.TryRemove(id, out var source))
        {
            source.Dispose();
        }

        return removed;
    }

    public void Dispose()
    {
        foreach (var source in _jobCancellation.Values)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            source.Dispose();
        }

        _jobCancellation.Clear();
        _jobs.Clear();
    }
}
