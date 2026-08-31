using System.IO.Compression;
using System.Security;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Models;
using VideoDownloader.Blazor.Options;
using VideoDownloader.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// Giữ trạng thái circuit lâu hơn khi người dùng chuyển tab.
builder.Services
    .AddServerSideBlazor(options =>
    {
        // Giữ trạng thái circuit lâu hơn khi người dùng chuyển tab.
        options.DisconnectedCircuitRetentionPeriod =
            TimeSpan.FromMinutes(15);

        // Số circuit bị ngắt tối đa được giữ lại.
        options.DisconnectedCircuitMaxRetained = 200;

        options.JSInteropDefaultCallTimeout =
            TimeSpan.FromMinutes(2);
    })
    .AddHubOptions(options =>
    {
        // Server đợi client lâu hơn trước khi xác định đã mất kết nối.
        options.ClientTimeoutInterval =
            TimeSpan.FromSeconds(60);

        options.HandshakeTimeout =
            TimeSpan.FromSeconds(30);

        // Phải khớp với client: 15 giây.
        options.KeepAliveInterval =
            TimeSpan.FromSeconds(15);
    });

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/manifest+json",
        "application/xml",
        "image/svg+xml"
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);

builder.Services.Configure<DownloaderOptions>(
    builder.Configuration.GetSection(DownloaderOptions.SectionName));
builder.Services.Configure<SiteOptions>(
    builder.Configuration.GetSection(SiteOptions.SectionName));

builder.Services.AddHttpClient("ThreadsResolver", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient("FacebookStoryResolver", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddSingleton<DownloadJobStore>();
builder.Services.AddSingleton<ProviderUrlValidator>();
builder.Services.AddSingleton<IVideoDownloadQueue, VideoDownloadQueue>();
builder.Services.AddSingleton<ThreadsMediaResolver>();
builder.Services.AddSingleton<FacebookStoryResolver>();
builder.Services.AddSingleton<YtDlpService>();
builder.Services.AddHostedService<VideoDownloadWorker>();
builder.Services.AddHostedService<DownloadCleanupService>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "public,max-age=604800";
    }
});
app.UseRouting();

app.MapGet("/robots.txt", (HttpContext context, IOptions<SiteOptions> options) =>
{
    var origin = GetPublicOrigin(context, options.Value);
    var content = $"User-agent: *\nAllow: /\nDisallow: /downloads/\nSitemap: {origin}/sitemap.xml\n";
    return Results.Text(content, "text/plain; charset=utf-8");
});

app.MapGet("/sitemap.xml", (HttpContext context, IOptions<SiteOptions> options) =>
{
    var origin = GetPublicOrigin(context, options.Value);
    var lastModified = File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location)
        .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    var pages = new[]
    {
        new SitemapPage("/", "daily", "1.0", true),
        new SitemapPage("/youtube-video-downloader", "weekly", "0.9", false),
        new SitemapPage("/facebook-video-downloader", "weekly", "0.9", false),
        new SitemapPage("/tiktok-video-downloader", "weekly", "0.9", false),
        new SitemapPage("/instagram-video-downloader", "weekly", "0.9", false),
        new SitemapPage("/twitter-video-downloader", "weekly", "0.9", false),
        new SitemapPage("/reddit-video-downloader", "weekly", "0.9", false),
        new SitemapPage("/threads-video-downloader", "weekly", "0.9", false)
    };

    var xml = new StringBuilder();
    xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    xml.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" xmlns:image=\"http://www.google.com/schemas/sitemap-image/1.1\">");

    foreach (var page in pages)
    {
        var location = SecurityElement.Escape($"{origin}{page.Path}");
        xml.AppendLine("  <url>");
        xml.AppendLine($"    <loc>{location}</loc>");
        xml.AppendLine($"    <lastmod>{lastModified}</lastmod>");
        xml.AppendLine($"    <changefreq>{page.ChangeFrequency}</changefreq>");
        xml.AppendLine($"    <priority>{page.Priority}</priority>");

        if (page.IncludeSocialImage)
        {
            var imageLocation = SecurityElement.Escape($"{origin}/images/og-video-downloader.png");
            xml.AppendLine("    <image:image>");
            xml.AppendLine($"      <image:loc>{imageLocation}</image:loc>");
            xml.AppendLine("      <image:title>Clip2Down online video downloader</image:title>");
            xml.AppendLine("    </image:image>");
        }

        xml.AppendLine("  </url>");
    }

    xml.AppendLine("</urlset>");

    context.Response.Headers.CacheControl = "public,max-age=3600";
    return Results.Text(xml.ToString(), "application/xml; charset=utf-8");
});

app.MapPost("/api/sessions/{sessionId:guid}/close", (
    Guid sessionId,
    DownloadJobStore store,
    IOptions<DownloaderOptions> options) =>
{
    if (options.Value.DeleteOnSessionClose)
    {
        var grace = TimeSpan.FromSeconds(
            Math.Clamp(options.Value.SessionCloseGraceSeconds, 0, 600));

        store.CloseSession(sessionId, grace);
    }

    return Results.NoContent();
});

app.MapGet("/downloads/{id:guid}", (
    Guid id,
    HttpContext context,
    DownloadJobStore store,
    IOptions<DownloaderOptions> options) =>
{
    context.Response.Headers.Append("X-Robots-Tag", "noindex, nofollow, noarchive");
    context.Response.Headers.CacheControl = "private,no-store";

    var job = store.Get(id);
    if (job is null || job.Status != DownloadJobStatus.Completed ||
        string.IsNullOrWhiteSpace(job.FilePath) || !File.Exists(job.FilePath))
    {
        return Results.NotFound();
    }

    var root = Path.GetFullPath(Path.Combine(
        app.Environment.ContentRootPath,
        options.Value.DownloadRoot));
    var filePath = Path.GetFullPath(job.FilePath);

    var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
        ? root
        : root + Path.DirectorySeparatorChar;

    if (!filePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Invalid file path.");
    }

    return Results.File(
        filePath,
        GetContentType(filePath),
        job.FileName,
        enableRangeProcessing: true);
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

static string GetPublicOrigin(HttpContext context, SiteOptions site)
{
    if (!string.IsNullOrWhiteSpace(site.BaseUrl))
    {
        return site.BaseUrl.TrimEnd('/');
    }

    return $"{context.Request.Scheme}://{context.Request.Host}";
}

static string GetContentType(string path) =>
    Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        _ => "application/octet-stream"
    };

internal sealed record SitemapPage(string Path, string ChangeFrequency, string Priority, bool IncludeSocialImage);
