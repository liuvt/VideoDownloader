namespace VideoDownloader.Blazor.Models;

public enum AudioOperation
{
    RemoveSegment,
    ExtractSegment,
    AdjustVolume,
    AdjustSpeed,
    WhiteNoise,
    Merge
}

public sealed class AudioToolRequest
{
    public AudioOperation Operation { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public int VolumePercent { get; init; } = 100;
    public double SpeedFactor { get; init; } = 1d;
    public double WhiteNoiseDurationSeconds { get; init; } = 60;
    public int WhiteNoiseIntensityPercent { get; init; } = 20;
    public IReadOnlyList<AudioFileEdit> FileEdits { get; init; } = Array.Empty<AudioFileEdit>();
}

public sealed class AudioFileEdit
{
    public int FileIndex { get; init; }
    public IReadOnlyList<AudioCutRange> RemovedSegments { get; init; } = Array.Empty<AudioCutRange>();
    public AudioCutRange? KeptSegment { get; init; }
}

public sealed record AudioCutRange(double StartSeconds, double EndSeconds);

public sealed record AudioToolResult(
    Guid Id,
    string FileName,
    string FilePath,
    DateTimeOffset CreatedAt);
