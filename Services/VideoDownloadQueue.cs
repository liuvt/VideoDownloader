using System.Threading.Channels;
using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Options;

namespace VideoDownloader.Blazor.Services;

public interface IVideoDownloadQueue
{
    ValueTask QueueAsync(Guid jobId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class VideoDownloadQueue : IVideoDownloadQueue
{
    private readonly Channel<Guid> _channel;

    public VideoDownloadQueue(IOptions<DownloaderOptions> options)
    {
        var capacity = Math.Clamp(options.Value.MaxQueueLength, 1, 500);
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask QueueAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!_channel.Writer.TryWrite(jobId))
        {
            throw new InvalidOperationException("The download queue is full. Try again in a moment.");
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
