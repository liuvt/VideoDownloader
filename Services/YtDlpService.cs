using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Models;
using VideoDownloader.Blazor.Options;

namespace VideoDownloader.Blazor.Services;

public sealed partial class YtDlpService
{
    private readonly DownloaderOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<YtDlpService> _logger;

    public YtDlpService(
        IOptions<DownloaderOptions> options,
        IWebHostEnvironment environment,
        ILogger<YtDlpService> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task DownloadAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath,
            _options.DownloadRoot));
        var jobDirectory = Path.Combine(root, job.Id.ToString("N"));
        Directory.CreateDirectory(jobDirectory);

        job.Status = DownloadJobStatus.ReadingMetadata;
        await ReadMetadataAsync(job, cancellationToken);

        job.Status = DownloadJobStatus.Downloading;
        job.Progress = 0;

        var outputTemplate = Path.Combine(jobDirectory, "media.%(ext)s");
        var arguments = BuildDownloadArguments(job, outputTemplate);
        string? finalPath = null;
        var errors = new Queue<string>();

        var exitCode = await RunProcessAsync(
            arguments,
            line =>
            {
                if (line.StartsWith("FILEPATH=", StringComparison.Ordinal))
                {
                    finalPath = line["FILEPATH=".Length..].Trim();
                }

                ParseProgress(job, line);
            },
            line =>
            {
                ParseProgress(job, line);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    errors.Enqueue(line.Trim());
                    while (errors.Count > 12)
                    {
                        errors.Dequeue();
                    }
                }
            },
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                errors.Count == 0
                    ? $"yt-dlp exited with code {exitCode}."
                    : string.Join(Environment.NewLine, errors));
        }

        job.Status = DownloadJobStatus.Processing;

        finalPath = ResolveFinalPath(finalPath, jobDirectory);
        if (finalPath is null)
        {
            throw new FileNotFoundException("The downloaded file could not be found.");
        }

        var safeTitle = SanitizeFileName(job.Title ?? "video");
        var extension = Path.GetExtension(finalPath);
        var destination = Path.Combine(jobDirectory, safeTitle + extension);

        if (!Path.GetFullPath(finalPath).Equals(
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            destination = GetUniquePath(destination);
            File.Move(finalPath, destination);
        }
        else
        {
            destination = finalPath;
        }

        job.FilePath = destination;
        job.FileName = Path.GetFileName(destination);
        job.Progress = 100;
        job.Speed = null;
        job.Eta = null;
        job.Status = DownloadJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
    }

    private async Task ReadMetadataAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var errors = new Queue<string>();
        var arguments = new List<string>
        {
            "--no-config",
            "--dump-single-json",
            "--skip-download",
            "--no-playlist",
            "--no-warnings"
        };

        AddCookiesArgument(arguments);
        arguments.Add(job.Url);

        var exitCode = await RunProcessAsync(
            arguments,
            line => stdout.AppendLine(line),
            line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    errors.Enqueue(line.Trim());
                    while (errors.Count > 8)
                    {
                        errors.Dequeue();
                    }
                }
            },
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                errors.Count == 0
                    ? "The video details could not be read."
                    : string.Join(Environment.NewLine, errors));
        }

        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;

        job.Title = GetString(root, "title") ?? "video";
        job.ThumbnailUrl = GetString(root, "thumbnail");
        job.DurationSeconds = GetInt64(root, "duration");
    }

    private List<string> BuildDownloadArguments(DownloadJob job, string outputTemplate)
    {
        var arguments = new List<string>
        {
            "--no-config",
            "--no-playlist",
            "--newline",
            "--max-filesize", _options.MaxVideoSize,
            "--progress-template",
            "download:PROGRESS=%(progress._percent_str)s|SPEED=%(progress._speed_str)s|ETA=%(progress._eta_str)s",
            "--print", "after_move:FILEPATH=%(filepath)s",
            "--output", outputTemplate
        };

        AddCookiesArgument(arguments);

        if (job.Kind == DownloadKind.Audio)
        {
            arguments.AddRange(new[]
            {
                "--extract-audio",
                "--audio-format", "mp3",
                "--audio-quality", "0"
            });
        }
        else
        {
            arguments.AddRange(new[]
            {
                "--format", GetVideoFormat(job.Quality),
                "--merge-output-format", "mp4"
            });
        }

        arguments.Add(job.Url);
        return arguments;
    }

    private void AddCookiesArgument(List<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(_options.CookiesFile))
        {
            return;
        }

        var cookiePath = Path.IsPathRooted(_options.CookiesFile)
            ? _options.CookiesFile
            : Path.Combine(_environment.ContentRootPath, _options.CookiesFile);

        if (File.Exists(cookiePath))
        {
            arguments.Add("--cookies");
            arguments.Add(cookiePath);
        }
    }

    private async Task<int> RunProcessAsync(
        IReadOnlyCollection<string> arguments,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveYtDlpPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
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
                throw new InvalidOperationException("yt-dlp could not be started.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"yt-dlp was not found at '{ResolveYtDlpPath()}'. Check Downloader:YtDlpPath in appsettings.json.", ex);
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The yt-dlp process could not be stopped.");
            }
        });

        var outputTask = ReadLinesAsync(process.StandardOutput, onOutput);
        var errorTask = ReadLinesAsync(process.StandardError, onError);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);
        return process.ExitCode;
    }

    private string ResolveYtDlpPath()
    {
        if (Path.IsPathRooted(_options.YtDlpPath))
        {
            return _options.YtDlpPath;
        }

        var localPath = Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath,
            _options.YtDlpPath));

        return File.Exists(localPath) ? localPath : _options.YtDlpPath;
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> callback)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            callback(line);
        }
    }

    private static void ParseProgress(DownloadJob job, string line)
    {
        var match = ProgressRegex().Match(line);
        if (!match.Success)
        {
            if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[ExtractAudio]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[VideoConvertor]", StringComparison.OrdinalIgnoreCase))
            {
                job.Status = DownloadJobStatus.Processing;
            }

            return;
        }

        if (double.TryParse(
                match.Groups["percent"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percent))
        {
            job.Progress = Math.Clamp(percent, 0, 100);
        }

        job.Speed = NormalizeProgressValue(match.Groups["speed"].Value);
        job.Eta = NormalizeProgressValue(match.Groups["eta"].Value);
    }

    private static string? NormalizeProgressValue(string value)
    {
        var normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized == "NA"
            ? null
            : normalized;
    }

    private static string GetVideoFormat(string quality) => quality switch
    {
        "360" => "bv*[height<=360]+ba/b[height<=360]/b",
        "480" => "bv*[height<=480]+ba/b[height<=480]/b",
        "720" => "bv*[height<=720]+ba/b[height<=720]/b",
        "1080" => "bv*[height<=1080]+ba/b[height<=1080]/b",
        "best" => "bv*+ba/b",
        _ => "bv*[height<=720]+ba/b[height<=720]/b"
    };

    private static string? ResolveFinalPath(string? reportedPath, string jobDirectory)
    {
        if (!string.IsNullOrWhiteSpace(reportedPath) && File.Exists(reportedPath))
        {
            var fullReported = Path.GetFullPath(reportedPath);
            var fullDirectory = Path.GetFullPath(jobDirectory) + Path.DirectorySeparatorChar;
            if (fullReported.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return fullReported;
            }
        }

        return Directory
            .EnumerateFiles(jobDirectory)
            .Where(path =>
                !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch)
            .ToArray();
        var result = new string(chars).Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(result))
        {
            result = "video";
        }

        return result.Length <= 120 ? result : result[..120].Trim();
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return null;
    }

    [GeneratedRegex(@"PROGRESS=\s*(?<percent>\d+(?:\.\d+)?)%\|SPEED=(?<speed>[^|]*)\|ETA=(?<eta>.*)$")]
    private static partial Regex ProgressRegex();
}
