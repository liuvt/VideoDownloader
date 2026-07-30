# Clip2Down — Blazor Server Video Downloader

A .NET 8 Blazor Server application that downloads accessible YouTube and Facebook media through `yt-dlp` and uses FFmpeg for merging and audio conversion.

> Download only media you own, media you are authorized to save, or content distributed under a compatible license. The application does not bypass DRM, paywalls or platform access controls.

## What changed in this US/Los Angeles edition

- Entire public interface rewritten in US English.
- Distinct developer-tool visual direction inspired by GitHub Primer rather than a generic SaaS landing page.
- Local system font stack matching GitHub Primer: Mona Sans when available, followed by Apple/Windows system fonts.
- Monospace utility labels and terminal-style download preview.
- Responsive layout for desktop and mobile.
- SEO content for common US search intent: YouTube video downloader, Facebook video downloader, YouTube to MP3, YouTube Shorts downloader and Facebook Reels downloader.
- `en-US` HTML language, Open Graph locale and web manifest.
- Canonical URL and `hreflang="en-US"`.
- JSON-LD for `WebSite`, `WebApplication` and visible `FAQPage` content.
- US service area in structured data without pretending to be a physical local business.
- `robots.txt`, `sitemap.xml`, no-index headers on temporary download endpoints and a 1200×630 social image.

SEO cannot guarantee a number-one ranking. Search visibility also depends on domain authority, useful original content, crawlability, real-world performance, backlinks, competition and compliance with Google and platform policies.

## Windows setup

Place the required executables in the local `Tools` directory:

```text
Tools/
├── yt-dlp.exe
├── ffmpeg.exe
└── ffprobe.exe

dotnet publish .\VideoDownloader.Blazor.csproj -c Release -o .\publish
```

Download `yt-dlp.exe` from the official yt-dlp release page. Install FFmpeg with Winget or place the FFmpeg executables in `Tools`.

```powershell
cd D:\Dev\VideoDownloader

New-Item -ItemType Directory -Force .\Tools | Out-Null

Invoke-WebRequest `
  -Uri "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" `
  -OutFile ".\Tools\yt-dlp.exe"

winget install --id Gyan.FFmpeg -e

.\Tools\yt-dlp.exe --version
ffmpeg -version

dotnet restore
dotnet watch
```

The service now resolves a relative `Downloader:YtDlpPath` against the project content root first, so `Tools\\yt-dlp.exe` works even when the process launch directory changes.

When FFmpeg is stored in `Tools` instead of the Windows PATH, add that directory before running the app:

```powershell
$env:Path = "$(Resolve-Path .\Tools);$env:Path"
dotnet watch
```

## Ubuntu setup

```bash
sudo apt update
sudo apt install -y ffmpeg python3
sudo wget https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
  -O /usr/local/bin/yt-dlp
sudo chmod a+rx /usr/local/bin/yt-dlp
```

Override the Windows development path on Linux:

```bash
export Downloader__YtDlpPath=/usr/local/bin/yt-dlp
dotnet run --urls http://0.0.0.0:4123
```

## Production SEO configuration

Set the real HTTPS origin before deployment. Do not leave `BaseUrl` empty behind a reverse proxy unless forwarded headers are configured correctly.

```json
{
  "Site": {
    "Name": "Clip2Down",
    "BaseUrl": "https://your-domain.com",
    "Description": "Download public YouTube videos, YouTube Shorts, Facebook videos and Reels as MP4 or MP3 with a fast online video downloader built for US users.",
    "Language": "en-US"
  }
}
```

Or use environment variables:

```bash
export Site__BaseUrl=https://your-domain.com
export Site__Language=en-US
```

After deployment:

1. Verify `/robots.txt` and `/sitemap.xml` use the production HTTPS domain.
2. Add the domain property to Google Search Console.
3. Submit `/sitemap.xml` and request indexing for the home page.
4. Test structured data and inspect the rendered HTML source.
5. Measure Core Web Vitals with real field data.
6. Build useful supporting pages only when they provide unique, helpful content; do not create thin location or keyword doorway pages.

## Downloader configuration

```json
{
  "Downloader": {
    "YtDlpPath": "Tools\\yt-dlp.exe",
    "DownloadRoot": "App_Data/downloads",
    "CookiesFile": "",
    "RetentionHours": 6,
    "MaxQueueLength": 20,
    "MaxVideoSize": "2G"
  }
}
```

For private or login-required media, configure a protected Netscape cookie file on the server. Never allow public users to upload account cookies through the website.

## Production hardening checklist

- Add authentication or per-user download ownership.
- Add ASP.NET Core rate limiting by IP and user.
- Apply bandwidth, file-size, duration and daily quota limits.
- Store jobs in a database when history must survive restarts.
- Run downloads in a dedicated worker for multi-instance deployment.
- Keep `yt-dlp` updated because source platforms change frequently.
- Review YouTube, Facebook, copyright and local legal requirements before opening the service publicly.
