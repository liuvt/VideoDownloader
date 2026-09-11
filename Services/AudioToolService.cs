using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Models;
using VideoDownloader.Blazor.Options;

namespace VideoDownloader.Blazor.Services;

public sealed class AudioToolService
{
    private readonly AudioToolsOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AudioToolService> _logger;
    private readonly ConcurrentDictionary<Guid, AudioToolResult> _results = new();
    private readonly SemaphoreSlim _processGate;

    public AudioToolService(
        IOptions<AudioToolsOptions> options,
        IWebHostEnvironment environment,
        ILogger<AudioToolService> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _processGate = new SemaphoreSlim(Math.Clamp(_options.MaxConcurrentJobs, 1, 8));
    }

    public long MaxUploadBytes => Math.Max(1, _options.MaxUploadSizeMb) * 1024L * 1024L;
    public int MaxMergeFiles => Math.Clamp(_options.MaxMergeFiles, 2, 30);
    public int RetentionHours => Math.Clamp(_options.RetentionHours, 1, 24);

    public AudioToolResult? GetResult(Guid id) =>
        _results.TryGetValue(id, out var result) ? result : null;

    public async Task<AudioToolResult> ProcessAsync(
        AudioToolRequest request,
        IReadOnlyList<IBrowserFile> files,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request, files);

