using System.Collections.Concurrent;
using VideoDownloader.Blazor.Models;

namespace VideoDownloader.Blazor.Services;

public sealed class DownloadJobStore
{
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();

    public DownloadJob Create(string url, DownloadKind kind, string quality)
    {
        var job = new DownloadJob
        {
            Url = url,
            Kind = kind,
            Quality = quality
        };

        _jobs[job.Id] = job;
        return job;
    }

    public DownloadJob? Get(Guid id) =>
        _jobs.TryGetValue(id, out var job) ? job : null;

    public IReadOnlyList<DownloadJob> GetLatest(int count = 10) =>
        _jobs.Values
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .ToArray();

    public bool Remove(Guid id) => _jobs.TryRemove(id, out _);
}
