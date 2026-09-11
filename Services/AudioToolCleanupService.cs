namespace VideoDownloader.Blazor.Services;

public sealed class AudioToolCleanupService : BackgroundService
{
    private readonly AudioToolService _audioToolService;

    public AudioToolCleanupService(AudioToolService audioToolService)
    {
        _audioToolService = audioToolService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _audioToolService.CleanupExpired();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _audioToolService.CleanupExpired();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