        var id = Guid.NewGuid();
        var workRoot = GetWorkRoot();
        var workDirectory = Path.Combine(workRoot, id.ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            var inputPaths = await SaveInputsAsync(files, workDirectory, cancellationToken);
            var outputName = BuildOutputName(request.Operation, id);
            var outputPath = Path.Combine(workDirectory, outputName);

            var args = BuildFfmpegArguments(request, inputPaths, outputPath);
            await _processGate.WaitAsync(cancellationToken);
            try
            {
                await RunFfmpegAsync(args, cancellationToken);
            }
            finally
            {
                _processGate.Release();
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1024)
            {
                throw new InvalidOperationException("FFmpeg did not create a valid MP3 result. Check the selected time range and try again.");
            }

            foreach (var inputPath in inputPaths)
            {
                TryDeleteFile(inputPath);
            }

            var result = new AudioToolResult(id, outputName, outputPath, DateTimeOffset.UtcNow);
            _results[id] = result;

            _logger.LogInformation(
                "Audio tool {Operation} completed as result {ResultId}.",
                request.Operation,
                id);

            return result;
        }
        catch
        {
            TryDeleteDirectory(workDirectory);
            throw;
        }
    }

    public void CleanupExpired()
    {
        var root = GetWorkRoot();
        Directory.CreateDirectory(root);

        var threshold = DateTimeOffset.UtcNow - TimeSpan.FromHours(RetentionHours);

        foreach (var pair in _results.ToArray())
        {
            if (pair.Value.CreatedAt > threshold)
            {
                continue;
            }

            if (_results.TryRemove(pair.Key, out var result))
            {
                TryDeleteDirectory(Path.GetDirectoryName(result.FilePath));
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                var lastWrite = new DateTimeOffset(
                    Directory.GetLastWriteTimeUtc(directory),
                    TimeSpan.Zero);

                if (lastWrite <= threshold)
                {
                    TryDeleteDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not inspect audio tool directory {Directory}.", directory);
            }
        }
    }

    private void ValidateRequest(AudioToolRequest request, IReadOnlyList<IBrowserFile> files)
    {
        if (request.Operation == AudioOperation.WhiteNoise)
        {
            if (request.WhiteNoiseDurationSeconds <= 0 || request.WhiteNoiseDurationSeconds > 43_200)
            {
                throw new InvalidOperationException("White noise duration must be between 1 second and 12 hours.");
            }

            if (request.WhiteNoiseIntensityPercent is < 1 or > 100)
            {
                throw new InvalidOperationException("White noise intensity must be between 1% and 100%.");
            }

            return;
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("Choose an MP3 file first.");
        }

        if (request.Operation == AudioOperation.Merge)
        {
            if (files.Count < 1)
            {
                throw new InvalidOperationException("Choose at least one MP3 file.");
            }

            if (files.Count > MaxMergeFiles)
            {
                throw new InvalidOperationException($"You can merge up to {MaxMergeFiles} MP3 files at a time.");
            }
        }
        else if (files.Count != 1)
        {
            throw new InvalidOperationException("This tool accepts one MP3 file at a time.");
        }

        var totalSize = files.Sum(file => file.Size);
        var maxTotalBytes = Math.Max(_options.MaxUploadSizeMb, _options.MaxTotalUploadSizeMb) * 1024L * 1024L;
        if (totalSize > maxTotalBytes)
        {
            throw new InvalidOperationException($"The selected files are too large. Maximum total size is {_options.MaxTotalUploadSizeMb} MB.");
        }

        foreach (var file in files)
        {
            if (!string.Equals(Path.GetExtension(file.Name), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only .mp3 files are supported on this page.");
            }

            if (file.Size <= 0 || file.Size > MaxUploadBytes)
            {
                throw new InvalidOperationException(
                    $"Each MP3 file must be smaller than {_options.MaxUploadSizeMb} MB.");
            }
        }

        if (request.Operation is AudioOperation.RemoveSegment or AudioOperation.ExtractSegment)
        {
            if (request.StartSeconds < 0 || request.EndSeconds <= request.StartSeconds)
            {
                throw new InvalidOperationException("The end time must be greater than the start time.");
            }
        }

        if ((request.Operation is AudioOperation.AdjustVolume or AudioOperation.Merge) &&
            request.VolumePercent is < 0 or > 400)
        {
            throw new InvalidOperationException("Volume must be between 0% and 400%.");
        }

        if ((request.Operation is AudioOperation.AdjustSpeed or AudioOperation.Merge) &&
            (request.SpeedFactor < 0.25d || request.SpeedFactor > 4d))
        {
            throw new InvalidOperationException("Playback speed must be between 0.25x and 4.00x.");
        }

        if (request.Operation == AudioOperation.Merge)
        {
            foreach (var edit in request.FileEdits)
            {
                if (edit.FileIndex < 0 || edit.FileIndex >= files.Count)
                {
                    throw new InvalidOperationException("One of the merge edit instructions is invalid.");
                }

                if (edit.KeptSegment is { } kept &&
                    (kept.StartSeconds < 0 || kept.EndSeconds <= kept.StartSeconds))
                {
                    throw new InvalidOperationException("Each kept interval must have an end time greater than its start time.");
                }

                foreach (var range in edit.RemovedSegments)
                {
                    if (range.StartSeconds < 0 || range.EndSeconds <= range.StartSeconds)
                    {
                        throw new InvalidOperationException("Each removed interval must have an end time greater than its start time.");
                    }
                }
            }
        }
    }

    private async Task<List<string>> SaveInputsAsync(
        IReadOnlyList<IBrowserFile> files,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>(files.Count);

        for (var index = 0; index < files.Count; index++)
        {
            var path = Path.Combine(workDirectory, $"input_{index + 1:00}.mp3");
            await using var source = files[index].OpenReadStream(MaxUploadBytes, cancellationToken);
            await using var target = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                useAsync: true);

            await source.CopyToAsync(target, 128 * 1024, cancellationToken);
            paths.Add(path);
        }

        return paths;
    }

    private static List<string> BuildFfmpegArguments(
        AudioToolRequest request,
        IReadOnlyList<string> inputPaths,
        string outputPath)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-y"
        };

        switch (request.Operation)
        {
            case AudioOperation.RemoveSegment:
                args.AddRange(["-i", inputPaths[0]]);
                args.AddRange([
                    "-filter_complex",
                    $"[0:a]aselect='not(between(t\\,{F(request.StartSeconds)}\\,{F(request.EndSeconds)}))',asetpts=N/SR/TB[outa]",
                    "-map", "[outa]",
                    "-vn",
                    "-c:a", "libmp3lame",
                    "-q:a", "2",
                    outputPath
                ]);
                break;

            case AudioOperation.ExtractSegment:
                args.AddRange([
                    "-ss", F(request.StartSeconds),
                    "-i", inputPaths[0],
                    "-t", F(request.EndSeconds - request.StartSeconds),
                    "-vn",
                    "-c:a", "libmp3lame",
                    "-q:a", "2",
                    outputPath
                ]);
                break;

            case AudioOperation.AdjustVolume:
                args.AddRange([
                    "-i", inputPaths[0],
                    "-vn",
                    "-af", $"volume={F(request.VolumePercent / 100d)}",
                    "-c:a", "libmp3lame",
                    "-q:a", "2",
                    outputPath
                ]);
                break;

            case AudioOperation.AdjustSpeed:
                args.AddRange([
                    "-i", inputPaths[0],
                    "-vn",
                    "-af", BuildAtempoFilter(request.SpeedFactor),
                    "-c:a", "libmp3lame",
                    "-q:a", "2",
                    outputPath
                ]);
                break;

            case AudioOperation.WhiteNoise:
                var amplitude = Math.Clamp(request.WhiteNoiseIntensityPercent / 100d, 0.01, 1d);
                args.AddRange([
                    "-f", "lavfi",
                    "-i", $"anoisesrc=color=white:amplitude={F(amplitude)}:duration={F(request.WhiteNoiseDurationSeconds)}:sample_rate=44100",
                    "-ac", "2",
                    "-c:a", "libmp3lame",
                    "-q:a", "4",
                    outputPath
                ]);
                break;

            case AudioOperation.Merge:
                foreach (var inputPath in inputPaths)
                {
                    args.AddRange(["-i", inputPath]);
                }

                var filter = new System.Text.StringBuilder();
                for (var i = 0; i < inputPaths.Count; i++)
                {
                    var edit = request.FileEdits.FirstOrDefault(x => x.FileIndex == i);
                    var ranges = NormalizeCutRanges(edit?.RemovedSegments ?? Array.Empty<AudioCutRange>());

                    filter.Append($"[{i}:a]");
                    if (edit?.KeptSegment is { } kept)
                    {
                        filter.Append($"atrim=start={F(kept.StartSeconds)}:end={F(kept.EndSeconds)},asetpts=N/SR/TB,");
                    }
                    else if (ranges.Count > 0)
                    {
                        var removeExpression = string.Join("+", ranges
                            .Select(range => $"between(t\\,{F(range.StartSeconds)}\\,{F(range.EndSeconds)})"));
                        filter.Append($"aselect='not({removeExpression})',asetpts=N/SR/TB,");
                    }

                    filter.Append($"aresample=44100,aformat=channel_layouts=stereo[a{i}];");
                }

                if (inputPaths.Count == 1)
                {
                    filter.Append("[a0]anull[joined];");
                }
                else
                {
                    var inputs = string.Concat(Enumerable.Range(0, inputPaths.Count).Select(i => $"[a{i}]"));
                    filter.Append($"{inputs}concat=n={inputPaths.Count}:v=0:a=1[joined];");
                }

                var outputFilters = new List<string>();
                if (Math.Abs(request.SpeedFactor - 1d) > 0.000001d)
                {
                    outputFilters.Add(BuildAtempoFilter(request.SpeedFactor));
                }

                if (request.VolumePercent != 100)
                {
                    outputFilters.Add($"volume={F(request.VolumePercent / 100d)}");
                }

                filter.Append(outputFilters.Count > 0
                    ? $"[joined]{string.Join(',', outputFilters)}[outa]"
                    : "[joined]anull[outa]");

                args.AddRange([
                    "-filter_complex", filter.ToString(),
                    "-map", "[outa]",
                    "-vn",
                    "-c:a", "libmp3lame",
                    "-q:a", "2",
                    outputPath
                ]);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request.Operation));
        }

        return args;
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("FFmpeg could not be started.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Unable to start FFmpeg at {FfmpegPath}.", startInfo.FileName);
            throw new InvalidOperationException("FFmpeg is not available on the server.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.ProcessTimeoutMinutes, 1, 60)));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException("Audio processing took too long and was stopped.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = await stderrTask;
        _ = await stdoutTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "FFmpeg audio processing failed with exit code {ExitCode}: {Error}",
                process.ExitCode,
                stderr);

            throw new InvalidOperationException("FFmpeg could not process this MP3 file. The file may be damaged or use an unsupported stream.");
        }
    }

    private string ResolveFfmpegPath()
    {
        if (string.IsNullOrWhiteSpace(_options.FfmpegPath))
        {
            return "ffmpeg";
        }

        if (Path.IsPathRooted(_options.FfmpegPath))
        {
            return _options.FfmpegPath;
        }

        var localPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, _options.FfmpegPath));
        return File.Exists(localPath) ? localPath : _options.FfmpegPath;
    }

    private string GetWorkRoot()
    {
        var configured = string.IsNullOrWhiteSpace(_options.WorkRoot)
            ? "App_Data/audio-tools"
            : _options.WorkRoot;

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configured));
    }

    private static string BuildOutputName(AudioOperation operation, Guid id)
    {
        var label = operation switch
        {
            AudioOperation.RemoveSegment => "remove-segment",
            AudioOperation.ExtractSegment => "extract-segment",
            AudioOperation.AdjustVolume => "volume",
            AudioOperation.AdjustSpeed => "speed",
            AudioOperation.WhiteNoise => "white-noise",
            AudioOperation.Merge => "merged",
            _ => "audio"
        };

        return $"clip2down-{label}-{id:N}.mp3";
    }

    private static string BuildAtempoFilter(double speedFactor)
    {
        var remaining = Math.Clamp(speedFactor, 0.25d, 4d);
        var filters = new List<string>();

        while (remaining < 0.5d - 0.000001d)
        {
            filters.Add("atempo=0.5");
            remaining /= 0.5d;
        }

        while (remaining > 2d + 0.000001d)
        {
            filters.Add("atempo=2");
            remaining /= 2d;
        }

        if (Math.Abs(remaining - 1d) > 0.000001d || filters.Count == 0)
        {
            filters.Add($"atempo={F(remaining)}");
        }

        return string.Join(',', filters);
    }

    private static List<AudioCutRange> NormalizeCutRanges(IReadOnlyList<AudioCutRange> ranges)
    {
        if (ranges.Count == 0)
        {
            return [];
        }

        var ordered = ranges
            .Where(x => x.StartSeconds >= 0 && x.EndSeconds > x.StartSeconds)
            .OrderBy(x => x.StartSeconds)
            .ThenBy(x => x.EndSeconds)
            .ToList();

        if (ordered.Count == 0)
        {
            return [];
        }

        var merged = new List<AudioCutRange> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var last = merged[^1];

            if (current.StartSeconds <= last.EndSeconds)
            {
                merged[^1] = new AudioCutRange(last.StartSeconds, Math.Max(last.EndSeconds, current.EndSeconds));
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var root = GetWorkRoot();
            var fullPath = Path.GetFullPath(path);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete audio tool directory {Directory}.", path);
        }
    }
}
