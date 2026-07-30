using System.IO.Compression;
using System.Security;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using VideoDownloader.Blazor.Models;
using VideoDownloader.Blazor.Options;
using VideoDownloader.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
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

builder.Services.AddSingleton<DownloadJobStore>();
builder.Services.AddSingleton<ProviderUrlValidator>();
builder.Services.AddSingleton<IVideoDownloadQueue, VideoDownloadQueue>();
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
    var content = $"User-agent: *\nAllow: /\nSitemap: {origin}/sitemap.xml\n";
    return Results.Text(content, "text/plain; charset=utf-8");
});

app.MapGet("/sitemap.xml", (HttpContext context, IOptions<SiteOptions> options) =>
{
    var origin = SecurityElement.Escape(GetPublicOrigin(context, options.Value));
    var content = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url>
            <loc>{origin}/</loc>
            <changefreq>weekly</changefreq>
            <priority>1.0</priority>
          </url>
        </urlset>
        """;
    return Results.Text(content, "application/xml; charset=utf-8");
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
